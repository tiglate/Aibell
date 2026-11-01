using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Storage;

public sealed partial class SchemaMigrationTests
{
    [Test]
    public async Task InitializeAsync_SchemaThirteen_AddsUnratedEpisodesAndPreservesEveryCatalogAndPosition()
    {
        await CreatePreBaselineAsync(5);
        Guid subscription = Guid.NewGuid();
        DateTimeOffset timestamp = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        PodcastPlaybackProgress progress = new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(15), timestamp);
        Uri book = new("file:///fixture/keep.m4b");
        await using (SqliteTrackRepository repository = new(databasePath))
        {
            await repository.AddStationAsync(new(Guid.NewGuid(), "Keep radio", new Uri("https://example.invalid/live")));
            await repository.AddSubscriptionAsync(new(subscription, new Uri("https://example.invalid/feed"), "Keep show", "", timestamp),
                [new(subscription, "episode", "Keep episode", new Uri("https://example.invalid/audio.mp3"))]);
            await repository.RecordDownloadAsync(subscription, "episode", "/keep/download.mp3", 123, timestamp);
            await repository.SaveEpisodePositionAsync(subscription, "episode", "/keep/download.mp3", progress);
            await repository.AddToShelfAsync(new(book, "Keep book", "Author", TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(3), timestamp, timestamp));
        }
        await ScalarAsync(DropCatalogColumns + " ALTER TABLE podcast_episodes DROP COLUMN is_new; ALTER TABLE podcast_episodes DROP COLUMN image_url; ALTER TABLE podcast_episodes DROP COLUMN rating; PRAGMA user_version=13;");
        await using (SqliteTrackRepository upgraded = new(databasePath))
        {
            Assert.That((await upgraded.ListAsync()).Single().Id, Is.EqualTo(trackId));
            Assert.That(await upgraded.GetTracksAsync(playlistId, ""), Has.Count.EqualTo(2));
            Assert.That((await upgraded.ListStationsAsync()).Single().Name, Is.EqualTo("Keep radio"));
            StoredPodcastEpisode episode = (await upgraded.ListEpisodesAsync(subscription)).Single();
            Assert.That(episode.Rating, Is.Zero);
            Assert.That(episode.LocalPath, Is.EqualTo("/keep/download.mp3"));
            Assert.That(episode.DownloadedBytes, Is.EqualTo(123));
            Assert.That(episode.SavedProgress, Is.EqualTo(progress));
            Assert.That((await upgraded.ListShelfAsync()).Single().SavedPosition, Is.EqualTo(TimeSpan.FromMinutes(3)));
            Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
            Assert.That(await ScalarAsync("PRAGMA foreign_key_check;"), Is.Null);
        }
        byte[] hash = await DatabaseFileSnapshot.HashAsync(databasePath);
        await using (SqliteTrackRepository reopened = new(databasePath)) await reopened.ListSubscriptionsAsync();
        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(hash));
    }

    [Test]
    public async Task InitializeAsync_RatingMigrationFailure_RollsBackBytesAndRetriesAfterRepair()
    {
        await CreatePreBaselineAsync(5);
        await using (SqliteTrackRepository current = new(databasePath)) await current.ListAsync();
        // Incompatible schema13 shape: a pre-existing unexpected column must never be silently accepted.
        await ScalarAsync(DropCatalogColumns + " PRAGMA user_version=13;");
        byte[] hash = await DatabaseFileSnapshot.HashAsync(databasePath);
        await using SqliteTrackRepository upgraded = new(databasePath);
        Assert.That(async () => await upgraded.ListSubscriptionsAsync(), Throws.TypeOf<Microsoft.Data.Sqlite.SqliteException>());
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(13L));
        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(hash));
        await ScalarAsync("ALTER TABLE podcast_episodes DROP COLUMN is_new; ALTER TABLE podcast_episodes DROP COLUMN image_url; ALTER TABLE podcast_episodes DROP COLUMN rating;");
        Assert.That(await upgraded.ListSubscriptionsAsync(), Is.Empty);
        Assert.That((await upgraded.ListAsync()).Single().Id, Is.EqualTo(trackId));
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
    }

    [Test]
    public async Task InitializeAsync_SchemaFifteen_ForgetsFeedValidatorsSoCoversArrive()
    {
        await CreatePreBaselineAsync(5);
        Guid subscription = Guid.NewGuid();
        await using (SqliteTrackRepository repository = new(databasePath))
        {
            await repository.AddSubscriptionAsync(new(subscription, new Uri("https://example.invalid/feed"), "Show", "", DateTimeOffset.UnixEpoch,
                EntityTag: "\"v1\"", LastModified: DateTimeOffset.UnixEpoch), []);
        }
        await ScalarAsync(DropCatalogColumns + " ALTER TABLE podcast_episodes DROP COLUMN is_new; PRAGMA user_version=15;");
        await using SqliteTrackRepository upgraded = new(databasePath);
        PodcastSubscription restored = (await upgraded.ListSubscriptionsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(restored.EntityTag, Is.Null);
            Assert.That(restored.LastModified, Is.Null);
        });
    }

    [Test]
    public async Task InitializeAsync_SchemaFourteen_AddsEpisodeImageColumnAndKeepsEpisodeState()
    {
        await CreatePreBaselineAsync(5);
        Guid subscription = Guid.NewGuid();
        await using (SqliteTrackRepository repository = new(databasePath))
        {
            await repository.AddSubscriptionAsync(new(subscription, new Uri("https://example.invalid/feed"), "Show", "", DateTimeOffset.UnixEpoch),
                [new(subscription, "episode", "Episode", new Uri("https://example.invalid/audio.mp3"), Rating: 3)]);
        }
        await ScalarAsync(DropCatalogColumns + " ALTER TABLE podcast_episodes DROP COLUMN is_new; ALTER TABLE podcast_episodes DROP COLUMN image_url; PRAGMA user_version=14;");
        await using SqliteTrackRepository upgraded = new(databasePath);
        StoredPodcastEpisode episode = (await upgraded.ListEpisodesAsync(subscription)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(episode.Rating, Is.EqualTo(3));
            Assert.That(episode.ImageUrl, Is.Null);
        });
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
    }
}
