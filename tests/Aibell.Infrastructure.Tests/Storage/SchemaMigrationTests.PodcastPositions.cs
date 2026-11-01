using Aibell.Domain.Audiobooks;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Storage;

public sealed partial class SchemaMigrationTests
{
    [Test]
    public async Task InitializeAsync_SchemaTwelve_AddsPositionsAndPreservesEveryCatalog()
    {
        await CreatePreBaselineAsync(5);
        Guid subscriptionId = Guid.NewGuid();
        Uri book = new("file:///fixture/book.m4b");
        await using (SqliteTrackRepository current = new(databasePath))
        {
            await current.ListAsync();
            await current.AddStationAsync(new(Guid.NewGuid(), "Keep radio", new Uri("https://example.invalid/live")));
            await current.AddSubscriptionAsync(new(subscriptionId, new Uri("https://example.invalid/feed"), "Keep podcast", "", DateTimeOffset.UtcNow),
                [new(subscriptionId, "episode", "Keep episode", new Uri("https://example.invalid/audio.mp3"))]);
            await current.AddToShelfAsync(new(book, "Keep book", "Author", TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(3), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
        await ScalarAsync(DropCatalogColumns + " ALTER TABLE podcast_episodes DROP COLUMN is_new; ALTER TABLE podcast_episodes DROP COLUMN image_url; ALTER TABLE podcast_episodes DROP COLUMN rating; DROP TABLE podcast_playback_positions; DROP TRIGGER clear_podcast_position_after_download_change; PRAGMA user_version=12;");
        await using (SqliteTrackRepository upgraded = new(databasePath))
        {
            Assert.That((await upgraded.ListAsync()).Single().Id, Is.EqualTo(trackId));
            Assert.That(await upgraded.GetTracksAsync(playlistId, ""), Has.Count.EqualTo(2));
            Assert.That((await upgraded.ListStationsAsync()).Single().Name, Is.EqualTo("Keep radio"));
            Assert.That((await upgraded.ListEpisodesAsync(subscriptionId)).Single().SavedProgress, Is.Null);
            Assert.That((await upgraded.ListShelfAsync()).Single().SavedPosition, Is.EqualTo(TimeSpan.FromMinutes(3)));
            Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
            Assert.That(await ScalarAsync("PRAGMA foreign_key_check;"), Is.Null);
        }
        byte[] hash = await DatabaseFileSnapshot.HashAsync(databasePath);
        await using (SqliteTrackRepository reopened = new(databasePath)) await reopened.ListSubscriptionsAsync();
        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(hash));
    }

    [Test]
    public async Task InitializeAsync_PositionMigrationFails_PreservesVersionTwelveBytesAndRetries()
    {
        await CreatePreBaselineAsync(5);
        await using (SqliteTrackRepository current = new(databasePath)) await current.ListAsync();
        await ScalarAsync(DropCatalogColumns + " ALTER TABLE podcast_episodes DROP COLUMN is_new; ALTER TABLE podcast_episodes DROP COLUMN image_url; ALTER TABLE podcast_episodes DROP COLUMN rating; DROP TABLE podcast_playback_positions; DROP TRIGGER clear_podcast_position_after_download_change; CREATE TABLE podcast_playback_positions(unexpected TEXT); PRAGMA user_version=12;");
        byte[] hash = await DatabaseFileSnapshot.HashAsync(databasePath);
        await using SqliteTrackRepository upgraded = new(databasePath);
        Assert.That(async () => await upgraded.ListSubscriptionsAsync(), Throws.TypeOf<Microsoft.Data.Sqlite.SqliteException>());
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(12L));
        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(hash));
        await ScalarAsync("DROP TABLE podcast_playback_positions;");
        Assert.That(await upgraded.ListSubscriptionsAsync(), Is.Empty);
        Assert.That((await upgraded.ListAsync()).Single().Id, Is.EqualTo(trackId));
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
    }
}
