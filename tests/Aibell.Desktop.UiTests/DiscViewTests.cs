using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Discs;
using Aibell.Application.Enrichment;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class DiscViewTests
{
    private static readonly DiscToc Toc = new([150, 15363, 32314], 46592);
    private static readonly OpticalDrive Drive = new("/dev/sr0", "CD/DVD drive (sr0)");
    private readonly List<(Window Window, DiscViewModel Model)> windows = [];

    [TearDown]
    public async Task TearDown()
    {
        foreach ((Window window, DiscViewModel model) in windows) { await model.ShutdownAsync(); model.Dispose(); window.Close(); }
        windows.Clear();
    }

    [AvaloniaTest]
    public async Task Open_DiscPresent_ListsGenericTracksAndDoesNotTouchTheNetwork()
    {
        FakeLookup lookup = new() { Available = true };
        (Window window, DiscViewModel viewModel) = await OpenAsync(new FakeDrives(Toc), lookup, new FakeHandoff());

        Assert.That(Rows(viewModel), Is.EqualTo(Items("Track 1", "Track 2", "Track 3")));
        Assert.That(Status(window), Does.Contain("3 tracks"));
        Assert.That(lookup.DiscCalls, Is.Zero, "opening the window must never send anything online");
        Assert.That(viewModel.HasDisc, Is.True);
    }

    [AvaloniaTest]
    public async Task Open_NoDrive_ExplainsAndDisablesPlayback()
    {
        (Window window, DiscViewModel viewModel) = await OpenAsync(new FakeDrives(null, noDrive: true), new FakeLookup(), new FakeHandoff());

        Assert.That(Status(window), Does.Contain("No CD or DVD drive"));
        Assert.That(viewModel.HasDisc, Is.False);
        Assert.That(Enabled("LookUpDisc", window), Is.False);
    }

    [AvaloniaTest]
    public async Task Open_EmptyDrive_SaysThereIsNoDisc()
    {
        (Window window, _) = await OpenAsync(new FakeDrives(null), new FakeLookup { Available = true }, new FakeHandoff());

        Assert.That(Status(window), Does.Contain("No audio disc"));
        Assert.That(Enabled("LookUpDisc", window), Is.False);
    }

    [AvaloniaTest]
    public async Task LookUp_OnlineLookupOff_ButtonStaysDisabled()
    {
        (Window window, _) = await OpenAsync(new FakeDrives(Toc), new FakeLookup { Available = false }, new FakeHandoff());
        Assert.That(Enabled("LookUpDisc", window), Is.False);
    }

    [AvaloniaTest]
    public async Task LookUp_ReleaseFound_NamesTracksAndShowsAlbumDetails()
    {
        FakeLookup lookup = new()
        {
            Available = true,
            Releases = [new("fake", "1", "The Album", "The Band", 1999, "rock", null, 1,
                [new(1, "One"), new(2, "Two", "Guest"), new(3, "Three")])]
        };
        (Window window, DiscViewModel viewModel) = await OpenAsync(new FakeDrives(Toc), lookup, new FakeHandoff());

        Click("LookUpDisc", window);
        await WaitAsync(() => !viewModel.IsBusy && viewModel.AlbumTitle == "The Album");

        Assert.That(Rows(viewModel), Is.EqualTo(Items("One", "Two", "Three")));
        Assert.That(viewModel.AlbumDetails, Is.EqualTo("The Band · 1999 · rock"));
        Assert.That(lookup.DiscCalls, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task LookUp_DiscUnknown_KeepsGenericTracks()
    {
        (Window window, DiscViewModel viewModel) = await OpenAsync(new FakeDrives(Toc), new FakeLookup { Available = true }, new FakeHandoff());

        Click("LookUpDisc", window);
        await WaitAsync(() => !viewModel.IsBusy && Status(window).Contains("not known", StringComparison.Ordinal));

        Assert.That(Rows(viewModel), Is.EqualTo(Items("Track 1", "Track 2", "Track 3")));
    }

    [AvaloniaTest]
    public async Task Open_HasNoSeparatePlayButtons_SoTheTrackListIsTheOnlyWayToPlay()
    {
        (Window window, _) = await OpenAsync(new FakeDrives(Toc), new FakeLookup(), new FakeHandoff());

        Assert.That(window.GetVisualDescendants().OfType<Button>().Select(b => b.Name), Does.Not.Contain("PlayDisc").And.Not.Contain("PlayDiscTrack"));
    }

    [AvaloniaTest]
    public async Task DoubleTapTrack_HandsTheDiscToThePlayerFromThatTrack()
    {
        FakeHandoff handoff = new();
        (Window window, DiscViewModel viewModel) = await OpenAsync(new FakeDrives(Toc), new FakeLookup(), handoff);
        viewModel.SelectedRow = viewModel.Rows[1];
        Dispatcher.UIThread.RunJobs();

        // Headless input cannot synthesize the double-tap gesture, so the handler bound to it in the XAML is invoked directly.
        DiscView view = window.GetVisualDescendants().OfType<DiscView>().Single();
        typeof(DiscView).GetMethod("TrackDoubleTapped", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, [null, null]);
        await WaitAsync(() => handoff.Calls.Count == 1);

        Assert.That(handoff.Calls[0].StartIndex, Is.EqualTo(1));
        Assert.That(handoff.Calls[0].Tracks.Select(track => track.Location.AbsoluteUri),
            Is.EqualTo(Items("cdda:///dev/sr0?track=1", "cdda:///dev/sr0?track=2", "cdda:///dev/sr0?track=3")));
    }

    [AvaloniaTest]
    public async Task EnterOnTrack_PlaysFromThatTrack()
    {
        FakeHandoff handoff = new();
        (Window window, DiscViewModel viewModel) = await OpenAsync(new FakeDrives(Toc), new FakeLookup(), handoff);
        viewModel.SelectedRow = viewModel.Rows[2];
        Dispatcher.UIThread.RunJobs();

        ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "DiscTrackList");
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await WaitAsync(() => handoff.Calls.Count == 1);

        Assert.That(handoff.Calls[0].StartIndex, Is.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task TrackTimes_AreNotClippedByTheListEdge()
    {
        (Window window, _) = await OpenAsync(new FakeDrives(new DiscToc([150, 15363, 32314], 46592 + 400000)), new FakeLookup(), new FakeHandoff());
        window.UpdateLayout();

        ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "DiscTrackList");
        foreach (TextBlock time in list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("secondary") && t.Text is { Length: > 0 } text && text.Contains(':')))
        {
            Rect bounds = time.TranslatePoint(new Point(0, 0), list) is { } origin ? new Rect(origin, time.Bounds.Size) : default;
            Assert.That(bounds.Right, Is.LessThanOrEqualTo(list.Bounds.Width + 0.5), $"time '{time.Text}' must fit inside the list");
        }
    }

    [AvaloniaTest]
    public async Task ArtistInfo_Found_ShowsBasicFactsOnly()
    {
        FakeLookup lookup = new()
        {
            Available = true,
            Releases = [new("fake", "1", "The Album", "The Band", 1999, "rock", null, 1, [])],
            Artist = new ArtistInfo("fake", "a", "The Band", "Group", "United Kingdom", 1985, 2001, "UK rock band", ["rock"], [new Uri("https://theband.example/")])
        };
        (Window window, DiscViewModel viewModel) = await OpenAsync(new FakeDrives(Toc), lookup, new FakeHandoff());
        Click("LookUpDisc", window);
        await WaitAsync(() => !viewModel.IsBusy && viewModel.AlbumTitle == "The Album");

        Click("ShowArtist", window);
        await WaitAsync(() => !viewModel.IsBusy && viewModel.ArtistDetails.Length > 0);

        Assert.That(viewModel.ArtistDetails, Does.Contain("Group · United Kingdom").And.Contain("1985–2001").And.Contain("https://theband.example/"));
    }

    private async Task<(Window, DiscViewModel)> OpenAsync(FakeDrives drives, FakeLookup lookup, FakeHandoff handoff)
    {
        DiscViewModel viewModel = new(drives, lookup, handoff);
        DiscView view = new() { DataContext = viewModel };
        Window window = new() { Width = 700, Height = 520, Content = view };
        windows.Add((window, viewModel));
        window.Show();
        _ = view.LoadAsync();
        await WaitAsync(() => !viewModel.IsBusy && viewModel.StatusMessage.Length > 0 && viewModel.StatusMessage != "Reading the disc…");
        return (window, viewModel);
    }

    private static string[] Rows(DiscViewModel viewModel) => [.. viewModel.Rows.Select(row => row.Title)];

    private static string Status(Window window) => window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "DiscStatus").Text ?? "";
    private static bool Enabled(string name, Window window) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name).IsEnabled;

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
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for the Audio CD window.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private sealed class FakeDrives(DiscToc? toc, bool noDrive = false) : IOpticalDriveService
    {
        public IReadOnlyList<OpticalDrive> ListDrives() => noDrive ? [] : [Drive];
        public Task<DiscToc?> ReadTocAsync(OpticalDrive drive, CancellationToken cancellationToken = default) => Task.FromResult(toc);
    }

    private sealed class FakeHandoff : IDiscPlaybackHandoff
    {
        public List<(IReadOnlyList<Track> Tracks, int StartIndex)> Calls { get; } = [];

        public Task PlayDiscAsync(IReadOnlyList<Track> tracks, int startIndex, CancellationToken cancellationToken = default)
        {
            Calls.Add((tracks, startIndex));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLookup : IMetadataLookupService
    {
        public bool Available { get; init; }
        public IReadOnlyList<ReleaseCandidate> Releases { get; init; } = [];
        public ArtistInfo? Artist { get; init; }
        public int DiscCalls { get; private set; }

        public bool IsAvailable => Available;

        public Task<MetadataLookupResult<ReleaseCandidate>> FindDiscAsync(DiscToc toc, CancellationToken cancellationToken = default)
        {
            DiscCalls++;
            return Task.FromResult(new MetadataLookupResult<ReleaseCandidate>(Releases.Count > 0 ? MetadataLookupStatus.Found : MetadataLookupStatus.NotFound, Releases));
        }

        public Task<MetadataLookupResult<ReleaseCandidate>> FindAlbumAsync(string album, string? artist, bool includeTracks = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MetadataLookupResult<ReleaseCandidate>(MetadataLookupStatus.NotFound, []));

        public Task<MetadataLookupResult<ArtistInfo>> FindArtistAsync(string artistName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Artist is null ? new MetadataLookupResult<ArtistInfo>(MetadataLookupStatus.NotFound, [])
                : new MetadataLookupResult<ArtistInfo>(MetadataLookupStatus.Found, [Artist]));
    }

    private static T[] Items<T>(params T[] items) => items;
}
