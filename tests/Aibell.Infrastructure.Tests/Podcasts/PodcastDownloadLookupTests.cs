using System.Security.Cryptography;
using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Podcasts;

[TestFixture]
public sealed class PodcastDownloadLookupTests
{
    private string directory = null!;
    private string database = null!;
    private string originalRoot = null!;
    private string file = null!;
    private PodcastSubscription subscription = null!;
    private StoredPodcastEpisode episode = null!;
    private SqliteTrackRepository repository = null!;

    [SetUp]
    public async Task SetUp()
    {
        directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-podcast-lookup-" + Guid.NewGuid().ToString("N"))).FullName;
        database = Path.Combine(directory, "profile.db");
        originalRoot = Path.Combine(directory, "old");
        repository = new(database);
        subscription = new(Guid.NewGuid(), new Uri("https://example.invalid/feed"), "Show", "", DateTimeOffset.UtcNow);
        episode = new(subscription.Id, "episode", "Episode", new Uri("https://example.invalid/audio.mp3"));
        await repository.AddSubscriptionAsync(subscription, [episode]);
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(episode.EpisodeId)))[..16].ToLowerInvariant();
        file = new PodcastDownloadStore(originalRoot).PrepareDestination(subscription.Id, hash + ".mp3").LocalPath;
        await File.WriteAllTextAsync(file, "owned downloaded bytes");
        await repository.RecordDownloadAsync(subscription.Id, episode.EpisodeId, file, new FileInfo(file).Length, DateTimeOffset.UtcNow);
        await repository.SetEpisodeRatingAsync(subscription.Id, episode.EpisodeId, 4);
        await repository.SaveEpisodePositionAsync(subscription.Id, episode.EpisodeId, file,
            new(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(20), DateTimeOffset.UtcNow));
        episode = (await repository.ListEpisodesAsync(subscription.Id)).Single();
    }

    [TearDown]
    public async Task TearDown()
    {
        await repository.DisposeAsync();
        Directory.Delete(directory, true);
    }

    [Test]
    public void StageDelete_ChangedToParentRoot_StillRecognizesHistoricalManagedLayout()
    {
        PodcastDownloadStore store = new(originalRoot);
        store.ConfigureRoot(directory, [originalRoot]);
        Assert.That(store.OwnsDownload(subscription.Id, episode.EpisodeId, file), Is.True);
        using (var deletion = store.StageDelete(subscription.Id, episode.EpisodeId, file))
        {
            Assert.That(File.Exists(file), Is.False);
            deletion.Complete();
        }
        Assert.That(File.Exists(file), Is.False);
        string arbitrary = Path.Combine(originalRoot, "keep.mp3"); File.WriteAllText(arbitrary, "Music");
        Assert.That(() => store.StageDelete(subscription.Id, episode.EpisodeId, arbitrary), Throws.TypeOf<IOException>());
        Assert.That(File.ReadAllText(arbitrary), Is.EqualTo("Music"));
    }

    [Test]
    public async Task Lookup_FirstEverQueuedCacheAfterReopen_ReturnsFullCurrentIdentityWithoutPlaybackOrWrites()
    {
        await repository.DisposeAsync(); repository = new(database);
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(file));
        PodcastService service = Service(new PodcastDownloadStore(directory, [originalRoot]));
        Assert.That(await service.FindDownloadedEpisodeAsync(new Uri(file)), Is.EqualTo(episode));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(file)), Is.EqualTo(before));
        Assert.That(await repository.ListAsync(), Is.Empty);
        Assert.That((await repository.ListEpisodesAsync(subscription.Id)).Single(), Is.EqualTo(episode));
        Assert.That(await service.FindDownloadedEpisodeAsync(new Uri("https://example.invalid/audio.mp3")), Is.Null);
        Assert.That(await service.FindDownloadedEpisodeAsync(new Uri("relative", UriKind.Relative)), Is.Null);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.That(async () => await service.FindDownloadedEpisodeAsync(new Uri(file), canceled.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [TestCase("missing")]
    [TestCase("outside")]
    [TestCase("arbitrary-name")]
    [TestCase("duplicate-identity")]
    public async Task Lookup_UnsafeMissingOrAmbiguousRecordedPath_DoesNotClaimMusicOrMutateRows(string invalid)
    {
        string requested = file;
        if (invalid == "missing") File.Delete(file);
        else if (invalid == "duplicate-identity")
        {
            await repository.ApplyRefreshAsync(subscription, [episode with { EpisodeId = "second" }]);
            await repository.RecordDownloadAsync(subscription.Id, "second", file, new FileInfo(file).Length, DateTimeOffset.UtcNow);
        }
        else
        {
            requested = Path.Combine(invalid == "outside" ? directory : originalRoot, "precious.mp3");
            await File.WriteAllTextAsync(requested, "Music bytes");
            await repository.RecordDownloadAsync(subscription.Id, episode.EpisodeId, requested, 11, DateTimeOffset.UtcNow);
        }
        var before = await repository.ListEpisodesAsync(subscription.Id);
        Assert.That(await Service(new PodcastDownloadStore(originalRoot)).FindDownloadedEpisodeAsync(new Uri(requested)), Is.Null);
        Assert.That(await repository.ListEpisodesAsync(subscription.Id), Is.EqualTo(before));
        if (invalid is "outside" or "arbitrary-name") Assert.That(await File.ReadAllTextAsync(requested), Is.EqualTo("Music bytes"));
    }

    [Test]
    [Platform("Linux")]
    public async Task Lookup_LinkedRecordedDownload_DoesNotFollowOutsideTarget()
    {
        string outside = Path.Combine(directory, "outside.mp3"); await File.WriteAllTextAsync(outside, "keep");
        File.Delete(file); File.CreateSymbolicLink(file, outside);
        Assert.That(await Service(new PodcastDownloadStore(originalRoot)).FindDownloadedEpisodeAsync(new Uri(file)), Is.Null);
        Assert.That(await File.ReadAllTextAsync(outside), Is.EqualTo("keep"));
        Assert.That((await repository.ListEpisodesAsync(subscription.Id)).Single(), Is.EqualTo(episode));
    }

    [Test]
    public async Task Lookup_PathCasing_UsesFilesystemPlatformSemantics()
    {
        Assert.That(await Service(new PodcastDownloadStore(originalRoot)).FindDownloadedEpisodeAsync(new Uri(file.ToUpperInvariant())),
            OperatingSystem.IsWindows() ? Is.EqualTo(episode) : Is.Null);
    }

    private PodcastService Service(PodcastDownloadStore store) => new(repository, new NoTransport(), new NoTransport(), store);
    private sealed class NoTransport : IPodcastFeedReader, IPodcastEpisodeDownloader
    {
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Lookup must not fetch a feed.");
        public Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Lookup must not download or play media.");
    }
}
