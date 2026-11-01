using Aibell.Domain.Radio;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Storage;

public sealed partial class SchemaMigrationTests
{
    private const string DropCatalogColumns =
        "DROP INDEX ix_radio_stations_catalog_id; ALTER TABLE radio_stations DROP COLUMN country; ALTER TABLE radio_stations DROP COLUMN country_code; " +
        "ALTER TABLE radio_stations DROP COLUMN logo_url; ALTER TABLE radio_stations DROP COLUMN catalog_id;";

    [Test]
    public async Task InitializeAsync_SchemaSeventeen_AddsCatalogColumnsKeepsStationsAndReopensByteForByte()
    {
        await CreatePreBaselineAsync(5);
        DateTimeOffset played = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        RadioStation keep = new(Guid.NewGuid(), "Keep radio", new Uri("https://example.invalid/live"), "Jazz", "Me", "Mine", 4);
        await using (SqliteTrackRepository repository = new(databasePath))
        {
            await repository.AddStationAsync(keep);
            await repository.RecordStationPlayedAsync(keep.Id, played);
        }
        await ScalarAsync(DropCatalogColumns + " PRAGMA user_version=17;");
        await using (SqliteTrackRepository upgraded = new(databasePath))
        {
            Assert.That((await upgraded.ListAsync()).Single().Id, Is.EqualTo(trackId));
            Assert.That(await upgraded.GetTracksAsync(playlistId, ""), Has.Count.EqualTo(2));
            RadioStation actual = (await upgraded.ListStationsAsync()).Single();
            Assert.Multiple(() =>
            {
                Assert.That(actual.Id, Is.EqualTo(keep.Id));
                Assert.That(actual.Rating, Is.EqualTo(4));
                Assert.That(actual.PlayCount, Is.EqualTo(1));
                Assert.That(actual.LastPlayed, Is.EqualTo(played));
                Assert.That(actual.Country, Is.Null);
                Assert.That(actual.CountryCode, Is.Null);
                Assert.That(actual.LogoUri, Is.Null);
                Assert.That(actual.CatalogId, Is.Null);
            });
            Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
            Assert.That(await ScalarAsync("PRAGMA foreign_key_check;"), Is.Null);
        }
        byte[] hash = await DatabaseFileSnapshot.HashAsync(databasePath);
        await using (SqliteTrackRepository reopened = new(databasePath)) await reopened.ListStationsAsync();
        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(hash));
    }

    [Test]
    public async Task InitializeAsync_CatalogMigrationFailure_RollsBackBytesAndRetriesAfterRepair()
    {
        await CreatePreBaselineAsync(5);
        await using (SqliteTrackRepository current = new(databasePath)) await current.AddStationAsync(new(Guid.NewGuid(), "Keep", new Uri("https://example.invalid/live")));
        // An unexpected column left behind by a partial repair must never be silently accepted.
        await ScalarAsync("DROP INDEX ix_radio_stations_catalog_id; ALTER TABLE radio_stations DROP COLUMN country_code; ALTER TABLE radio_stations DROP COLUMN logo_url; ALTER TABLE radio_stations DROP COLUMN catalog_id; PRAGMA user_version=17;");
        byte[] hash = await DatabaseFileSnapshot.HashAsync(databasePath);
        await using SqliteTrackRepository upgraded = new(databasePath);
        Assert.That(async () => await upgraded.ListStationsAsync(), Throws.TypeOf<SqliteException>());
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(17L));
        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(hash));
        await ScalarAsync("ALTER TABLE radio_stations DROP COLUMN country;");
        Assert.That((await upgraded.ListStationsAsync()).Single().Name, Is.EqualTo("Keep"));
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
        Assert.That((await upgraded.ListAsync()).Single().Id, Is.EqualTo(trackId));
    }
}
