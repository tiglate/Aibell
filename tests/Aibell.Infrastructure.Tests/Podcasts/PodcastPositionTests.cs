using System.Security.Cryptography;
using Aibell.Application.Library;
using Aibell.Application.Podcasts;
using Aibell.Domain.Media;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Podcasts;

[TestFixture]
public sealed class PodcastPositionTests
{
    private string directory = null!;
    private string database = null!;
    private string file = null!;
    private SqliteTrackRepository repository = null!;
    private PodcastSubscription subscription = null!;
    private StoredPodcastEpisode episode = null!;
    private readonly DateTimeOffset now = new(2026, 10, 16, 12, 34, 56, TimeSpan.Zero);
    private readonly PodcastPlaybackProgress previous = new(TimeSpan.FromTicks(1234567), TimeSpan.FromSeconds(10), DateTimeOffset.UnixEpoch);

    [SetUp]
    public async Task SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "aibell-podcast-position-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        database = Path.Combine(directory, "profile.db");
        repository = new(database);
        subscription = new(Guid.NewGuid(), new Uri("https://feed.example/show"), "Show", "", now);
        episode = new(subscription.Id, "episode", "Episode", new Uri("https://cdn.example/episode.wav"));
        await repository.AddSubscriptionAsync(subscription, [episode]);
        PodcastDownloadStore store = new(Path.Combine(directory, "downloads"));
        file = store.PrepareDestination(subscription.Id, "episode.wav").LocalPath;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.wav"), file);
        await repository.RecordDownloadAsync(subscription.Id, episode.EpisodeId, file, new FileInfo(file).Length, now);
        episode = (await repository.ListEpisodesAsync(subscription.Id)).Single();
        await repository.SaveEpisodePositionAsync(subscription.Id, episode.EpisodeId, file, previous);
        episode = (await repository.ListEpisodesAsync(subscription.Id)).Single();
    }

    [TearDown]
    public async Task TearDown()
    {
        await repository.DisposeAsync();
        Directory.Delete(directory, true);
    }

    [Test]
    public async Task Save_ExactTicksAndTimestamp_SurviveReopenWithoutChangingFileOrMusic()
    {
        byte[] hash = Hash();
        PodcastPlaybackProgress exact = new(TimeSpan.FromTicks(987654321), TimeSpan.FromTicks(1234567890), now.AddTicks(1234));
        Assert.That(await repository.SaveEpisodePositionAsync(subscription.Id, episode.EpisodeId, file, exact), Is.True);
        await repository.DisposeAsync();
        repository = new(database);
        Assert.That((await Current()).SavedProgress, Is.EqualTo(exact));
        Assert.That(Hash(), Is.EqualTo(hash));
        Assert.That(await Count("tracks"), Is.Zero);
    }

    [TestCase("wrong-path")]
    [TestCase("missing-episode")]
    [TestCase("missing-subscription")]
    public async Task Save_StaleIdentity_ReturnsFalseAndKeepsPrevious(string mismatch)
    {
        Assert.That(await repository.SaveEpisodePositionAsync(
            mismatch == "missing-subscription" ? Guid.NewGuid() : subscription.Id,
            mismatch == "missing-episode" ? "gone" : episode.EpisodeId,
            mismatch == "wrong-path" ? file + ".changed" : file, previous with { Position = TimeSpan.FromSeconds(3) }), Is.False);
        Assert.That((await Current()).SavedProgress, Is.EqualTo(previous));
    }

    [Test]
    public async Task Refresh_UpdatedAndAbsentEpisodeAndNotModified_PreservePosition()
    {
        await repository.ApplyRefreshAsync(subscription with { Title = "Updated" }, [episode with { Title = "Updated episode" }]);
        await repository.ApplyRefreshAsync(subscription, []);
        await repository.ApplyRefreshAsync(subscription with { LastRefreshed = now.AddHours(1) }, null);
        Assert.That((await Current()).SavedProgress, Is.EqualTo(previous));
    }

    [Test]
    public async Task Refresh_ServiceFreshThenNotModified_KeepsSavedProgressAndConditionalValidators()
    {
        byte[] hash = Hash();
        ConditionalFeed feed = new(subscription.FeedUrl);
        PodcastService service = new(repository, feed, new OfflineDownloader(), new PodcastDownloadStore(Path.Combine(directory, "downloads")),
            positions: repository, metadata: new AtlMediaMetadataReader());
        PodcastRefreshResult fresh = await service.RefreshAsync(subscription.Id);
        PodcastRefreshResult notModified = await service.RefreshAsync(subscription.Id);
        Assert.That(fresh.NotModified, Is.False);
        Assert.That(notModified.NotModified, Is.True);
        Assert.That(feed.Requests[1].EntityTag, Is.EqualTo("\"positions-v2\""));
        Assert.That((await Current()).SavedProgress, Is.EqualTo(previous));
        Assert.That((await Current()).LocalPath, Is.EqualTo(file));
        Assert.That(Hash(), Is.EqualTo(hash));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ForgetOrRemove_CascadesPositionAndPreservesActualFile(bool remove)
    {
        byte[] hash = Hash();
        if (remove) await repository.RemoveSubscriptionAsync(subscription.Id);
        else await repository.ClearDownloadAsync(subscription.Id, episode.EpisodeId);
        Assert.That(await Count("podcast_playback_positions"), Is.Zero);
        if (!remove)
        {
            await repository.RecordDownloadAsync(subscription.Id, episode.EpisodeId, file, new FileInfo(file).Length, now);
            Assert.That((await Current()).SavedProgress, Is.Null, "redownload never resurrects old progress");
        }
        Assert.That(Hash(), Is.EqualTo(hash));
    }

    [Test]
    public async Task RecordDownload_SamePathNewDownload_ClearsOldPositionWithoutChangingFile()
    {
        byte[] hash = Hash();
        await repository.RecordDownloadAsync(subscription.Id, episode.EpisodeId, file, new FileInfo(file).Length, now.AddMinutes(1));
        Assert.That((await Current()).SavedProgress, Is.Null);
        Assert.That(Hash(), Is.EqualTo(hash));
    }

    [Test]
    public async Task Save_TransactionTriggerFailure_KeepsPrevious()
    {
        await Execute("CREATE TRIGGER reject_position BEFORE UPDATE ON podcast_playback_positions BEGIN SELECT RAISE(ABORT,'test rejection'); END;");
        Assert.That(async () => await repository.SaveEpisodePositionAsync(subscription.Id, episode.EpisodeId, file,
            previous with { Position = TimeSpan.FromSeconds(2) }), Throws.TypeOf<SqliteException>());
        Assert.That((await Current()).SavedProgress, Is.EqualTo(previous));
    }

    [Test]
    public async Task Save_InvalidProgressOrCanceledRequest_KeepsPrevious()
    {
        Assert.That(async () => await repository.SaveEpisodePositionAsync(subscription.Id, episode.EpisodeId, file,
            previous with { Position = TimeSpan.FromTicks(-1) }), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(async () => await repository.SaveEpisodePositionAsync(subscription.Id, episode.EpisodeId, " ", previous),
            Throws.TypeOf<ArgumentException>());
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        Assert.That(async () => await repository.SaveEpisodePositionAsync(subscription.Id, episode.EpisodeId, file, previous, cancel.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That((await Current()).SavedProgress, Is.EqualTo(previous));
    }

    [Test]
    public async Task Save_RealAtlMetadataAndPlayerSnapshot_ClampsToActualDurationAndKeepsBytes()
    {
        byte[] hash = Hash();
        AtlMediaMetadataReader metadata = new();
        TimeSpan duration = (await metadata.ReadAsync(new Uri(file))).Duration;
        StoredPodcastEpisode saved = await Service(metadata).SavePositionAsync(episode,
            new Source(new(new Uri(file), TimeSpan.FromHours(1), true)));
        Assert.That(saved.SavedProgress!.Position, Is.EqualTo(duration));
        Assert.That(saved.SavedProgress.Duration, Is.EqualTo(duration));
        Assert.That((await Current()).SavedProgress, Is.EqualTo(saved.SavedProgress));
        Assert.That(Hash(), Is.EqualTo(hash));
    }

    [TestCase("wrong-file")]
    [TestCase("stopped")]
    [TestCase("no-track")]
    [TestCase("changed-player")]
    public async Task Save_IneligiblePlayer_KeepsPrevious(string state)
    {
        PodcastPlaybackSnapshot? initial = state == "no-track" ? null : new(
            new Uri(state == "wrong-file" ? file + ".other" : file), TimeSpan.FromSeconds(1), state != "stopped");
        Source source = new(initial);
        if (state == "changed-player") source.Next = new(new Uri(file + ".other"), TimeSpan.Zero, true);
        Assert.That(async () => await Service(new Metadata()).SavePositionAsync(episode, source), Throws.TypeOf<InvalidOperationException>());
        Assert.That((await Current()).SavedProgress, Is.EqualTo(previous));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task Save_InvalidActualDuration_KeepsPrevious(int seconds)
    {
        Assert.That(async () => await Service(new Metadata { Duration = TimeSpan.FromSeconds(seconds) }).SavePositionAsync(episode,
            new Source(new(new Uri(file), TimeSpan.FromSeconds(1), true))), Throws.TypeOf<InvalidDataException>());
        Assert.That((await Current()).SavedProgress, Is.EqualTo(previous));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Save_MissingOrUnreadableFile_KeepsPrevious(bool unreadable)
    {
        if (unreadable) await File.WriteAllTextAsync(file, "invalid wave data"); else File.Delete(file);
        Assert.That(async () => await Service(new AtlMediaMetadataReader()).SavePositionAsync(episode,
            new Source(new(new Uri(file), TimeSpan.FromSeconds(1), true))), unreadable ? Throws.TypeOf<InvalidDataException>() : Throws.TypeOf<FileNotFoundException>());
        Assert.That((await Current()).SavedProgress, Is.EqualTo(previous));
    }

    [TestCase("save-metadata")]
    [TestCase("play-metadata")]
    [TestCase("save-snapshot")]
    [TestCase("save-second-snapshot")]
    public async Task Cancellation_UncooperativePort_NeverWritesOrHandsOff(string stage)
    {
        using CancellationTokenSource cancel = new();
        Metadata metadata = new(); Source source = new(new(new Uri(file), TimeSpan.FromSeconds(1), true));
        Handoff handoff = new();
        if (stage.EndsWith("metadata", StringComparison.Ordinal)) metadata.OnRead = cancel.Cancel;
        else source.OnRead = () => { if (stage != "save-second-snapshot" || source.Reads == 2) cancel.Cancel(); };
        PodcastService service = Service(metadata);
        if (stage == "play-metadata")
            Assert.That(async () => await service.PlayAsync(subscription, episode, handoff, true, cancel.Token), Throws.InstanceOf<OperationCanceledException>());
        else
            Assert.That(async () => await service.SavePositionAsync(episode, source, cancel.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(handoff.Requests, Is.Empty);
        Assert.That((await Current()).SavedProgress, Is.EqualTo(previous));
    }

    [TestCase(false, 5, 1)]
    [TestCase(true, 5, 1)]
    [TestCase(true, 1, 1)]
    [TestCase(true, 1, 2)]
    public async Task Play_ExplicitResumeOrStart_UsesActualDuration(bool resume, int durationSeconds, int positionSeconds)
    {
        episode = episode with { SavedProgress = new(TimeSpan.FromSeconds(positionSeconds), TimeSpan.FromSeconds(10), now) };
        await repository.SaveEpisodePositionAsync(subscription.Id, episode.EpisodeId, file, episode.SavedProgress!);
        Metadata metadata = new() { Duration = TimeSpan.FromSeconds(durationSeconds) };
        Handoff handoff = new();
        await Service(metadata).PlayAsync(subscription, episode, handoff, resume);
        TimeSpan expected = resume && positionSeconds < durationSeconds ? TimeSpan.FromSeconds(positionSeconds) : TimeSpan.Zero;
        Assert.That(handoff.Requests.Single().StartPosition, Is.EqualTo(expected));
        Assert.That(handoff.Requests.Single().File, Is.EqualTo(new Uri(file)));
        Assert.That(metadata.Reads, Is.EqualTo(resume ? 1 : 0));
    }

    [TestCase(-1, 10, 0, 10)]
    [TestCase(100, 40, 30, 30)]
    public async Task Save_UnboundedPlayerPositionOrDuration_ClampsSafely(int positionDays, int durationDays, int expectedPositionDays, int expectedDurationDays)
    {
        StoredPodcastEpisode saved = await Service(new Metadata { Duration = TimeSpan.FromDays(durationDays) }).SavePositionAsync(episode,
            new Source(new(new Uri(file), TimeSpan.FromDays(positionDays), true)));
        Assert.That(saved.SavedProgress!.Position, Is.EqualTo(TimeSpan.FromDays(expectedPositionDays)));
        Assert.That(saved.SavedProgress.Duration, Is.EqualTo(TimeSpan.FromDays(expectedDurationDays)));
    }

    [Test]
    public async Task Save_RepositoryPathChangesDuringMetadata_NeverRecordsAgainstReplacement()
    {
        Metadata metadata = new() { OnRead = () => repository.ClearDownloadAsync(subscription.Id, episode.EpisodeId).GetAwaiter().GetResult() };
        Assert.That(async () => await Service(metadata).SavePositionAsync(episode,
            new Source(new(new Uri(file), TimeSpan.FromSeconds(1), true))), Throws.TypeOf<InvalidOperationException>());
        Assert.That((await Current()).SavedProgress, Is.Null);
        Assert.That(await Count("podcast_playback_positions"), Is.Zero);
        Assert.That(File.Exists(file), Is.True);
    }

    [Test]
    public async Task Save_FileDisappearsDuringMetadata_KeepsPrevious()
    {
        Metadata metadata = new() { OnRead = () => File.Delete(file) };
        Assert.That(async () => await Service(metadata).SavePositionAsync(episode,
            new Source(new(new Uri(file), TimeSpan.FromSeconds(1), true))), Throws.TypeOf<InvalidOperationException>());
        Assert.That((await Current()).SavedProgress, Is.EqualTo(previous));
    }

    [Test]
    public async Task Save_UnavailableOrNullSource_FailsBeforeChangingPrevious()
    {
        PodcastService unavailable = new(repository, new OfflineFeed(), new OfflineDownloader(),
            new PodcastDownloadStore(Path.Combine(directory, "downloads")));
        Assert.That(async () => await unavailable.SavePositionAsync(episode, new Source(null)), Throws.TypeOf<InvalidOperationException>());
        Assert.That(async () => await Service(new Metadata()).SavePositionAsync(episode, null!), Throws.TypeOf<ArgumentNullException>());
        Assert.That((await Current()).SavedProgress, Is.EqualTo(previous));
    }

    [Test]
    public async Task Play_NoSavedPosition_DoesNotReadMetadataOrInventResume()
    {
        Handoff handoff = new(); Metadata metadata = new();
        await Execute("DELETE FROM podcast_playback_positions;");
        await Service(metadata).PlayAsync(subscription, episode with { SavedProgress = null }, handoff, true);
        Assert.That(metadata.Reads, Is.Zero);
        Assert.That(handoff.Requests.Single().StartPosition, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void Play_UnavailableResumeOrMissingFileOrNullHandoff_NeverHandsOff()
    {
        Handoff handoff = new();
        PodcastService unavailable = new(repository, new OfflineFeed(), new OfflineDownloader(),
            new PodcastDownloadStore(Path.Combine(directory, "downloads")));
        Assert.That(async () => await unavailable.PlayAsync(subscription, episode, handoff, true), Throws.TypeOf<InvalidOperationException>());
        Assert.That(async () => await Service(new Metadata()).PlayAsync(subscription, episode, null!, true), Throws.TypeOf<ArgumentNullException>());
        File.Delete(file);
        Assert.That(async () => await Service(new Metadata()).PlayAsync(subscription, episode, handoff, true), Throws.TypeOf<FileNotFoundException>());
        Assert.That(handoff.Requests, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SaveOrPlay_PreCanceled_DoesNotConsultExternalPorts(bool play)
    {
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        Metadata metadata = new(); Source source = new(null); Handoff handoff = new();
        if (play)
            Assert.That(async () => await Service(metadata).PlayAsync(subscription, episode, handoff, true, cancel.Token), Throws.InstanceOf<OperationCanceledException>());
        else
            Assert.That(async () => await Service(metadata).SavePositionAsync(episode, source, cancel.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(source.Reads, Is.Zero);
        Assert.That(metadata.Reads, Is.Zero);
        Assert.That(handoff.Requests, Is.Empty);
    }

    [Test]
    public void SupportsPositions_RequiresMetadataAndRepository()
    {
        PodcastDownloadStore store = new(Path.Combine(directory, "downloads"));
        Assert.That(new PodcastService(repository, new OfflineFeed(), new OfflineDownloader(), store).SupportsPositions, Is.False);
        Assert.That(new PodcastService(repository, new OfflineFeed(), new OfflineDownloader(), store, positions: repository).SupportsPositions, Is.False);
        Assert.That(new PodcastService(repository, new OfflineFeed(), new OfflineDownloader(), store, metadata: new Metadata()).SupportsPositions, Is.False);
        Assert.That(Service(new Metadata()).SupportsPositions, Is.True);
    }

    private PodcastService Service(IMediaMetadataReader metadata) => new(repository, new OfflineFeed(), new OfflineDownloader(),
        new PodcastDownloadStore(Path.Combine(directory, "downloads")), positions: repository, metadata: metadata);
    private async Task<StoredPodcastEpisode> Current() => (await repository.ListEpisodesAsync(subscription.Id)).Single();
    private byte[] Hash() => SHA256.HashData(File.ReadAllBytes(file));
    private async Task<long> Count(string table)
    {
        await using SqliteConnection connection = new($"Data Source={database};Pooling=False"); await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)(await command.ExecuteScalarAsync())!;
    }
    private async Task Execute(string sql)
    {
        await using SqliteConnection connection = new($"Data Source={database};Pooling=False"); await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private sealed class Metadata : IMediaMetadataReader
    {
        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);
        public Action? OnRead { get; set; }
        public int Reads { get; private set; }
        public Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default)
        { Reads++; OnRead?.Invoke(); return Task.FromResult(new MediaMetadata("Episode", "Show", null, Duration)); }
    }
    private sealed class Source : IPodcastPositionSource
    {
        private readonly PodcastPlaybackSnapshot? initial;
        public Source(PodcastPlaybackSnapshot? initial) { this.initial = initial; Next = initial; }
        public PodcastPlaybackSnapshot? Next { get; set; }
        public Action? OnRead { get; set; }
        public int Reads { get; private set; }
        public ValueTask<PodcastPlaybackSnapshot?> GetPodcastPlaybackAsync(CancellationToken cancellationToken = default)
        { Reads++; OnRead?.Invoke(); return ValueTask.FromResult(Reads == 1 ? initial : Next); }
    }
    private sealed class Handoff : IPodcastPlaybackHandoff
    {
        public List<PodcastPlaybackRequest> Requests { get; } = [];
        public Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.CompletedTask; }
    }
    private sealed class ConditionalFeed(Uri url) : IPodcastFeedReader
    {
        public List<PodcastFeedRequest> Requests { get; } = [];
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Requests.Count == 1
                ? new PodcastFeedResult(new PodcastFeed(url, "Updated show", "", [new PodcastEpisode("episode", "Updated episode", new Uri("https://cdn.example/episode.wav"))]), [], EntityTag: "\"positions-v2\"")
                : new PodcastFeedResult(null, [], NotModified: true));
        }
    }
    private sealed class OfflineFeed : IPodcastFeedReader
    {
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Position operations must not fetch a feed.");
    }
    private sealed class OfflineDownloader : IPodcastEpisodeDownloader
    {
        public Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Position operations must not download an episode.");
    }
}
