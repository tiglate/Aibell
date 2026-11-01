using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Audiobooks;
using Aibell.Application.Library;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Audiobooks;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Audiobooks;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class AudiobookChaptersWindowTests
{
    private string directory = null!;
    private string book = null!;
    private readonly List<AudiobooksTestWindow> windows = [];
    private readonly List<SqliteTrackRepository> repositories = [];
    private Handoff handoff = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-audiobook-chapters-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        book = Path.Combine(directory, "Dracula.mp3");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "fixture.mp3"), book);
        handoff = new();
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (AudiobooksTestWindow window in windows) { await window.ShutdownAsync(); window.Close(); }
        foreach (SqliteTrackRepository repository in repositories) await repository.DisposeAsync();
        windows.Clear(); repositories.Clear();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }

    private void WriteChapters(params (string Title, uint Start)[] chapters)
    {
        ATL.Track track = new(book);
        track.Chapters.Clear();
        foreach ((string title, uint start) in chapters) track.Chapters.Add(new ATL.ChapterInfo { Title = title, StartTime = start });
        Assert.That(track.Save(), Is.True);
    }

    [AvaloniaTest]
    public async Task ChapterFlow_BrowseSelectAndPlay_UsesTheSharedHandoffOnlyOnExplicitPlayAndLeavesTheFileAlone()
    {
        WriteChapters(("Opening", 0), ("The Journey", 500), ("Finale", 900));
        byte[] before = SHA256.HashData(File.ReadAllBytes(book));
        (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(new AtlAudiobookChapterReader());
        await viewModel.AddAsync(new Uri(book));
        await WaitAsync(() => viewModel.SelectedItem is not null);

        Click("ShowChapters", window);
        await WaitAsync(() => viewModel.Chapters.Count == 3);
        Assert.That(viewModel.Chapters.Select(c => c.Title), Is.EqualTo(["Opening", "The Journey", "Finale"]));
        Assert.That(Enabled("PlayChapter", window), Is.False, "no chapter chosen yet");
        Assert.That(Enabled("PlayAudiobook", window), Is.True, "ordinary Play stays available");

        viewModel.SelectedChapter = viewModel.Chapters[1];
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Assert.That(handoff.Requests, Is.Empty, "choosing a chapter must not start playback");
        Assert.That(Enabled("PlayChapter", window), Is.True);

        Click("PlayChapter", window);
        await WaitAsync(() => handoff.Requests.Count == 1);
        Assert.That(handoff.Requests[0].StartPosition, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        Assert.That(handoff.Requests[0].Title, Is.EqualTo("Dracula"));
        Assert.That(viewModel.SelectedItem!.Entry.HasSavedPosition, Is.False, "playing a chapter saves nothing");
        Assert.That(SHA256.HashData(File.ReadAllBytes(book)), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task Chapters_BookWithoutChapters_ShowsEmptyStateAndKeepsOrdinaryPlay()
    {
        WriteChapters();
        (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(new AtlAudiobookChapterReader());
        await viewModel.AddAsync(new Uri(book));

        Click("ShowChapters", window);
        await WaitAsync(() => viewModel.ChaptersVisible);

        Assert.That(viewModel.ChaptersMessage, Does.Contain("no chapters"));
        Assert.That(viewModel.Chapters, Is.Empty);
        Assert.That(Enabled("PlayChapter", window), Is.False);
        Click("PlayAudiobook", window);
        await WaitAsync(() => handoff.Requests.Count == 1);
        Assert.That(handoff.Requests[0].StartPosition, Is.EqualTo(TimeSpan.Zero));
    }

    [AvaloniaTest]
    public async Task Chapters_NoChapterReaderConfigured_HidesTheChapterActionButKeepsPlay()
    {
        (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(null);
        await viewModel.AddAsync(new Uri(book));

        Assert.That(Enabled("ShowChapters", window), Is.False);
        Assert.That(Enabled("PlayAudiobook", window), Is.True);
    }

    [AvaloniaTest]
    public async Task Chapters_SelectionChangesWhileReading_StaleResultIsDiscarded()
    {
        GatedChapters gated = new();
        try
        {
            (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(gated);
            await viewModel.AddAsync(new Uri(book));
            Click("ShowChapters", window);
            await WaitAsync(() => gated.Started);

            viewModel.SelectedItem = null;
            gated.Release.SetResult(new(TimeSpan.FromSeconds(1), [new(TimeSpan.Zero, "Late")]));
            await WaitAsync(() => !viewModel.IsBusy);

            Assert.That(viewModel.Chapters, Is.Empty);
            Assert.That(viewModel.ChaptersVisible, Is.False);
            Assert.That(handoff.Requests, Is.Empty);
        }
        finally
        {
            gated.Release.TrySetResult(new(TimeSpan.FromSeconds(1), []));
        }
    }

    [AvaloniaTest]
    public async Task Chapters_BookChangesBetweenListingAndPlay_RefusesAndDoesNotStartPlayback()
    {
        WriteChapters(("One", 0), ("Two", 500));
        (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(new AtlAudiobookChapterReader());
        await viewModel.AddAsync(new Uri(book));
        Click("ShowChapters", window);
        await WaitAsync(() => viewModel.Chapters.Count == 2);
        viewModel.SelectedChapter = viewModel.Chapters[1];
        WriteChapters(("Rewritten", 0));

        Click("PlayChapter", window);
        await WaitAsync(() => Status(window).Contains("Could not play the chapter", StringComparison.Ordinal));

        Assert.That(Status(window), Does.Contain("changed"));
        Assert.That(handoff.Requests, Is.Empty);
    }

    [AvaloniaTest]
    public async Task Chapters_CancelWhileReaderIgnoresCancellation_DrainsBeforeReturningToIdle()
    {
        GatedChapters gated = new();
        try
        {
            (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(gated);
            await viewModel.AddAsync(new Uri(book));
            Click("ShowChapters", window);
            await WaitAsync(() => gated.Started);

            Click("CancelAudiobookOperation", window);
            Assert.That(viewModel.IsBusy, Is.True, "accepted reads remain owned until they finish");
            gated.Release.SetResult(new(TimeSpan.FromSeconds(1), [new(TimeSpan.Zero, "Late")]));
            await WaitAsync(() => !viewModel.IsBusy);

            Assert.That(Status(window), Is.EqualTo("Cancelled."));
            Assert.That(viewModel.Chapters, Is.Empty);
        }
        finally
        {
            gated.Release.TrySetResult(new(TimeSpan.FromSeconds(1), []));
        }
    }

    [AvaloniaTest]
    public async Task Close_WhileReadingChapters_DrainsTheAcceptedReader()
    {
        GatedChapters gated = new();
        try
        {
            (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(gated);
            await viewModel.AddAsync(new Uri(book));
            Click("ShowChapters", window);
            await WaitAsync(() => gated.Started);

            Task closing = window.ShutdownAsync();
            bool wasDraining = !closing.IsCompleted;
            gated.Release.SetResult(new(TimeSpan.FromSeconds(1), [new(TimeSpan.Zero, "Late")]));
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(wasDraining, Is.True, "close must await the accepted file read");

            Assert.That(viewModel.Chapters, Is.Empty);
            Assert.That(handoff.Requests, Is.Empty);
        }
        finally
        {
            gated.Release.TrySetResult(new(TimeSpan.FromSeconds(1), []));
        }
    }

    [AvaloniaTest]
    public async Task Chapters_MinimumWindow_KeyboardSelectionAndExplicitPlayRemainReachable()
    {
        WriteChapters(("Opening", 0), ("The Journey", 500));
        (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(new AtlAudiobookChapterReader());
        window.Width = 620;
        window.Height = 380;
        await viewModel.AddAsync(new Uri(book));
        await viewModel.ShowChaptersAsync(viewModel.SelectedItem);
        Dispatcher.UIThread.RunJobs();
        ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "ChapterList");
        Button play = window.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "PlayChapter");
        list.SelectedIndex = 0;
        Assert.That(list.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab), Is.True);
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.That(viewModel.SelectedChapter?.Chapter.Start, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        Assert.That(handoff.Requests, Is.Empty, "arrow selection must not play");
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.That(play.IsKeyboardFocusWithin, Is.True);
        Assert.That(play.Bounds.Width, Is.GreaterThan(0));
        Assert.That(play.Bounds.Height, Is.GreaterThan(0));
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitAsync(() => handoff.Requests.Count == 1);
        Assert.That(handoff.Requests[0].StartPosition, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
    }

    [AvaloniaTest]
    public async Task PlayChapter_SelectionChangesDuringFreshRead_DoesNotStartTheOldBook()
    {
        RecheckingChapters reader = new();
        try
        {
            (_, AudiobooksViewModel viewModel) = await OpenAsync(reader);
            await viewModel.AddAsync(new Uri(book));
            await viewModel.ShowChaptersAsync(viewModel.SelectedItem);
            viewModel.SelectedChapter = viewModel.Chapters[0];
            Task playing = viewModel.PlayChapterAsync();
            Assert.That(reader.Reads, Is.EqualTo(2), "play must re-read before handoff");

            viewModel.SelectedItem = null;
            reader.Release.SetResult(reader.Listing);
            await playing;

            Assert.That(handoff.Requests, Is.Empty, "changing the selected book must cancel the pending chapter play");
            Assert.That(viewModel.Chapters, Is.Empty);
            Assert.That(viewModel.ChaptersVisible, Is.False);
        }
        finally
        {
            reader.Release.TrySetResult(reader.Listing);
        }
    }

    [AvaloniaTest]
    public async Task PlayChapter_ChapterSelectionChangesDuringFreshRead_DoesNotStartTheOldChapter()
    {
        RecheckingChapters reader = new();
        try
        {
            (_, AudiobooksViewModel viewModel) = await OpenAsync(reader);
            await viewModel.AddAsync(new Uri(book));
            await viewModel.ShowChaptersAsync(viewModel.SelectedItem);
            viewModel.SelectedChapter = viewModel.Chapters[0];
            Task playing = viewModel.PlayChapterAsync();
            Assert.That(reader.Reads, Is.EqualTo(2));

            viewModel.SelectedChapter = viewModel.Chapters[1];
            reader.Release.SetResult(reader.Listing);
            await playing;

            Assert.That(handoff.Requests, Is.Empty, "a pending play belongs to its originally selected chapter");
            Assert.That(viewModel.SelectedChapter, Is.SameAs(viewModel.Chapters[1]));
        }
        finally
        {
            reader.Release.TrySetResult(reader.Listing);
        }
    }

    [AvaloniaTest]
    public async Task PlayChapter_CloseDuringFreshRead_DrainsAndNeverStartsAfterReaderCompletes()
    {
        RecheckingChapters reader = new();
        try
        {
            (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(reader);
            await viewModel.AddAsync(new Uri(book));
            await viewModel.ShowChaptersAsync(viewModel.SelectedItem);
            viewModel.SelectedChapter = viewModel.Chapters[0];
            Task playing = viewModel.PlayChapterAsync();
            Assert.That(reader.Reads, Is.EqualTo(2));
            string status = viewModel.StatusMessage;

            Task closing = window.ShutdownAsync();
            bool wasDraining = !closing.IsCompleted;
            Assert.That(reader.LastToken.IsCancellationRequested, Is.True,
                "shutdown must signal the accepted operation before a reader can race completion");
            reader.Release.SetResult(reader.Listing);
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(wasDraining, Is.True, "close must own the playback re-read too");
            await playing;
            Dispatcher.UIThread.RunJobs();

            Assert.That(handoff.Requests, Is.Empty);
            Assert.That(viewModel.StatusMessage, Is.EqualTo(status), "no late status mutation after close");
        }
        finally
        {
            reader.Release.TrySetResult(reader.Listing);
        }
    }

    private sealed class RecheckingChapters : IAudiobookChapterReader
    {
        public int Reads { get; private set; }
        public AudiobookChapterListing Listing { get; } = new(TimeSpan.FromSeconds(1), [new(TimeSpan.Zero, "Opening"), new(TimeSpan.FromMilliseconds(500), "Later")]);
        public TaskCompletionSource<AudiobookChapterListing> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LastToken { get; private set; }
        public Task<AudiobookChapterListing> ReadAsync(Uri file, CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
            return ++Reads == 1 ? Task.FromResult(Listing) : Release.Task;
        }
    }

    private async Task<(AudiobooksTestWindow, AudiobooksViewModel)> OpenAsync(IAudiobookChapterReader? chapters)
    {
        SqliteTrackRepository repository = new(Path.Combine(directory, "library.db"));
        repositories.Add(repository);
        AudiobookShelfService service = new(repository, new Reader(), new AudiobookFiles(), null, chapters);
        AudiobooksViewModel viewModel = new(service, handoff);
        AudiobooksTestWindow window = new(viewModel, null); windows.Add(window);
        string initialStatus = viewModel.StatusMessage;
        window.Show();
        await WaitAsync(() => !viewModel.IsBusy && viewModel.StatusMessage != initialStatus);
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel);
    }

    private static string Status(Window window) => window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "AudiobookStatus").Text ?? "";
    private static bool Enabled(string name, Window window) => window.GetVisualDescendants().OfType<Button>().First(b => b.Name == name).IsEnabled;

    private static void Click(string name, Window window)
    {
        Dispatcher.UIThread.RunJobs();
        Button button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == name && b.IsVisible);
        Assert.That(button.IsEnabled, Is.True, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for the Audiobooks window.");
            Dispatcher.UIThread.RunJobs(); await Task.Delay(20);
        }
    }

    private sealed class Reader : IMediaMetadataReader
    {
        public Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaMetadata("Dracula", "Bram Stoker", "", TimeSpan.FromSeconds(1)));
    }

    private sealed class GatedChapters : IAudiobookChapterReader
    {
        public bool Started { get; private set; }
        public TaskCompletionSource<AudiobookChapterListing> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AudiobookChapterListing> ReadAsync(Uri file, CancellationToken cancellationToken = default) { Started = true; return Release.Task; }
    }

    private sealed class Handoff : IAudiobookPlaybackHandoff
    {
        public List<AudiobookPlaybackRequest> Requests { get; } = [];
        public Task PlayAsync(AudiobookPlaybackRequest request, CancellationToken cancellationToken = default) { Requests.Add(request); return Task.CompletedTask; }
        public ValueTask<AudiobookPlaybackSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<AudiobookPlaybackSnapshot?>(null);
    }
}
