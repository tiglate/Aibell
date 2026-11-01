using System.Security.Cryptography;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Storage;

[TestFixture]
public sealed partial class SchemaMigrationTests
{
    private string directory = null!;
    private string databasePath = null!;
    private readonly Guid trackId = Guid.NewGuid();
    private readonly Guid playlistId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "library.db");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task InitializeAsync_PreBaselineLibrary_PreservesTracksSourcesAndPlaylistOrder(int version)
    {
        await CreatePreBaselineAsync(version);
        await using SqliteTrackRepository repository = new(databasePath);
        var tracks = await repository.ListAsync();
        var playlistTracks = await repository.GetTracksAsync(playlistId, string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(tracks.Single().Id, Is.EqualTo(trackId));
            Assert.That(tracks.Single().Source, Is.EqualTo("/music"));
            Assert.That(tracks.Single().DateAdded, Is.EqualTo(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero)));
            Assert.That(playlistTracks.Select(track => track.Id), Is.EqualTo(new[] { trackId, trackId }));
        });
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
        Assert.That(await ScalarAsync("PRAGMA foreign_key_check;"), Is.Null);
    }

    [Test]
    public async Task InitializeAsync_SchemaSeven_PreservesExistingLibrary()
    {
        await CreatePreBaselineAsync(5);
        await using (SqliteTrackRepository current = new(databasePath))
        {
            await current.ListAsync();
        }
        await ScalarAsync("DROP TABLE podcast_playback_positions; DROP TRIGGER clear_podcast_position_after_download_change; DROP TABLE audiobook_shelf; DROP TABLE podcast_episodes; DROP TABLE podcast_subscriptions; DROP TABLE radio_stations; ALTER TABLE tracks DROP COLUMN metadata_json; PRAGMA user_version=7;");
        await using SqliteTrackRepository upgraded = new(databasePath);
        Assert.That((await upgraded.ListAsync()).Single().Id, Is.EqualTo(trackId));
        Assert.That(await upgraded.GetTracksAsync(playlistId, ""), Has.Count.EqualTo(2));
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
    }

    [Test]
    public async Task InitializeAsync_CurrentLibrary_DoesNotRewriteDatabase()
    {
        await CreatePreBaselineAsync(5);
        await using (SqliteTrackRepository repository = new(databasePath))
        {
            await repository.ListAsync();
        }
        byte[] originalHash = await DatabaseFileSnapshot.HashAsync(databasePath);

        await using (SqliteTrackRepository reopened = new(databasePath))
        {
            await reopened.ListAsync();
            await reopened.ListAsync();
        }

        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(originalHash));
    }

    [Test]
    public async Task InitializeAsync_LaterMigrationFails_RollsBackEarlierStepAndCanRetry()
    {
        await CreatePreBaselineAsync(5);
        await ScalarAsync("CREATE TABLE smart_playlist_rules (unexpected TEXT);");
        byte[] originalHash = await DatabaseFileSnapshot.HashAsync(databasePath);
        await using SqliteTrackRepository repository = new(databasePath);

        Assert.That(async () => await repository.ListAsync(), Throws.TypeOf<SqliteException>());
        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(originalHash));
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(5L));
        Assert.That(await ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('tracks') WHERE name='genre';"), Is.EqualTo(0L));

        // The caller repairs the incompatible table; initialization can be retried
        // on the same repository instead of remembering a failed upgrade as complete.
        await ScalarAsync("DROP TABLE smart_playlist_rules;");
        Assert.That((await repository.ListAsync()).Single().Id, Is.EqualTo(trackId));
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
    }

    [Test]
    public async Task InitializeAsync_PodcastMigrationFails_RollsBackPartialTablesAndRetriesWithoutLosingLibrary()
    {
        await CreatePreBaselineAsync(5);
        await using (SqliteTrackRepository current = new(databasePath)) await current.ListAsync();
        await ScalarAsync(DropCatalogColumns + " DROP TABLE podcast_playback_positions; DROP TRIGGER clear_podcast_position_after_download_change; DROP TABLE audiobook_shelf; DROP TABLE podcast_episodes; DROP TABLE podcast_subscriptions; CREATE TABLE podcast_episodes(unexpected TEXT); PRAGMA user_version=10;");
        byte[] originalHash = await DatabaseFileSnapshot.HashAsync(databasePath);
        await using SqliteTrackRepository upgraded = new(databasePath);
        Assert.That(async () => await upgraded.ListSubscriptionsAsync(), Throws.TypeOf<SqliteException>());
        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(originalHash));
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(10L));
        Assert.That(await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name='podcast_subscriptions';"), Is.EqualTo(0L));
        await ScalarAsync("DROP TABLE podcast_episodes;");
        Assert.That(await upgraded.ListSubscriptionsAsync(), Is.Empty);
        Assert.That((await upgraded.ListAsync()).Single().Id, Is.EqualTo(trackId));
        Assert.That(await upgraded.GetTracksAsync(playlistId, ""), Has.Count.EqualTo(2));
        Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(18L));
        Assert.That(await ScalarAsync("PRAGMA foreign_key_check;"), Is.Null);
    }

    private async Task CreatePreBaselineAsync(int version)
    {
        await using SqliteConnection connection = new($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE tracks (
                id TEXT PRIMARY KEY NOT NULL, location TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL, artist TEXT NOT NULL, album TEXT NOT NULL,
                duration_ticks INTEGER NOT NULL, updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE library_sources (path TEXT PRIMARY KEY NOT NULL);
            CREATE TABLE track_sources (
                track_id TEXT NOT NULL, source_path TEXT NOT NULL,
                PRIMARY KEY(track_id, source_path),
                FOREIGN KEY(track_id) REFERENCES tracks(id) ON DELETE CASCADE,
                FOREIGN KEY(source_path) REFERENCES library_sources(path) ON DELETE CASCADE
            );
            CREATE TABLE playlists (id TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL);
            CREATE TABLE playlist_entries (
                playlist_id TEXT NOT NULL, track_id TEXT NOT NULL, position INTEGER NOT NULL,
                PRIMARY KEY(playlist_id, position),
                FOREIGN KEY(playlist_id) REFERENCES playlists(id) ON DELETE CASCADE,
                FOREIGN KEY(track_id) REFERENCES tracks(id) ON DELETE CASCADE
            );
            INSERT INTO tracks VALUES($track, 'file:///music/song.flac', 'Song', 'Artist', 'Album', 100, '2025-01-02T03:04:05Z');
            INSERT INTO library_sources VALUES('/music');
            INSERT INTO track_sources VALUES($track, '/music');
            INSERT INTO playlists VALUES($playlist, 'Favorites');
            INSERT INTO playlist_entries VALUES($playlist, $track, 0), ($playlist, $track, 1);
            PRAGMA user_version={version};
            """;
        command.Parameters.AddWithValue("$track", trackId.ToString("D"));
        command.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using SqliteConnection connection = new($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
