using System.Security.Cryptography;
using Aibell.Application.Playback;
using Aibell.Application.Podcasts;
using Aibell.Desktop.Services;
using Aibell.Domain.Media;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Desktop.Tests;

[TestFixture]
public sealed class PodcastProgressPersistenceTests
{
    private string directory = null!;
    [SetUp] public void SetUp() => directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-auto-progress-" + Guid.NewGuid().ToString("N"))).FullName;
    [TearDown] public void TearDown() => Directory.Delete(directory, true);

    [Test]
    public async Task AutomaticPosition_FlushThenReopen_PersistsExactTicksWithoutChangingSourceOrMusic()
    {
        string database = Path.Combine(directory, "profile.db");
        string file = Path.Combine(directory, "episode.mp3");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.mp3"), file);
        byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(file));
        Guid id = Guid.NewGuid();
        TimeSpan position = TimeSpan.FromTicks(1234567), duration = TimeSpan.FromSeconds(10);
        await using (SqliteTrackRepository repository = new(database))
        {
            StoredPodcastEpisode episode = await SeedAsync(repository, id, file);
            PodcastService service = new(repository, new OfflineTransport(), new OfflineTransport(), new PodcastDownloadStore(directory));
            await using PodcastProgressCoordinator progress = new(service.SaveAutomaticPositionAsync, startTimer: false);
            progress.Register(episode, new Uri(file));
            PlaybackSnapshot snapshot = new(PlaybackStatus.Playing,
                new Track(Guid.NewGuid(), new Uri(file), "Episode", "Show", "Show", duration), position, .5);
            progress.Observe(snapshot);
            await progress.ShutdownAsync(snapshot);
            progress.Observe(snapshot with { Position = TimeSpan.Zero });
            Assert.That(await repository.ListAsync(), Is.Empty);
        }
        await using SqliteTrackRepository reopened = new(database);
        PodcastPlaybackProgress restored = (await reopened.ListEpisodesAsync(id)).Single().SavedProgress!;
        Assert.That(restored.Position, Is.EqualTo(position));
        Assert.That(restored.Duration, Is.EqualTo(duration));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(file)), Is.EqualTo(hash));
    }

    [TestCase("redownload")]
    [TestCase("clear")]
    [TestCase("remove")]
    [TestCase("missing")]
    public async Task StaleSession_AfterDownloadOrSubscriptionChange_CannotResurrectProgress(string change)
    {
        string file = Path.Combine(directory, "episode.mp3"); File.WriteAllText(file, "original bytes");
        await using SqliteTrackRepository repository = new(Path.Combine(directory, "profile.db"));
        Guid id = Guid.NewGuid(); StoredPodcastEpisode episode = await SeedAsync(repository, id, file);
        PodcastService service = new(repository, new OfflineTransport(), new OfflineTransport(), new PodcastDownloadStore(directory));
        await using PodcastProgressCoordinator progress = new(service.SaveAutomaticPositionAsync, startTimer: false);
        progress.Register(episode, new Uri(file));
        PlaybackSnapshot snapshot = new(PlaybackStatus.Playing,
            new Track(Guid.NewGuid(), new Uri(file), "Episode", "Show", "Show", TimeSpan.FromMinutes(10)), TimeSpan.FromSeconds(10), .5);
        progress.Observe(snapshot); await progress.TickAsync();
        if (change == "redownload") await repository.RecordDownloadAsync(id, episode.EpisodeId, file, 101, DateTimeOffset.UtcNow.AddDays(1));
        else if (change == "clear") await repository.ClearDownloadAsync(id, episode.EpisodeId);
        else if (change == "remove") await repository.RemoveSubscriptionAsync(id);
        else File.Delete(file);
        progress.Observe(snapshot with { Position = TimeSpan.FromSeconds(20) }); await progress.TickAsync();
        Assert.That(progress.LastError, Is.Not.Null);
        IReadOnlyList<StoredPodcastEpisode> remaining = await repository.ListEpisodesAsync(id);
        if (change == "remove") Assert.That(remaining, Is.Empty);
        else if (change == "missing") Assert.That(remaining.Single().SavedProgress!.Position, Is.EqualTo(TimeSpan.FromSeconds(10)));
        else Assert.That(remaining.Single().SavedProgress, Is.Null);
        if (change != "missing") Assert.That(File.ReadAllText(file), Is.EqualTo("original bytes"));
    }

    private static async Task<StoredPodcastEpisode> SeedAsync(SqliteTrackRepository repository, Guid id, string file)
    {
        StoredPodcastEpisode episode = new(id, "episode", "Episode", new Uri("https://example.invalid/episode.mp3"));
        await repository.AddSubscriptionAsync(new(id, new Uri("https://example.invalid/feed"), "Show", "", DateTimeOffset.UtcNow), [episode]);
        await repository.RecordDownloadAsync(id, episode.EpisodeId, file, new FileInfo(file).Length, DateTimeOffset.UnixEpoch);
        return (await repository.ListEpisodesAsync(id)).Single();
    }

    private sealed class OfflineTransport : IPodcastFeedReader, IPodcastEpisodeDownloader
    {
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Automatic progress must never fetch feeds.");
        public Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Automatic progress must never download media.");
    }
}
