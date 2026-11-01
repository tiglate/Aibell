using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Discs;
using Aibell.Application.Enrichment;
using Aibell.Application.Library;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class ArtworkWindowTests
{
    private static readonly Track Song = new(Guid.NewGuid(), new Uri("file:///music/band/album/one.mp3"), "One", "The Band", "The Album", TimeSpan.FromMinutes(3));
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 0xFF, 0xD9];
    private readonly List<ArtworkWindow> windows = [];

    [TearDown]
    public void CloseWindows()
    {
        foreach (ArtworkWindow window in windows) window.Close();
        windows.Clear();
    }

    private static ReleaseCandidate Release(string title, string? art, int year = 1999) =>
        new("test", title, title, "The Band", year, "rock", art is null ? null : new Uri(art), 0.9, [new ReleaseTrack(1, "One")]);

    [AvaloniaTest]
    public async Task Open_SearchesByAlbumAndAlbumArtistAndListsOnlyReleasesThatHaveACover()
    {
        FakeLookup lookup = new(MetadataLookupStatus.Found, Release("The Album", "https://c.test/1.jpg"), Release("Bootleg", null), Release("The Album (Deluxe)", "https://c.test/2.jpg", 2004));
        (ArtworkWindow window, ArtworkLookupViewModel model) = await OpenAsync(lookup, new FakeDownloader(), new FakeWriter(),
            Song with { AlbumArtist = "Various" });

        Assert.That(lookup.Requests, Is.EqualTo(Items(("The Album", "Various"))));
        Assert.That(model.Candidates.Select(row => row.Title), Is.EqualTo(Items("The Album", "The Album (Deluxe)")));
        Assert.That(model.Candidates[0].Details, Is.EqualTo("The Band · 1999 · 1 tracks"));
        Assert.That(model.Selected, Is.SameAs(model.Candidates[0]));
        Assert.That(Text("ArtworkHeading", window), Is.EqualTo("The Album — Various"));
        Assert.That(Text("ArtworkStatus", window), Does.Contain("Found 2 covers"));
        Assert.That(Enabled("SaveArtwork", window), Is.True);
    }

    [AvaloniaTest]
    public async Task Save_DownloadsTheChosenCoverAndWritesItBesideTheTrack()
    {
        FakeDownloader downloader = new();
        FakeWriter writer = new();
        (ArtworkWindow window, ArtworkLookupViewModel model) = await OpenAsync(
            new FakeLookup(MetadataLookupStatus.Found, Release("A", "https://c.test/1.jpg"), Release("B", "https://c.test/2.jpg")), downloader, writer);
        model.Selected = model.Candidates[1];

        Click("SaveArtwork", window);
        await UntilAsync(() => model.SavedPath is not null);

        Assert.That(downloader.Requested, Is.EqualTo(Items(new Uri("https://c.test/2.jpg"))));
        Assert.That(writer.Saved, Is.EqualTo(Items((Song.Location, Jpeg.Length))));
        Assert.That(model.SavedPath, Is.EqualTo(Path.Combine(Path.GetDirectoryName(Song.Location.LocalPath)!, "cover.jpg")));
        Assert.That(Text("ArtworkStatus", window), Does.Contain("Saved cover.jpg").And.Contain("keep showing that picture"));
        Assert.That(Enabled("SaveArtwork", window), Is.False, "one cover is saved per search");
    }

    [AvaloniaTest]
    public async Task Save_ExistingCover_ShowsTheReasonAndSavesNothing()
    {
        FakeWriter writer = new() { Error = new IOException("This folder already has cover.jpg. Rename or delete it first; Aibell never replaces an existing cover.") };
        (ArtworkWindow window, ArtworkLookupViewModel model) = await OpenAsync(
            new FakeLookup(MetadataLookupStatus.Found, Release("A", "https://c.test/1.jpg")), new FakeDownloader(), writer);

        Click("SaveArtwork", window);
        await UntilAsync(() => !model.IsBusy);

        Assert.That(model.SavedPath, Is.Null);
        Assert.That(Text("ArtworkStatus", window), Does.Contain("already has cover.jpg"));
        Assert.That(Enabled("SaveArtwork", window), Is.True, "the user may try again after renaming the file");
    }

    [AvaloniaTest]
    public async Task Save_DownloadFails_ShowsTheErrorWithoutWriting()
    {
        FakeWriter writer = new();
        (ArtworkWindow window, ArtworkLookupViewModel model) = await OpenAsync(
            new FakeLookup(MetadataLookupStatus.Found, Release("A", "https://c.test/1.jpg")), new FakeDownloader { Error = new InvalidDataException("The download is not a PNG or JPEG image.") }, writer);

        Click("SaveArtwork", window);
        await UntilAsync(() => !model.IsBusy);

        Assert.That(Text("ArtworkStatus", window), Does.Contain("not a PNG or JPEG"));
        Assert.That(writer.Saved, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(MetadataLookupStatus.NotFound, "No matching album")]
    [TestCase(MetadataLookupStatus.Disabled, "Preferences > Online")]
    [TestCase(MetadataLookupStatus.NoProvider, "Preferences > Plugins")]
    [TestCase(MetadataLookupStatus.Failed, "The lookup failed")]
    public async Task Open_LookupDidNotFindAnything_ExplainsWhyAndOffersNoSave(MetadataLookupStatus status, string expected)
    {
        (ArtworkWindow window, _) = await OpenAsync(new FakeLookup(status), new FakeDownloader(), new FakeWriter());

        Assert.That(Text("ArtworkStatus", window), Does.Contain(expected));
        Assert.That(Enabled("SaveArtwork", window), Is.False);
    }

    [AvaloniaTest]
    public async Task Open_AlbumKnownButNoCoverArt_SaysSo()
    {
        (ArtworkWindow window, ArtworkLookupViewModel model) = await OpenAsync(new FakeLookup(MetadataLookupStatus.Found, Release("The Album", null)), new FakeDownloader(), new FakeWriter());

        Assert.That(model.Candidates, Is.Empty);
        Assert.That(Text("ArtworkStatus", window), Does.Contain("no cover art"));
    }

    [AvaloniaTest]
    public async Task Open_WhileSearching_ShowsAProgressIndicatorThatDisappearsWhenDone()
    {
        FakeLookup lookup = new(MetadataLookupStatus.Found, Release("A", "https://c.test/1.jpg")) { Hold = true };
        ArtworkLookupViewModel model = new(Song, lookup, new FakeDownloader(), new FakeWriter());
        ArtworkWindow window = new(model);
        windows.Add(window);
        window.Show();
        await UntilAsync(() => lookup.Requests.Count == 1);
        ProgressBar bar = window.GetVisualDescendants().OfType<ProgressBar>().Single(candidate => candidate.Name == "ArtworkProgress");

        Assert.That(bar.IsVisible, Is.True);
        Assert.That(bar.IsIndeterminate, Is.True);
        Assert.That(Text("ArtworkStatus", window), Does.Contain("Looking for covers"));
        lookup.Release();
        await UntilAsync(() => !model.IsBusy);
        Dispatcher.UIThread.RunJobs();

        Assert.That(bar.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task Close_WhileSearching_CancelsTheLookup()
    {
        FakeLookup lookup = new(MetadataLookupStatus.Found) { Hold = true };
        ArtworkLookupViewModel model = new(Song, lookup, new FakeDownloader(), new FakeWriter());
        ArtworkWindow window = new(model);
        windows.Add(window);
        window.Show();
        await UntilAsync(() => lookup.Requests.Count == 1);

        window.Close();
        await UntilAsync(() => lookup.Cancelled);

        Assert.That(lookup.Cancelled, Is.True);
    }

    private async Task<(ArtworkWindow, ArtworkLookupViewModel)> OpenAsync(FakeLookup lookup, FakeDownloader downloader, FakeWriter writer, Track? track = null)
    {
        ArtworkLookupViewModel model = new(track ?? Song, lookup, downloader, writer);
        ArtworkWindow window = new(model);
        windows.Add(window);
        window.Show();
        await UntilAsync(() => lookup.Requests.Count == 1 && !model.IsBusy);
        Dispatcher.UIThread.RunJobs();
        return (window, model);
    }

    private static string Text(string name, Window window) => window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Name == name).Text ?? "";

    private static bool Enabled(string name, Window window) => window.GetVisualDescendants().OfType<Button>().First(button => button.Name == name).IsEnabled;

    private static void Click(string name, Window window)
    {
        Dispatcher.UIThread.RunJobs();
        Button button = window.GetVisualDescendants().OfType<Button>().First(candidate => candidate.Name == name);
        Assert.That(button.IsEnabled, Is.True, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }

    private static T[] Items<T>(params T[] items) => items;

    internal sealed class FakeLookup(MetadataLookupStatus status, params ReleaseCandidate[] releases) : IMetadataLookupService
    {
        public List<(string Album, string? Artist)> Requests { get; } = [];
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hold { get; init; }
        public bool Cancelled { get; private set; }

        public void Release() => gate.TrySetResult();
        public bool IsAvailable => true;

        public Task<MetadataLookupResult<ReleaseCandidate>> FindDiscAsync(DiscToc toc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MetadataLookupResult<ReleaseCandidate>(MetadataLookupStatus.NotFound, []));

        public async Task<MetadataLookupResult<ReleaseCandidate>> FindAlbumAsync(string album, string? artist, bool includeTracks = true, CancellationToken cancellationToken = default)
        {
            Requests.Add((album, artist));
            if (Hold)
            {
                try { await gate.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return new MetadataLookupResult<ReleaseCandidate>(status, releases, status == MetadataLookupStatus.Failed ? "boom" : null);
        }

        public Task<MetadataLookupResult<ArtistInfo>> FindArtistAsync(string artistName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MetadataLookupResult<ArtistInfo>(MetadataLookupStatus.NotFound, []));
    }

    internal sealed class FakeDownloader : ICoverArtDownloader
    {
        public List<Uri> Requested { get; } = [];
        public Exception? Error { get; init; }

        public Task<byte[]> DownloadAsync(Uri source, CancellationToken cancellationToken = default)
        {
            Requested.Add(source);
            return Error is null ? Task.FromResult(Jpeg) : Task.FromException<byte[]>(Error);
        }
    }

    internal sealed class FakeWriter : IAlbumCoverWriter
    {
        public List<(Uri Location, int Length)> Saved { get; } = [];
        public Exception? Error { get; init; }

        public Task<string> SaveAsync(Uri trackLocation, byte[] image, CancellationToken cancellationToken = default)
        {
            if (Error is not null) return Task.FromException<string>(Error);
            Saved.Add((trackLocation, image.Length));
            return Task.FromResult(Path.Combine(Path.GetDirectoryName(trackLocation.LocalPath)!, "cover.jpg"));
        }
    }
}
