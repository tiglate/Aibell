using System.Text.Json;
using Aibell.Application.Library;
using Aibell.Application.Playlists;
using Aibell.Domain.Media;
using Aibell.Domain.Playlists;
using Microsoft.Data.Sqlite;

namespace Aibell.Infrastructure.Storage;

public sealed partial class SqliteTrackRepository :
    ITrackRepository,
    ITrackMetadataRepository,
    ILibraryBrowser,
    ILibrarySourceRepository,
    IPlaylistRepository,
    Aibell.Application.Podcasts.IPodcastNewEpisodeRepository,
    IAsyncDisposable
{
    private const int CurrentSchemaVersion = 18;
    private readonly string connectionString;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private bool initialized;
    private bool disposed;

    public SqliteTrackRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task<IReadOnlyList<Track>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        return await QueryTracksAsync(null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Track>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        string normalized = query.Trim();
        return await QueryTracksAsync(
            normalized.Length == 0 ? null : normalized,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Track>> QueryTracksAsync(
        string? query,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = query is null
            ? """
            SELECT id, location, title, artist, album, duration_ticks, genre,
                   album_artist, track_number, year, rating, bit_rate, bpm,
                   channels, comment, composer, date_added_utc, date_modified_utc,
                   description, disc_number, episode_number, keywords,
                   last_played_utc, last_skipped_utc, play_count, sample_rate,
                   season_number, show_name, file_size, skip_count, source, metadata_json
            FROM tracks
            ORDER BY artist COLLATE NOCASE, album COLLATE NOCASE, title COLLATE NOCASE;
            """
            : """
            SELECT id, location, title, artist, album, duration_ticks, genre,
                   album_artist, track_number, year, rating, bit_rate, bpm,
                   channels, comment, composer, date_added_utc, date_modified_utc,
                   description, disc_number, episode_number, keywords,
                   last_played_utc, last_skipped_utc, play_count, sample_rate,
                   season_number, show_name, file_size, skip_count, source, metadata_json
            FROM tracks
            WHERE instr(lower(title), lower($query)) > 0
               OR instr(lower(artist), lower($query)) > 0
               OR instr(lower(album), lower($query)) > 0
               OR instr(lower(genre), lower($query)) > 0
            ORDER BY artist COLLATE NOCASE, album COLLATE NOCASE, title COLLATE NOCASE;
            """;
        if (query is not null)
        {
            command.Parameters.AddWithValue("$query", query);
        }

        List<Track> tracks = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tracks.Add(ReadTrack(reader));
        }

        return tracks;
    }

    private static Track ReadTrack(SqliteDataReader reader)
    {
        return new Track(
            Guid.Parse(reader.GetString(0)),
            new Uri(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            TimeSpan.FromTicks(reader.GetInt64(5)),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetDouble(12),
            reader.GetInt32(13),
            reader.GetString(14),
            reader.GetString(15),
            ReadDateTimeOffset(reader, 16),
            ReadDateTimeOffset(reader, 17),
            reader.GetString(18),
            reader.GetInt32(19),
            reader.GetInt32(20),
            reader.GetString(21),
            ReadDateTimeOffset(reader, 22),
            ReadDateTimeOffset(reader, 23),
            reader.GetInt32(24),
            reader.GetInt32(25),
            reader.GetInt32(26),
            reader.GetString(27),
            reader.GetInt64(28),
            reader.GetInt32(29),
            reader.GetString(30), ReadMetadata(reader));
    }

    private static Aibell.Domain.Media.TrackMetadata? ReadMetadata(SqliteDataReader reader)
    {
        if (reader.IsDBNull(31)) return null;
        try
        {
            Aibell.Domain.Media.TrackMetadata? value = JsonSerializer.Deserialize<Aibell.Domain.Media.TrackMetadata>(reader.GetString(31));
            value?.Validate();
            return value;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException) { return null; }
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        string value = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;
    }

    public async Task<IReadOnlyList<GenreSummary>> ListGenresAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT genre, COUNT(*)
            FROM tracks
            GROUP BY genre COLLATE NOCASE
            ORDER BY genre COLLATE NOCASE;
            """;

        List<GenreSummary> genres = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            genres.Add(new GenreSummary(reader.GetString(0), reader.GetInt32(1)));
        }

        return genres;
    }

    public async Task<IReadOnlyList<ArtistSummary>> ListArtistsAsync(
        string? genre,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT artist, COUNT(*)
            FROM tracks
            WHERE $genre IS NULL OR genre = $genre COLLATE NOCASE
            GROUP BY artist COLLATE NOCASE
            ORDER BY artist COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$genre", genre is null ? DBNull.Value : genre);

        List<ArtistSummary> artists = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            artists.Add(new ArtistSummary(reader.GetString(0), reader.GetInt32(1)));
        }

        return artists;
    }

    public async Task<IReadOnlyList<AlbumSummary>> ListAlbumsAsync(
        string? genre,
        string? artist,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT artist, album, COUNT(*)
            FROM tracks
            WHERE ($genre IS NULL OR genre = $genre COLLATE NOCASE)
              AND ($artist IS NULL OR artist = $artist COLLATE NOCASE)
            GROUP BY artist COLLATE NOCASE, album COLLATE NOCASE
            ORDER BY album COLLATE NOCASE, artist COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$genre", genre is null ? DBNull.Value : genre);
        command.Parameters.AddWithValue("$artist", artist is null ? DBNull.Value : artist);

        List<AlbumSummary> albums = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            albums.Add(new AlbumSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return albums;
    }

    public Task<IReadOnlyList<Track>> GetTracksAsync(
        string? genre,
        string? artist,
        string? album,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return QueryScopedTracksAsync(genre, artist, album, query.Trim(), cancellationToken);
    }

    private async Task<IReadOnlyList<Track>> QueryScopedTracksAsync(
        string? genre,
        string? artist,
        string? album,
        string query,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, location, title, artist, album, duration_ticks, genre,
                   album_artist, track_number, year, rating, bit_rate, bpm,
                   channels, comment, composer, date_added_utc, date_modified_utc,
                   description, disc_number, episode_number, keywords,
                   last_played_utc, last_skipped_utc, play_count, sample_rate,
                   season_number, show_name, file_size, skip_count, source, metadata_json
            FROM tracks
            WHERE ($genre IS NULL OR genre = $genre COLLATE NOCASE)
              AND ($artist IS NULL OR artist = $artist COLLATE NOCASE)
              AND ($album IS NULL OR album = $album COLLATE NOCASE)
              AND ($query = ''
                   OR instr(lower(title), lower($query)) > 0
                   OR instr(lower(artist), lower($query)) > 0
                   OR instr(lower(album), lower($query)) > 0
                   OR instr(lower(genre), lower($query)) > 0)
            ORDER BY album COLLATE NOCASE, title COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$genre", genre is null ? DBNull.Value : genre);
        command.Parameters.AddWithValue("$artist", artist is null ? DBNull.Value : artist);
        command.Parameters.AddWithValue("$album", album is null ? DBNull.Value : album);
        command.Parameters.AddWithValue("$query", query);

        List<Track> tracks = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tracks.Add(ReadTrack(reader));
        }

        return tracks;
    }

    async Task<IReadOnlyList<Playlist>> IPlaylistRepository.ListAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT playlists.id, playlists.name, playlist_entries.track_id, smart_playlist_rules.definition_json
            FROM playlists
            LEFT JOIN playlist_entries
                ON playlist_entries.playlist_id = playlists.id
            LEFT JOIN smart_playlist_rules ON smart_playlist_rules.playlist_id = playlists.id
            ORDER BY playlists.name COLLATE NOCASE, playlist_entries.position;
            """;

        Dictionary<Guid, (string Name, List<Guid> TrackIds, SmartPlaylistDefinition? Definition)> playlists = [];
        await using (SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Guid id = Guid.Parse(reader.GetString(0));
                if (!playlists.TryGetValue(id, out (string Name, List<Guid> TrackIds, SmartPlaylistDefinition? Definition) value))
                {
                    value = (reader.GetString(1), [], reader.IsDBNull(3) ? null : ReadSmartDefinition(reader.GetString(3)));
                    playlists.Add(id, value);
                }

                if (!reader.IsDBNull(2))
                {
                    value.TrackIds.Add(Guid.Parse(reader.GetString(2)));
                }
            }
        }

        List<Playlist> result = [];
        foreach (var pair in playlists)
        {
            IReadOnlyList<Guid> trackIds = pair.Value.Definition is { } definition
                ? await QuerySmartTrackIdsAsync(connection, definition, cancellationToken).ConfigureAwait(false)
                : pair.Value.TrackIds;
            result.Add(new Playlist(pair.Key, pair.Value.Name, trackIds, pair.Value.Definition));
        }
        return result;
    }

    public async Task<IReadOnlyList<Track>> GetTracksAsync(
        Guid playlistId,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        SmartPlaylistDefinition? definition = await GetSmartDefinitionAsync(connection, playlistId, cancellationToken)
            .ConfigureAwait(false);
        if (definition is not null)
        {
            return await QuerySmartTracksAsync(connection, definition, query, cancellationToken).ConfigureAwait(false);
        }
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT tracks.id, tracks.location, tracks.title, tracks.artist,
                   tracks.album, tracks.duration_ticks, tracks.genre,
                   tracks.album_artist, tracks.track_number, tracks.year, tracks.rating,
                   tracks.bit_rate, tracks.bpm, tracks.channels, tracks.comment,
                   tracks.composer, tracks.date_added_utc, tracks.date_modified_utc,
                   tracks.description, tracks.disc_number, tracks.episode_number,
                   tracks.keywords, tracks.last_played_utc, tracks.last_skipped_utc,
                   tracks.play_count, tracks.sample_rate, tracks.season_number,
                   tracks.show_name, tracks.file_size, tracks.skip_count, tracks.source, tracks.metadata_json
            FROM playlist_entries
            JOIN tracks ON tracks.id = playlist_entries.track_id
            WHERE playlist_entries.playlist_id = $playlist
              AND ($query = ''
                   OR instr(lower(tracks.title), lower($query)) > 0
                   OR instr(lower(tracks.artist), lower($query)) > 0
                   OR instr(lower(tracks.album), lower($query)) > 0
                   OR instr(lower(tracks.genre), lower($query)) > 0)
            ORDER BY playlist_entries.position;
            """;
        command.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
        command.Parameters.AddWithValue("$query", query.Trim());

        List<Track> tracks = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tracks.Add(ReadTrack(reader));
        }

        return tracks;
    }

    public Task<Playlist> CreateAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        return CreatePlaylistAsync(name, null, cancellationToken);
    }

    public Task<Playlist> CreateWithTrackAsync(
        string name,
        Guid trackId,
        CancellationToken cancellationToken = default)
    {
        return CreatePlaylistAsync(name, trackId, cancellationToken);
    }

    private async Task<Playlist> CreatePlaylistAsync(
        string name,
        Guid? trackId,
        CancellationToken cancellationToken,
        SmartPlaylistDefinition? definition = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalizedName = name.Trim();
        Guid id = Guid.NewGuid();

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO playlists (id, name)
            SELECT $id, $name
            WHERE NOT EXISTS (
                SELECT 1 FROM playlists WHERE name = $name COLLATE NOCASE
            );
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$name", normalizedName);
        int inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted == 0)
        {
            throw new InvalidOperationException($"A playlist named '{normalizedName}' already exists.");
        }

        if (trackId is Guid initialTrackId)
        {
            command.CommandText = """
                INSERT INTO playlist_entries (playlist_id, track_id, position)
                VALUES ($id, $track, 0);
                """;
            command.Parameters.AddWithValue("$track", initialTrackId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (definition is not null)
        {
            command.CommandText = "INSERT INTO smart_playlist_rules(playlist_id, definition_json) VALUES($id, $definition);";
            command.Parameters.AddWithValue("$definition", System.Text.Json.JsonSerializer.Serialize(definition));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new Playlist(id, normalizedName, trackId is Guid addedTrackId ? [addedTrackId] : [], definition);
    }

    public async Task RenameAsync(
        Guid playlistId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalizedName = name.Trim();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE playlists
            SET name = $name
            WHERE id = $id
              AND NOT EXISTS (
                  SELECT 1 FROM playlists
                  WHERE name = $name COLLATE NOCASE AND id <> $id
              );
            """;
        command.Parameters.AddWithValue("$id", playlistId.ToString("D"));
        command.Parameters.AddWithValue("$name", normalizedName);
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed == 0)
        {
            throw new InvalidOperationException(
                $"Could not rename the playlist. The name '{normalizedName}' may already be in use.");
        }
    }

    public async Task DeleteAsync(
        Guid playlistId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM playlists WHERE id = $id;";
        command.Parameters.AddWithValue("$id", playlistId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddTrackAsync(
        Guid playlistId,
        Guid trackId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureManualPlaylistAsync(connection, playlistId, cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO playlist_entries (playlist_id, track_id, position)
            SELECT $playlist, $track,
                   COALESCE((
                       SELECT MAX(position) + 1
                       FROM playlist_entries
                       WHERE playlist_id = $playlist
                   ), 0)
            WHERE NOT EXISTS (
                  SELECT 1 FROM playlist_entries
                  WHERE playlist_id = $playlist AND track_id = $track
            );
            """;
        command.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
        command.Parameters.AddWithValue("$track", trackId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveTrackAsync(
        Guid playlistId,
        Guid trackId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureManualPlaylistAsync(connection, playlistId, cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM playlist_entries
            WHERE playlist_id = $playlist AND track_id = $track;
            """;
        command.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
        command.Parameters.AddWithValue("$track", trackId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveTrackAsync(
        Guid playlistId,
        Guid trackId,
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (offset is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be -1 or 1.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureManualPlaylistAsync(connection, playlistId, cancellationToken, transaction).ConfigureAwait(false);

        int? currentPosition = await FindTrackPositionAsync(
            connection,
            transaction,
            playlistId,
            trackId,
            cancellationToken).ConfigureAwait(false);
        if (currentPosition is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        int? targetPosition = await FindAdjacentPositionAsync(
            connection,
            transaction,
            playlistId,
            currentPosition.Value,
            offset,
            cancellationToken).ConfigureAwait(false);
        if (targetPosition is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using SqliteCommand swap = connection.CreateCommand();
        swap.Transaction = transaction;
        swap.CommandText = """
            UPDATE playlist_entries SET position = -1
            WHERE playlist_id = $playlist AND position = $current;
            UPDATE playlist_entries SET position = $current
            WHERE playlist_id = $playlist AND position = $target;
            UPDATE playlist_entries SET position = $target
            WHERE playlist_id = $playlist AND position = -1;
            """;
        swap.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
        swap.Parameters.AddWithValue("$current", currentPosition.Value);
        swap.Parameters.AddWithValue("$target", targetPosition.Value);
        await swap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReorderTracksAsync(
        Guid playlistId,
        IReadOnlyList<Guid> trackIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        if (trackIds.Count != trackIds.Distinct().Count())
        {
            throw new ArgumentException("Playlist order cannot contain duplicate tracks.", nameof(trackIds));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureManualPlaylistAsync(connection, playlistId, cancellationToken, transaction).ConfigureAwait(false);

        HashSet<Guid> storedTrackIds = [];
        await using (SqliteCommand query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT track_id FROM playlist_entries
                WHERE playlist_id = $playlist
                ORDER BY position;
                """;
            query.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
            await using SqliteDataReader reader = await query
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                storedTrackIds.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        if (storedTrackIds.Count != trackIds.Count || !storedTrackIds.SetEquals(trackIds))
        {
            throw new InvalidOperationException(
                "The requested order must contain every track in the playlist exactly once.");
        }

        await using (SqliteCommand releasePositions = connection.CreateCommand())
        {
            releasePositions.Transaction = transaction;
            releasePositions.CommandText = """
                UPDATE playlist_entries
                SET position = -position - 1
                WHERE playlist_id = $playlist;
                """;
            releasePositions.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
            await releasePositions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (int position = 0; position < trackIds.Count; position++)
        {
            await using SqliteCommand update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE playlist_entries SET position = $position
                WHERE playlist_id = $playlist AND track_id = $track;
                """;
            update.Parameters.AddWithValue("$position", position);
            update.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
            update.Parameters.AddWithValue("$track", trackIds[position].ToString("D"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string sourceRoot = Path.GetFullPath(path);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO library_sources (path) VALUES ($path);";
        command.Parameters.AddWithValue("$path", sourceRoot);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task<IReadOnlyList<string>> ILibrarySourceRepository.ListAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM library_sources ORDER BY path COLLATE NOCASE;";

        List<string> sources = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sources.Add(reader.GetString(0));
        }

        return sources;
    }

    public async Task<int> RemoveAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string sourceRoot = Path.GetFullPath(path);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (SqliteCommand removeSource = connection.CreateCommand())
        {
            removeSource.Transaction = transaction;
            removeSource.CommandText = "DELETE FROM library_sources WHERE path = $path;";
            removeSource.Parameters.AddWithValue("$path", sourceRoot);
            await removeSource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int removedTracks;
        await using (SqliteCommand removeOrphans = connection.CreateCommand())
        {
            removeOrphans.Transaction = transaction;
            removeOrphans.CommandText = """
                DELETE FROM tracks
                WHERE NOT EXISTS (
                    SELECT 1 FROM track_sources
                    WHERE track_sources.track_id = tracks.id
                );
                """;
            removedTracks = await removeOrphans
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removedTracks;
    }

    public async Task UpsertAsync(
        Track track,
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        sourceRoot = Path.GetFullPath(sourceRoot);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO library_sources (path) VALUES ($source);
            INSERT INTO tracks (
                id, location, title, artist, album, genre, album_artist,
                track_number, year, rating, duration_ticks, updated_at_utc,
                bit_rate, bpm, channels, comment, composer, date_added_utc,
                date_modified_utc, description, disc_number, episode_number,
                keywords, sample_rate, season_number, show_name, file_size, source)
            VALUES (
                $id, $location, $title, $artist, $album, $genre, $albumArtist,
                $trackNumber, $year, $rating, $duration, $updated,
                $bitRate, $bpm, $channels, $comment, $composer, $dateAdded,
                $dateModified, $description, $discNumber, $episodeNumber,
                $keywords, $sampleRate, $seasonNumber, $showName, $fileSize, $trackSource)
            ON CONFLICT(id) DO UPDATE SET
                location = excluded.location,
                title = CASE WHEN tracks.metadata_json IS NULL THEN excluded.title ELSE tracks.title END,
                artist = CASE WHEN tracks.metadata_json IS NULL THEN excluded.artist ELSE tracks.artist END,
                album = CASE WHEN tracks.metadata_json IS NULL THEN excluded.album ELSE tracks.album END,
                genre = CASE WHEN tracks.metadata_json IS NULL THEN excluded.genre ELSE tracks.genre END,
                album_artist = CASE WHEN tracks.metadata_json IS NULL THEN excluded.album_artist ELSE tracks.album_artist END,
                track_number = CASE WHEN tracks.metadata_json IS NULL THEN excluded.track_number ELSE tracks.track_number END,
                year = CASE WHEN tracks.metadata_json IS NULL THEN excluded.year ELSE tracks.year END,
                duration_ticks = excluded.duration_ticks,
                bit_rate = excluded.bit_rate,
                bpm = CASE WHEN tracks.metadata_json IS NULL THEN excluded.bpm ELSE tracks.bpm END,
                channels = excluded.channels,
                comment = CASE WHEN tracks.metadata_json IS NULL THEN excluded.comment ELSE tracks.comment END,
                composer = CASE WHEN tracks.metadata_json IS NULL THEN excluded.composer ELSE tracks.composer END,
                date_modified_utc = excluded.date_modified_utc,
                description = CASE WHEN tracks.metadata_json IS NULL THEN excluded.description ELSE tracks.description END,
                disc_number = CASE WHEN tracks.metadata_json IS NULL THEN excluded.disc_number ELSE tracks.disc_number END,
                episode_number = CASE WHEN tracks.metadata_json IS NULL THEN excluded.episode_number ELSE tracks.episode_number END,
                keywords = CASE WHEN tracks.metadata_json IS NULL THEN excluded.keywords ELSE tracks.keywords END,
                sample_rate = excluded.sample_rate,
                season_number = CASE WHEN tracks.metadata_json IS NULL THEN excluded.season_number ELSE tracks.season_number END,
                show_name = CASE WHEN tracks.metadata_json IS NULL THEN excluded.show_name ELSE tracks.show_name END,
                file_size = excluded.file_size,
                source = excluded.source,
                updated_at_utc = excluded.updated_at_utc;
            INSERT OR IGNORE INTO track_sources (track_id, source_path)
            VALUES ($id, $source);
            """;
        command.Parameters.AddWithValue("$source", sourceRoot);
        command.Parameters.AddWithValue("$id", track.Id.ToString("D"));
        command.Parameters.AddWithValue("$location", track.Location.AbsoluteUri);
        command.Parameters.AddWithValue("$title", track.Title);
        command.Parameters.AddWithValue("$artist", track.Artist);
        command.Parameters.AddWithValue("$album", track.Album);
        command.Parameters.AddWithValue("$genre", track.Genre);
        command.Parameters.AddWithValue("$albumArtist", track.AlbumArtist);
        command.Parameters.AddWithValue("$trackNumber", track.TrackNumber);
        command.Parameters.AddWithValue("$year", track.Year);
        command.Parameters.AddWithValue("$rating", track.Rating);
        command.Parameters.AddWithValue("$duration", track.Duration.Ticks);
        string now = DateTimeOffset.UtcNow.ToString("O");
        command.Parameters.AddWithValue("$updated", now);
        command.Parameters.AddWithValue("$bitRate", track.BitRate);
        command.Parameters.AddWithValue("$bpm", track.BeatsPerMinute);
        command.Parameters.AddWithValue("$channels", track.Channels);
        command.Parameters.AddWithValue("$comment", track.Comment);
        command.Parameters.AddWithValue("$composer", track.Composer);
        command.Parameters.AddWithValue("$dateAdded", track.DateAdded?.ToString("O") ?? now);
        command.Parameters.AddWithValue("$dateModified", track.DateModified?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$description", track.Description);
        command.Parameters.AddWithValue("$discNumber", track.DiscNumber);
        command.Parameters.AddWithValue("$episodeNumber", track.EpisodeNumber);
        command.Parameters.AddWithValue("$keywords", track.Keywords);
        command.Parameters.AddWithValue("$sampleRate", track.SampleRate);
        command.Parameters.AddWithValue("$seasonNumber", track.SeasonNumber);
        command.Parameters.AddWithValue("$showName", track.ShowName);
        command.Parameters.AddWithValue("$fileSize", track.FileSize);
        command.Parameters.AddWithValue("$trackSource", sourceRoot);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetRatingAsync(
        Guid trackId,
        int rating,
        CancellationToken cancellationToken = default)
    {
        if (rating is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be from zero to five.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE tracks SET rating = $rating WHERE id = $id;";
        command.Parameters.AddWithValue("$rating", rating);
        command.Parameters.AddWithValue("$id", trackId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task RecordPlaybackStartedAsync(
        Guid trackId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        return UpdatePlaybackStatisticAsync(
            trackId,
            "play_count",
            "last_played_utc",
            occurredAt,
            cancellationToken);
    }

    public Task RecordSkippedAsync(
        Guid trackId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        return UpdatePlaybackStatisticAsync(
            trackId,
            "skip_count",
            "last_skipped_utc",
            occurredAt,
            cancellationToken);
    }

    private async Task UpdatePlaybackStatisticAsync(
        Guid trackId,
        string countColumn,
        string timestampColumn,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE tracks
            SET {countColumn} = {countColumn} + 1,
                {timestampColumn} = $occurred
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$occurred", occurredAt.ToString("O"));
        command.Parameters.AddWithValue("$id", trackId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteMissingAsync(
        string sourceRoot,
        IReadOnlyCollection<Uri> existingLocations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(existingLocations);
        sourceRoot = Path.GetFullPath(sourceRoot);
        HashSet<string> existing = existingLocations
            .Select(location => location.AbsoluteUri)
            .ToHashSet(StringComparer.Ordinal);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        List<(string Id, bool Associated)> missing = [];
        await using (SqliteCommand query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT tracks.id, tracks.location,
                       CASE WHEN selected_source.track_id IS NULL THEN 0 ELSE 1 END
                FROM tracks
                LEFT JOIN track_sources AS selected_source
                    ON selected_source.track_id = tracks.id
                   AND selected_source.source_path = $source
                WHERE selected_source.track_id IS NOT NULL
                   OR NOT EXISTS (
                       SELECT 1 FROM track_sources AS any_source
                       WHERE any_source.track_id = tracks.id
                   );
                """;
            query.Parameters.AddWithValue("$source", sourceRoot);
            await using SqliteDataReader reader = await query
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string locationText = reader.GetString(1);
                bool associated = reader.GetInt64(2) != 0;
                if (!existing.Contains(locationText) &&
                    (associated || IsFileUnderRoot(new Uri(locationText), sourceRoot)))
                {
                    missing.Add((reader.GetString(0), associated));
                }
            }
        }

        int removed = 0;
        foreach ((string id, bool associated) in missing)
        {
            if (associated)
            {
                await using SqliteCommand detach = connection.CreateCommand();
                detach.Transaction = transaction;
                detach.CommandText = """
                    DELETE FROM track_sources
                    WHERE track_id = $id AND source_path = $source;
                    """;
                detach.Parameters.AddWithValue("$id", id);
                detach.Parameters.AddWithValue("$source", sourceRoot);
                await detach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM tracks
                WHERE id = $id
                  AND NOT EXISTS (
                      SELECT 1 FROM track_sources WHERE track_id = $id
                  );
                """;
            delete.Parameters.AddWithValue("$id", id);
            removed += await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        using SqliteConnection connection = new(connectionString);
        SqliteConnection.ClearPool(connection);
        initializationLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<int?> FindTrackPositionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid playlistId,
        Guid trackId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT position FROM playlist_entries
            WHERE playlist_id = $playlist AND track_id = $track
            ORDER BY position LIMIT 1;
            """;
        command.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
        command.Parameters.AddWithValue("$track", trackId.ToString("D"));
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long position ? checked((int)position) : null;
    }

    private static async Task<int?> FindAdjacentPositionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid playlistId,
        int currentPosition,
        int offset,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = offset < 0
            ? """
                SELECT position FROM playlist_entries
                WHERE playlist_id = $playlist AND position < $position
                ORDER BY position DESC LIMIT 1;
                """
            : """
                SELECT position FROM playlist_entries
                WHERE playlist_id = $playlist AND position > $position
                ORDER BY position LIMIT 1;
                """;
        command.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
        command.Parameters.AddWithValue("$position", currentPosition);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long position ? checked((int)position) : null;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (initialized)
        {
            return;
        }

        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            await using SqliteConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version;";
            long schemaVersion = (long)(await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) ?? 0L);
            if (schemaVersion < 0 || schemaVersion > CurrentSchemaVersion)
            {
                throw new NotSupportedException(
                    $"Modern library schema {schemaVersion} is not supported by this application " +
                    $"(maximum {CurrentSchemaVersion}). The database was not changed.");
            }
            await MigrateSchemaAsync(connection, transaction, schemaVersion, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private static bool IsFileUnderRoot(Uri location, string sourceRoot)
    {
        if (!location.IsFile)
        {
            return false;
        }

        string relative = Path.GetRelativePath(sourceRoot, location.LocalPath);
        return !Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
