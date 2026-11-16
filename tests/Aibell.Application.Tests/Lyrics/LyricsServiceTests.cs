using Aibell.Application.Lyrics;
using Aibell.Domain.Lyrics;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Application.Tests.Lyrics;

[TestFixture]
public sealed class LyricsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class MemoryStore : ILyricsStore
    {
        public Dictionary<Guid, StoredLyrics> Rows { get; } = [];

        public Task<StoredLyrics?> GetAsync(Guid trackId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.TryGetValue(trackId, out StoredLyrics? row) ? row : null);

        public Task<bool> SaveFetchedAsync(Guid trackId, TrackLyrics lyrics, DateTimeOffset at, CancellationToken cancellationToken = default)
        {
            if (Rows.TryGetValue(trackId, out StoredLyrics? row) && row.Lyrics is { IsUserOwned: true }) return Task.FromResult(false);
            Rows[trackId] = new StoredLyrics(lyrics, at);
            return Task.FromResult(true);
        }

        public Task<bool> SaveMissAsync(Guid trackId, DateTimeOffset at, CancellationToken cancellationToken = default)
        {
            if (Rows.TryGetValue(trackId, out StoredLyrics? row) && row.Lyrics is not null) return Task.FromResult(false);
            Rows[trackId] = new StoredLyrics(null, at);
            return Task.FromResult(true);
        }

        public Task SaveUserAsync(Guid trackId, TrackLyrics lyrics, DateTimeOffset at, CancellationToken cancellationToken = default)
        {
            Rows[trackId] = new StoredLyrics(lyrics, at);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid trackId, CancellationToken cancellationToken = default)
        {
            Rows.Remove(trackId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFileReader(TrackLyrics? lyrics = null) : ILyricsFileReader
    {
        public int Calls { get; private set; }

        public Task<TrackLyrics?> ReadAsync(Uri location, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(lyrics);
        }
    }

    private sealed class FakeProvider : ILyricsProvider
    {
        public LyricsProviderDescriptor Descriptor { get; } = new("fake", "Fake");
        public Func<LyricsQuery, CancellationToken, Task<LyricsCandidate?>> Find { get; set; } = (_, _) => Task.FromResult<LyricsCandidate?>(null);
        public Func<LyricsQuery, Task<IReadOnlyList<LyricsCandidate>>> Search { get; set; } = _ => Task.FromResult<IReadOnlyList<LyricsCandidate>>([]);
        public int FindCalls { get; private set; }

        public Task<LyricsCandidate?> FindAsync(LyricsQuery query, CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return Find(query, cancellationToken);
        }

        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default) => Search(query);
    }

    private static Track Song(string artist = "Rammstein", string title = "Deutschland", string path = "/music/a.mp3", string source = "") =>
        new(TrackIdentity.FromLocation(new Uri("file://" + path)), new Uri("file://" + path), title, artist, "Album", TimeSpan.FromSeconds(323), Source: source);

    private static LyricsCandidate Candidate(string? synced = "[00:01.00]a", string? plain = "a", bool instrumental = false) =>
        new("fake", "1", "Deutschland", "Rammstein", "Album", TimeSpan.FromSeconds(323), synced, plain, instrumental);

    private static (LyricsService Service, MemoryStore Store, FakeProvider Provider, FakeTime Time) Create(TrackLyrics? embedded = null, bool enabled = true)
    {
        MemoryStore store = new();
        FakeProvider provider = new() { Find = (_, _) => Task.FromResult<LyricsCandidate?>(Candidate()) };
        FakeTime time = new(Now);
        return (new LyricsService(store, new FakeFileReader(embedded), [provider], () => enabled, time), store, provider, time);
    }

    [Test]
    public async Task ResolveFetchesStoresAndThenServesFromTheCache()
    {
        var (service, store, provider, _) = Create();
        Track track = Song();
        LyricsResult first = await service.ResolveAsync(track);
        Assert.That(first.Status, Is.EqualTo(LyricsStatus.Found));
        Assert.That(first.Lyrics!.IsSynced, Is.True);
        Assert.That(store.Rows[track.Id].Lyrics!.Origin, Is.EqualTo(LyricsOrigin.Provider));
        LyricsResult second = await service.ResolveAsync(track);
        Assert.That(second.Status, Is.EqualTo(LyricsStatus.Found));
        Assert.That(provider.FindCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task ResolveDoesNothingAtAllWhenLyricsAreDisabled()
    {
        var (service, store, provider, _) = Create(enabled: false);
        Assert.That((await service.ResolveAsync(Song())).Status, Is.EqualTo(LyricsStatus.Disabled));
        Assert.That(provider.FindCalls, Is.Zero);
        Assert.That(store.Rows, Is.Empty);
        Assert.That(await service.SearchAsync(new LyricsQuery("a", "b", "", TimeSpan.Zero)), Is.Empty);
    }

    [Test]
    public async Task ResolveSkipsRemoteItemsAndTracksWithoutTitleOrArtistWithoutCallingTheNetwork()
    {
        var (service, _, provider, _) = Create();
        Track stream = Song() with { Location = new Uri("http://radio.example/live") };
        Assert.That((await service.ResolveAsync(stream)).Status, Is.EqualTo(LyricsStatus.Unavailable));
        Assert.That((await service.ResolveAsync(Song(artist: ""))).Status, Is.EqualTo(LyricsStatus.Unavailable));
        Assert.That((await service.ResolveAsync(Song(title: " "))).Status, Is.EqualTo(LyricsStatus.Unavailable));
        Assert.That(provider.FindCalls, Is.Zero);
    }

    [Test]
    public async Task EmbeddedLyricsBeatTheCacheAndSkipTheNetwork()
    {
        TrackLyrics embedded = TrackLyrics.FromText(null, "from file", false, LyricsOrigin.Embedded, "file")!;
        var (service, store, provider, _) = Create(embedded);
        Track track = Song();
        store.Rows[track.Id] = new StoredLyrics(TrackLyrics.FromText(null, "from cache", false, LyricsOrigin.Provider)!, Now);
        LyricsResult result = await service.ResolveAsync(track);
        Assert.That(result.Lyrics!.Lines[0].Text, Is.EqualTo("from file"));
        Assert.That(provider.FindCalls, Is.Zero);
    }

    [Test]
    public async Task TheUsersOwnEntryBeatsEmbeddedLyrics()
    {
        TrackLyrics embedded = TrackLyrics.FromText(null, "from file", false, LyricsOrigin.Embedded)!;
        var (service, store, _, _) = Create(embedded);
        Track track = Song();
        await service.SaveWrittenAsync(track, "mine");
        Assert.That((await service.ResolveAsync(track)).Lyrics!.Lines[0].Text, Is.EqualTo("mine"));
        Assert.That(store.Rows[track.Id].Lyrics!.Origin, Is.EqualTo(LyricsOrigin.UserWritten));
    }

    [Test]
    public async Task AMissIsRememberedForAWhileThenTriedAgain()
    {
        var (service, store, provider, time) = Create();
        provider.Find = (_, _) => Task.FromResult<LyricsCandidate?>(null);
        Track track = Song();
        Assert.That((await service.ResolveAsync(track)).Status, Is.EqualTo(LyricsStatus.NotFound));
        Assert.That(store.Rows[track.Id].Lyrics, Is.Null);
        await service.ResolveAsync(track);
        Assert.That(provider.FindCalls, Is.EqualTo(1), "second play must not hit the network");
        time.Current = Now + LyricsService.MissLifetime + TimeSpan.FromMinutes(1);
        provider.Find = (_, _) => Task.FromResult<LyricsCandidate?>(Candidate());
        Assert.That((await service.ResolveAsync(track)).Status, Is.EqualTo(LyricsStatus.Found));
        Assert.That(provider.FindCalls, Is.EqualTo(2));
    }

    [Test]
    public async Task AnUnreachableProviderIsNotRememberedAsNoLyrics()
    {
        var (service, store, provider, _) = Create();
        provider.Find = (_, _) => throw new HttpRequestException("offline");
        Track track = Song();
        Assert.That((await service.ResolveAsync(track)).Status, Is.EqualTo(LyricsStatus.NotFound));
        Assert.That(store.Rows, Is.Empty);
    }

    [Test]
    public async Task NoProviderInstalledReportsItAndTouchesNothing()
    {
        LyricsService service = new(new MemoryStore(), new FakeFileReader(), [], () => true);
        Assert.That((await service.ResolveAsync(Song())).Status, Is.EqualTo(LyricsStatus.NoProvider));
        Assert.That(service.HasProviders, Is.False);
    }

    [Test]
    public async Task CancellingALookupPropagatesAndStoresNothing()
    {
        var (service, store, provider, _) = Create();
        using CancellationTokenSource cancel = new();
        provider.Find = async (_, token) => { await cancel.CancelAsync(); token.ThrowIfCancellationRequested(); return Candidate(); };
        Assert.That(async () => await service.ResolveAsync(Song(), cancel.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(store.Rows, Is.Empty);
    }

    [Test]
    public async Task InstrumentalRecordsAreStoredAsInstrumental()
    {
        var (service, _, provider, _) = Create();
        provider.Find = (_, _) => Task.FromResult<LyricsCandidate?>(Candidate(null, null, instrumental: true));
        LyricsResult result = await service.ResolveAsync(Song());
        Assert.That(result.Lyrics!.IsInstrumental, Is.True);
    }

    [Test]
    public async Task AFetchNeverReplacesTheUsersChoice()
    {
        var (service, store, _, _) = Create();
        Track track = Song();
        await service.SelectAsync(track, Candidate("[00:09.00]chosen", "chosen"));
        Assert.That(await store.SaveFetchedAsync(track.Id, TrackLyrics.FromText(null, "auto", false, LyricsOrigin.Provider)!, Now), Is.False);
        Assert.That(store.Rows[track.Id].Lyrics!.Origin, Is.EqualTo(LyricsOrigin.UserSelected));
    }

    [Test]
    public async Task SearchMergesProvidersAndSurvivesABrokenOne()
    {
        FakeProvider broken = new() { Search = _ => throw new InvalidOperationException("boom") };
        FakeProvider good = new() { Search = _ => Task.FromResult<IReadOnlyList<LyricsCandidate>>([Candidate()]) };
        LyricsService service = new(new MemoryStore(), new FakeFileReader(), [broken, good], () => true);
        Assert.That(await service.SearchAsync(new LyricsQuery("a", "b", "", TimeSpan.Zero)), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SelectingACandidateWithoutTextReturnsNullAndWritingEmptyTextRemovesTheEntry()
    {
        var (service, store, _, _) = Create();
        Track track = Song();
        Assert.That(await service.SelectAsync(track, Candidate(null, " ")), Is.Null);
        Assert.That(store.Rows, Is.Empty);
        await service.SaveWrittenAsync(track, "[00:01.00]typed");
        Assert.That(store.Rows[track.Id].Lyrics!.IsSynced, Is.True);
        Assert.That(await service.SaveWrittenAsync(track, "  "), Is.Null);
        Assert.That(store.Rows, Is.Empty);
    }
}
