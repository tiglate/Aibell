using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Audiobooks;
using Aibell.Application.Library;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Audiobooks;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class AudiobooksTestWindowTests
{
    private string directory = null!;
    private string book = null!;
    private readonly List<AudiobooksTestWindow> windows = [];
    private readonly List<SqliteTrackRepository> repositories = [];
    private ShelfReader reader = null!;
    private ShelfHandoff handoff = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-audiobook-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        book = Path.Combine(directory, "Dracula.mp3");
        File.WriteAllBytes(book, [7, 7, 7]);
        reader = new(new MediaMetadata("Dracula", "Bram Stoker", "", TimeSpan.FromHours(15)));
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

    [AvaloniaTest]
    public async Task ShelfFlow_AddPlaySaveRemove_PersistsAcrossReopenAndNeverTouchesTheAudioFile()
    {
        byte[] before = SHA256.HashData(File.ReadAllBytes(book));
        DelayedFilePicker picker = new(new Uri(book)); picker.Release.SetResult();
        (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(picker);
        Assert.That(Status(window), Does.Contain("empty"));
        Assert.That(Enabled("ResumeAudiobook", window), Is.False);

        Click("AddAudiobook", window);
        await WaitAsync(() => viewModel.Items.Count == 1);
        Assert.That(Status(window), Does.Contain("Added"));
        Assert.That(viewModel.SelectedItem!.StateText, Is.EqualTo("No saved position"));
        Assert.That(Enabled("PlayAudiobook", window), Is.True);
        Assert.That(Enabled("ResumeAudiobook", window), Is.False, "nothing saved yet");

        Click("PlayAudiobook", window);
        await WaitAsync(() => handoff.Requests.Count == 1);
        Assert.That(handoff.Requests[0].StartPosition, Is.EqualTo(TimeSpan.Zero));
        Assert.That(handoff.Requests[0].Title, Is.EqualTo("Dracula"));

        handoff.Current = new(new Uri(book), new TimeSpan(3, 2, 1), true);
        Click("SaveAudiobookPosition", window);
        await WaitAsync(() => Status(window).StartsWith("Saved position", StringComparison.Ordinal));
        Assert.That(viewModel.SelectedItem.StateText, Is.EqualTo("Saved 3:02:01 of 15:00:00"));
        Assert.That(Enabled("ResumeAudiobook", window), Is.True);

        await window.ShutdownAsync(); window.Close();
        (AudiobooksTestWindow reopened, AudiobooksViewModel again) = await OpenAsync(picker);
        await WaitAsync(() => again.Items.Count == 1);
        again.SelectedItem = again.Items[0];
        Click("ResumeAudiobook", reopened);
        await WaitAsync(() => handoff.Requests.Count == 2);
        Assert.That(handoff.Requests[1].StartPosition, Is.EqualTo(new TimeSpan(3, 2, 1)), "resume seeks to the saved position");

        Click("RemoveAudiobook", reopened);
        Window confirm = await WaitForDialogAsync(reopened);
        Click("ConfirmRemoveAudiobook", confirm);
        await WaitAsync(() => again.Items.Count == 0);
        Assert.That(SHA256.HashData(File.ReadAllBytes(book)), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task Play_FileMovedAfterAdding_ShowsActionableErrorAndMarksTheRowMissing()
    {
        (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(null);
        await viewModel.AddAsync(new Uri(book));
        File.Delete(book);
        Click("PlayAudiobook", window);
        await WaitAsync(() => Status(window).Contains("missing", StringComparison.Ordinal));
        Assert.That(Status(window), Does.Contain("Could not play").And.Contain("Remove"));
        Assert.That(viewModel.SelectedItem!.CanPlay, Is.False);
        Assert.That(handoff.Requests, Is.Empty);
    }

    [AvaloniaTest]
    public async Task SavePosition_WhenNothingOrAnotherFilePlays_ExplainsWithoutChangingTheShelf()
    {
        (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(null);
        await viewModel.AddAsync(new Uri(book));
        Click("SaveAudiobookPosition", window);
        await WaitAsync(() => Status(window).Contains("Nothing is playing", StringComparison.Ordinal));
        string other = Path.Combine(directory, "other.mp3"); File.WriteAllBytes(other, [1]);
        handoff.Current = new(new Uri(other), TimeSpan.FromMinutes(2), true);
        Click("SaveAudiobookPosition", window);
        await WaitAsync(() => Status(window).Contains("not playing a book from this shelf", StringComparison.Ordinal));
        Assert.That(viewModel.SelectedItem!.Entry.HasSavedPosition, Is.False);
    }

    [AvaloniaTest]
    public async Task Close_WhilePickerIsOpen_DrainsWithoutWaitingForTheDialogAndIgnoresALateResult()
    {
        DelayedFilePicker picker = new(new Uri(book));
        (AudiobooksTestWindow window, AudiobooksViewModel viewModel) = await OpenAsync(picker);
        Click("AddAudiobook", window);
        await WaitAsync(() => picker.Started);
        window.Close();
        await WaitAsync(() => !window.IsVisible);
        picker.Release.SetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.That(viewModel.Items, Is.Empty);
        Assert.That(await RepositoryItemCountAsync(), Is.Zero, "a late picker result adds nothing after close");
    }

    private async Task<int> RepositoryItemCountAsync()
    {
        await using SqliteTrackRepository fresh = new(Path.Combine(directory, "library.db"));
        return (await fresh.ListShelfAsync()).Count;
    }

    private async Task<(AudiobooksTestWindow, AudiobooksViewModel)> OpenAsync(DelayedFilePicker? picker)
    {
        SqliteTrackRepository repository = new(Path.Combine(directory, "library.db"));
        repositories.Add(repository);
        AudiobooksViewModel viewModel = new(new AudiobookShelfService(repository, reader, new AudiobookFiles()), handoff);
        AudiobooksTestWindow window = new(viewModel, picker); windows.Add(window);
        string initialStatus = viewModel.StatusMessage;
        window.Show();
        await WaitAsync(() => !viewModel.IsBusy && viewModel.StatusMessage != initialStatus);
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel);
    }

    private static string Status(Window window) => window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "AudiobookStatus").Text ?? "";
    private static bool Enabled(string name, Window window) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name).IsEnabled;

    private static void Click(string name, Window window)
    {
        Dispatcher.UIThread.RunJobs();
        Button button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == name && b.IsVisible);
        Assert.That(button.IsEnabled, Is.True, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task<Window> WaitForDialogAsync(Window owner)
    {
        await WaitAsync(() => owner.OwnedWindows.Count == 1);
        Dispatcher.UIThread.RunJobs();
        return owner.OwnedWindows[0];
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

    private sealed class ShelfReader(MediaMetadata metadata) : IMediaMetadataReader
    {
        public Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default) => Task.FromResult(metadata);
    }

    private sealed class ShelfHandoff : IAudiobookPlaybackHandoff
    {
        public List<AudiobookPlaybackRequest> Requests { get; } = [];
        public AudiobookPlaybackSnapshot? Current { get; set; }
        public Task PlayAsync(AudiobookPlaybackRequest request, CancellationToken cancellationToken = default) { Requests.Add(request); return Task.CompletedTask; }
        public ValueTask<AudiobookPlaybackSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
    }
}
