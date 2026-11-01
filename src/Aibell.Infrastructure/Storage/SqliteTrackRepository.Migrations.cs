using Microsoft.Data.Sqlite;

namespace Aibell.Infrastructure.Storage;

public sealed partial class SqliteTrackRepository
{
    // Versions 1–5 shipped with additive, shape-based initialization. Keep that
    // compatibility path as one baseline migration; all later changes have a
    // dedicated step and run only when their version has not been applied.
    private static async Task MigrateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long schemaVersion,
        CancellationToken cancellationToken)
    {
        if (schemaVersion < 6)
        {
            await MigrateToBaselineSixAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 6, cancellationToken)
                .ConfigureAwait(false);
        }

        if (schemaVersion < 7)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE smart_playlist_rules (
                    playlist_id TEXT PRIMARY KEY NOT NULL,
                    definition_json TEXT NOT NULL,
                    FOREIGN KEY (playlist_id) REFERENCES playlists(id) ON DELETE CASCADE
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 7, cancellationToken)
                .ConfigureAwait(false);
        }

        if (schemaVersion < 8)
            await SetSchemaVersionAsync(connection, transaction, 8, cancellationToken).ConfigureAwait(false);
        if (schemaVersion < 9)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "ALTER TABLE tracks ADD COLUMN metadata_json TEXT;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 9, cancellationToken).ConfigureAwait(false);
        }

        if (schemaVersion < 10)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE radio_stations (
                    id TEXT PRIMARY KEY NOT NULL,
                    name TEXT NOT NULL CHECK (length(trim(name)) BETWEEN 1 AND 200),
                    stream TEXT NOT NULL UNIQUE,
                    genre TEXT NOT NULL DEFAULT '', creator TEXT NOT NULL DEFAULT '',
                    description TEXT NOT NULL DEFAULT '', rating INTEGER NOT NULL DEFAULT 0 CHECK (rating BETWEEN 0 AND 5),
                    play_count INTEGER NOT NULL DEFAULT 0 CHECK (play_count >= 0), last_played_utc TEXT
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 10, cancellationToken).ConfigureAwait(false);
        }

        if (schemaVersion < 11)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = PodcastSchemaV11Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 11, cancellationToken).ConfigureAwait(false);
        }
        if (schemaVersion < 12)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = AudiobookSchemaV12Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 12, cancellationToken).ConfigureAwait(false);
        }
        if (schemaVersion < 13)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = PodcastPositionsSchemaV13Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 13, cancellationToken).ConfigureAwait(false);
        }

        if (schemaVersion < 14)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            // Refresh SQLite's cached schema before preparing ALTER after an externally repaired failed migration.
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('podcast_episodes');";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "ALTER TABLE podcast_episodes ADD COLUMN rating INTEGER NOT NULL DEFAULT 0 CHECK (rating BETWEEN 0 AND 5);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 14, cancellationToken).ConfigureAwait(false);
        }

        if (schemaVersion < 15)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('podcast_episodes');";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "ALTER TABLE podcast_episodes ADD COLUMN image_url TEXT;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 15, cancellationToken).ConfigureAwait(false);
        }

        if (schemaVersion < 16)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            // Episodes cached before covers were read have no image address. Forget the feed validators so the next
            // refresh downloads the whole feed instead of answering 304 and leaving every cover missing.
            command.CommandText = "UPDATE podcast_subscriptions SET entity_tag = NULL, last_modified_utc = NULL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 16, cancellationToken).ConfigureAwait(false);
        }

        if (schemaVersion < 17)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('podcast_episodes');";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            // Existing episodes are not new: only rows inserted by a later refresh are flagged.
            command.CommandText = "ALTER TABLE podcast_episodes ADD COLUMN is_new INTEGER NOT NULL DEFAULT 0 CHECK (is_new IN (0, 1));";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 17, cancellationToken).ConfigureAwait(false);
        }

        if (schemaVersion < 18)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('radio_stations');";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            // Stations copied from the curated catalog remember where they came from. Existing rows keep NULL in every new column.
            command.CommandText = """
                ALTER TABLE radio_stations ADD COLUMN country TEXT;
                ALTER TABLE radio_stations ADD COLUMN country_code TEXT;
                ALTER TABLE radio_stations ADD COLUMN logo_url TEXT;
                ALTER TABLE radio_stations ADD COLUMN catalog_id TEXT;
                CREATE UNIQUE INDEX ix_radio_stations_catalog_id ON radio_stations (catalog_id) WHERE catalog_id IS NOT NULL;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(connection, transaction, 18, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SetSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateToBaselineSixAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tracks (
                id TEXT PRIMARY KEY NOT NULL,
                location TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                artist TEXT NOT NULL,
                album TEXT NOT NULL,
                genre TEXT NOT NULL DEFAULT '',
                album_artist TEXT NOT NULL DEFAULT '',
                track_number INTEGER NOT NULL DEFAULT 0,
                year INTEGER NOT NULL DEFAULT 0,
                rating INTEGER NOT NULL DEFAULT 0 CHECK (rating BETWEEN 0 AND 5),
                duration_ticks INTEGER NOT NULL CHECK (duration_ticks >= 0),
                bit_rate INTEGER NOT NULL DEFAULT 0,
                bpm REAL NOT NULL DEFAULT 0,
                channels INTEGER NOT NULL DEFAULT 0,
                comment TEXT NOT NULL DEFAULT '',
                composer TEXT NOT NULL DEFAULT '',
                date_added_utc TEXT NOT NULL DEFAULT '',
                date_modified_utc TEXT,
                description TEXT NOT NULL DEFAULT '',
                disc_number INTEGER NOT NULL DEFAULT 0,
                episode_number INTEGER NOT NULL DEFAULT 0,
                keywords TEXT NOT NULL DEFAULT '',
                last_played_utc TEXT,
                last_skipped_utc TEXT,
                play_count INTEGER NOT NULL DEFAULT 0,
                sample_rate INTEGER NOT NULL DEFAULT 0,
                season_number INTEGER NOT NULL DEFAULT 0,
                show_name TEXT NOT NULL DEFAULT '',
                file_size INTEGER NOT NULL DEFAULT 0,
                skip_count INTEGER NOT NULL DEFAULT 0,
                source TEXT NOT NULL DEFAULT '',
                updated_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tracks_artist_album
                ON tracks (artist COLLATE NOCASE, album COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS library_sources (
                path TEXT PRIMARY KEY NOT NULL
            );
            CREATE TABLE IF NOT EXISTS track_sources (
                track_id TEXT NOT NULL,
                source_path TEXT NOT NULL,
                PRIMARY KEY (track_id, source_path),
                FOREIGN KEY (track_id) REFERENCES tracks (id) ON DELETE CASCADE,
                FOREIGN KEY (source_path) REFERENCES library_sources (path) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_track_sources_source
                ON track_sources (source_path);
            CREATE TABLE IF NOT EXISTS playlists (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS playlist_entries (
                playlist_id TEXT NOT NULL,
                track_id TEXT NOT NULL,
                position INTEGER NOT NULL,
                PRIMARY KEY (playlist_id, position),
                FOREIGN KEY (playlist_id) REFERENCES playlists (id) ON DELETE CASCADE,
                FOREIGN KEY (track_id) REFERENCES tracks (id) ON DELETE CASCADE
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        (string Name, string Definition)[] addedColumns =
        [
            ("genre", "TEXT NOT NULL DEFAULT ''"),
            ("album_artist", "TEXT NOT NULL DEFAULT ''"),
            ("track_number", "INTEGER NOT NULL DEFAULT 0"),
            ("year", "INTEGER NOT NULL DEFAULT 0"),
            ("rating", "INTEGER NOT NULL DEFAULT 0 CHECK (rating BETWEEN 0 AND 5)"),
            ("bit_rate", "INTEGER NOT NULL DEFAULT 0"),
            ("bpm", "REAL NOT NULL DEFAULT 0"),
            ("channels", "INTEGER NOT NULL DEFAULT 0"),
            ("comment", "TEXT NOT NULL DEFAULT ''"),
            ("composer", "TEXT NOT NULL DEFAULT ''"),
            ("date_added_utc", "TEXT NOT NULL DEFAULT ''"),
            ("date_modified_utc", "TEXT"),
            ("description", "TEXT NOT NULL DEFAULT ''"),
            ("disc_number", "INTEGER NOT NULL DEFAULT 0"),
            ("episode_number", "INTEGER NOT NULL DEFAULT 0"),
            ("keywords", "TEXT NOT NULL DEFAULT ''"),
            ("last_played_utc", "TEXT"),
            ("last_skipped_utc", "TEXT"),
            ("play_count", "INTEGER NOT NULL DEFAULT 0"),
            ("sample_rate", "INTEGER NOT NULL DEFAULT 0"),
            ("season_number", "INTEGER NOT NULL DEFAULT 0"),
            ("show_name", "TEXT NOT NULL DEFAULT ''"),
            ("file_size", "INTEGER NOT NULL DEFAULT 0"),
            ("skip_count", "INTEGER NOT NULL DEFAULT 0"),
            ("source", "TEXT NOT NULL DEFAULT ''")
        ];
        await using SqliteCommand migration = connection.CreateCommand();
        migration.Transaction = transaction;
        foreach ((string name, string definition) in addedColumns)
        {
            migration.CommandText =
                $"SELECT COUNT(*) FROM pragma_table_info('tracks') WHERE name = '{name}';";
            long columnCount = (long)(await migration
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) ?? 0L);
            if (columnCount == 0)
            {
                migration.CommandText = $"ALTER TABLE tracks ADD COLUMN {name} {definition};";
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        migration.CommandText = """
            UPDATE tracks
            SET date_added_utc = updated_at_utc
            WHERE date_added_utc = '';
            UPDATE tracks
            SET source = COALESCE(
                (SELECT MIN(source_path) FROM track_sources WHERE track_id = tracks.id),
                '')
            WHERE source = '';
            CREATE INDEX IF NOT EXISTS ix_tracks_genre_artist_album
                ON tracks (genre COLLATE NOCASE, artist COLLATE NOCASE, album COLLATE NOCASE);
            """;
        await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
