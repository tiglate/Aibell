using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Storage;

public sealed partial class SchemaMigrationTests
{
    [Test]
    public async Task InitializeAsync_SchemaEleven_PreservesMusicRadioPodcastsAndAddsShelf()
    {
        await CreatePreBaselineAsync(5);
        await using (SqliteTrackRepository current = new(databasePath))
        {
            await current.ListAsync();
            await current.AddStationAsync(new(Guid.NewGuid(), "Keep radio", new Uri("https://example.invalid/live")));
            await current.AddSubscriptionAsync(new(Guid.NewGuid(), new Uri("https://example.invalid/feed"), "Keep podcast", "", DateTimeOffset.UtcNow), []);
        }
        await ScalarAsync(DropCatalogColumns + " ALTER TABLE podcast_episodes DROP COLUMN is_new; ALTER TABLE podcast_episodes DROP COLUMN image_url; ALTER TABLE podcast_episodes DROP COLUMN rating; DROP TABLE podcast_playback_positions; DROP TRIGGER clear_podcast_position_after_download_change; DROP TABLE audiobook_shelf; PRAGMA user_version=11;");
        await using (SqliteTrackRepository upgraded = new(databasePath))
        {
            Assert.That(await upgraded.ListShelfAsync(), Is.Empty);
            Assert.That((await upgraded.ListAsync()).Single().Id, Is.EqualTo(trackId));
            Assert.That(await upgraded.GetTracksAsync(playlistId, ""), Has.Count.EqualTo(2));
            Assert.That((await upgraded.ListStationsAsync()).Single().Name, Is.EqualTo("Keep radio"));
            Assert.That((await upgraded.ListSubscriptionsAsync()).Single().Title, Is.EqualTo("Keep podcast"));
            Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
            Assert.That(await ScalarAsync("PRAGMA foreign_key_check;"), Is.Null);
        }
        byte[] hash = await DatabaseFileSnapshot.HashAsync(databasePath);
        await using (SqliteTrackRepository reopened = new(databasePath)) Assert.That(await reopened.ListShelfAsync(), Is.Empty);
        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(hash));
    }

    [Test]
    public async Task InitializeAsync_AudiobookMigrationFails_PreservesVersionElevenAndRetries()
    {
        await CreatePreBaselineAsync(5);
        await using (SqliteTrackRepository current = new(databasePath)) await current.ListAsync();
        await ScalarAsync(DropCatalogColumns + " ALTER TABLE podcast_episodes DROP COLUMN is_new; ALTER TABLE podcast_episodes DROP COLUMN image_url; ALTER TABLE podcast_episodes DROP COLUMN rating; DROP TABLE podcast_playback_positions; DROP TRIGGER clear_podcast_position_after_download_change; DROP TABLE audiobook_shelf; CREATE TABLE audiobook_shelf(unexpected TEXT); PRAGMA user_version=11;");
        byte[] originalHash = await DatabaseFileSnapshot.HashAsync(databasePath);
        await using SqliteTrackRepository upgraded = new(databasePath);
        Assert.That(async () => await upgraded.ListShelfAsync(), Throws.TypeOf<Microsoft.Data.Sqlite.SqliteException>());
        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(originalHash));
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(11L));
        await ScalarAsync("DROP TABLE audiobook_shelf;");
        Assert.That(await upgraded.ListShelfAsync(), Is.Empty);
        Assert.That((await upgraded.ListAsync()).Single().Id, Is.EqualTo(trackId));
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
    }
}
