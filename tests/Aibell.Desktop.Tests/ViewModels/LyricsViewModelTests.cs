using Aibell.Application.Lyrics;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Lyrics;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.ViewModels;

[TestFixture]
public sealed class LyricsViewModelTests
{
    private sealed class Store : ILyricsStore
    {
        public Task<StoredLyrics?> GetAsync(Guid trackId, CancellationToken cancellationToken = default) => Task.FromResult<StoredLyrics?>(null);
        public Task<bool> SaveFetchedAsync(Guid trackId, TrackLyrics lyrics, DateTimeOffset at, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> SaveMissAsync(Guid trackId, DateTimeOffset at, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SaveUserAsync(Guid trackId, TrackLyrics lyrics, DateTimeOffset at, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(Guid trackId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoFileReader : ILyricsFileReader
    {
        public Task<TrackLyrics?> ReadAsync(Uri location, CancellationToken cancellationToken = default) => Task.FromResult<TrackLyrics?>(null);
    }

    private sealed class Provider : ILyricsProvider
    {
        public LyricsProviderDescriptor Descriptor { get; } = new("fake", "Fake");
        public Dictionary<string, TaskCompletionSource<LyricsCandidate?>> Pending { get; } = [];
        public List<string> Asked { get; } = [];

        public Task<LyricsCandidate?> FindAsync(LyricsQuery query, CancellationToken cancellationToken = default)
        {
            lock (Pending)
            {
                Asked.Add(query.Title);
                TaskCompletionSource<LyricsCandidate?> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Pending[query.Title] = source;
                cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
                return source.Task;
            }
        }

        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LyricsCandidate>>([]);

        public void Answer(string title, string synced) =>
            Pending[title].SetResult(new LyricsCandidate("fake", "1", title, "A", "", TimeSpan.FromSeconds(100), synced, null, false));
    }

    private static Track Song(string title, string path = "/music/x.mp3", string source = "") =>
        new(TrackIdentity.FromLocation(new Uri("file://" + path)), new Uri("file://" + path), title, "Artist", "Album", TimeSpan.FromSeconds(100), Source: source);

    private static (LyricsViewModel Model, Provider Provider) Create()
    {
        Provider provider = new();
        LyricsService service = new(new Store(), new NoFileReader(), [provider], () => true);
        return (new LyricsViewModel(service, action => action()), provider);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.That(condition(), Is.True, "timed out");
    }

    [Test]
    public async Task ATrackChangeLooksLyricsUpAndShowsThemWithoutBlocking()
    {
        var (model, provider) = Create();
        Track track = Song("One");
        model.SetTrack(track);
        Assert.That(model.IsLoading, Is.True, "returns at once while the lookup is pending");
        Assert.That(model.StatusText, Is.EqualTo("Looking for lyrics…"));
        await UntilAsync(() => provider.Pending.ContainsKey("One"));
        provider.Answer("One", "[00:01.00]first\n[00:05.00]second");
        await UntilAsync(() => model.HasLines);
        Assert.That(model.IsLoading, Is.False);
        Assert.That(model.IsSynced, Is.True);
        Assert.That(model.Lines.Select(line => line.Text), Is.EqualTo(new[] { "first", "second" }));
        Assert.That(model.SourceText, Does.Contain("synced"));
    }

    [Test]
    public async Task ALibraryTrackWhoseSourceIsItsMusicFolderIsLookedUp()
    {
        var (model, provider) = Create();
        model.SetTrack(Song("Deutschland", "/media/music/a.flac", source: "/media/music"));
        Assert.That(model.CanEdit, Is.True);
        await UntilAsync(() => provider.Asked.Contains("Deutschland"));
    }

    [Test]
    public async Task ALateAnswerForASkippedTrackIsDropped()
    {
        var (model, provider) = Create();
        model.SetTrack(Song("One", "/music/one.mp3"));
        await UntilAsync(() => provider.Pending.ContainsKey("One"));
        model.SetTrack(Song("Two", "/music/two.mp3"));
        await UntilAsync(() => provider.Pending.ContainsKey("Two"));
        provider.Pending["One"].TrySetResult(new LyricsCandidate("fake", "1", "One", "A", "", TimeSpan.Zero, null, "stale lyrics", false));
        provider.Answer("Two", "[00:01.00]right");
        await UntilAsync(() => model.HasLines);
        Assert.That(model.Lines.Select(line => line.Text), Is.EqualTo(new[] { "right" }));
        Assert.That(model.Track!.Title, Is.EqualTo("Two"));
    }

    [Test]
    public void RadioDiscAndMpdItemsAreNeverLookedUp()
    {
        var (model, provider) = Create();
        model.SetTrack(Song("Live") with { Location = new Uri("http://radio.example/live") });
        Assert.That(model.StatusText, Is.EqualTo("Lyrics are not available for this item."));
        model.SetTrack(Song("CD", "/music/cd.mp3", source: "Audio CD"));
        model.SetTrack(Song("Pod", "/music/pod.mp3", source: "Podcast"));
        Assert.That(provider.Asked, Is.Empty);
        Assert.That(model.CanEdit, Is.False);
    }

    [Test]
    public void DisablingStopsLookupsAndClearsThePanelAndEnablingResumes()
    {
        var (model, provider) = Create();
        model.SetEnabled(false);
        model.SetTrack(Song("One"));
        Assert.That(provider.Asked, Is.Empty);
        Assert.That(model.IsLoading, Is.False);
        model.SetEnabled(true);
        model.SetTrack(Song("One"));
        Assert.That(model.IsLoading, Is.True);
    }

    [Test]
    public async Task PositionHighlightsTheCurrentLineAndRaisesTheScrollEvent()
    {
        var (model, provider) = Create();
        model.SetTrack(Song("One"));
        await UntilAsync(() => provider.Pending.ContainsKey("One"));
        provider.Answer("One", "[00:01.00]a\n[00:05.00]b\n[00:09.00]c");
        await UntilAsync(() => model.HasLines);
        List<int> raised = [];
        model.CurrentLineChanged += (_, index) => raised.Add(index);
        model.SetPosition(TimeSpan.FromSeconds(0.5));
        model.SetPosition(TimeSpan.FromSeconds(5.2));
        model.SetPosition(TimeSpan.FromSeconds(6));
        model.SetPosition(TimeSpan.FromSeconds(100));
        Assert.That(raised, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(model.Lines.Select(line => line.IsCurrent), Is.EqualTo(new[] { false, false, true }));
        model.SetPosition(TimeSpan.FromSeconds(0));
        Assert.That(model.Lines.Any(line => line.IsCurrent), Is.False);
    }

    [Test]
    public void IndexAtFindsTheLastLineAtOrBeforeThePosition()
    {
        TimeSpan[] starts = [.. new[] { 1.0, 5.0, 9.0 }.Select(TimeSpan.FromSeconds)];
        Assert.That(LyricsViewModel.IndexAt(starts, TimeSpan.FromSeconds(0.9)), Is.EqualTo(-1));
        Assert.That(LyricsViewModel.IndexAt(starts, TimeSpan.FromSeconds(1)), Is.EqualTo(0));
        Assert.That(LyricsViewModel.IndexAt(starts, TimeSpan.FromSeconds(8.99)), Is.EqualTo(1));
        Assert.That(LyricsViewModel.IndexAt(starts, TimeSpan.FromSeconds(500)), Is.EqualTo(2));
        Assert.That(LyricsViewModel.IndexAt([], TimeSpan.FromSeconds(1)), Is.EqualTo(-1));
    }

    [Test]
    [Retry(3)] // Timing- and file-lock-sensitive on loaded Windows CI runners.
    public async Task PlainLyricsShowWithoutHighlightingAndTheUserCanReplaceThem()
    {
        var (model, provider) = Create();
        Track track = Song("One");
        model.SetTrack(track);
        await UntilAsync(() => provider.Pending.ContainsKey("One"));
        provider.Pending["One"].SetResult(null);
        await UntilAsync(() => !model.IsLoading);
        Assert.That(model.StatusText, Does.StartWith("No lyrics found"));
        model.Show(track, TrackLyrics.FromText(null, "typed\nlines", false, LyricsOrigin.UserWritten));
        Assert.That(model.HasLines, Is.True);
        Assert.That(model.IsSynced, Is.False);
        model.SetPosition(TimeSpan.FromSeconds(5));
        Assert.That(model.Lines.Any(line => line.IsCurrent), Is.False);
        Assert.That(model.SourceText, Is.EqualTo("Typed by you"));
        model.Show(Song("Other", "/music/other.mp3"), null);
        Assert.That(model.HasLines, Is.True, "a result for another track is ignored");
    }
}
