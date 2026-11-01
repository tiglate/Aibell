using Aibell.Application.Playlists;
using Aibell.Domain.Media;
using Aibell.Domain.Playlists;
using Microsoft.Data.Sqlite;

namespace Aibell.Infrastructure.Storage;

public sealed partial class SqliteTrackRepository : IPlaylistImportDestination
{
    public async Task<Playlist> ImportPlaylistAsync(string name, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count is < 1 or > 10000) throw new ArgumentException("Choose between 1 and 10,000 local tracks.", nameof(tracks));
        Track[] snapshot = tracks.ToArray();
        if (snapshot.Any(track => track is null || track.Id == Guid.Empty || !track.Location.IsAbsoluteUri || !track.Location.IsFile || track.Location.IsUnc))
            throw new ArgumentException("Every imported track must have a local file location and identity.", nameof(tracks));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid id = Guid.NewGuid(); string normalized = name.Trim();
        await using (SqliteCommand create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                INSERT INTO playlists(id, name)
                SELECT $id, $name WHERE NOT EXISTS(SELECT 1 FROM playlists WHERE name = $name COLLATE NOCASE);
                """;
            create.Parameters.AddWithValue("$id", id.ToString("D")); create.Parameters.AddWithValue("$name", normalized);
            if (await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                throw new InvalidOperationException($"A playlist named '{normalized}' already exists.");
        }
        List<Guid> identities = [];
        HashSet<Guid> seen = [];
        foreach (Track track in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid persistedId = await InsertPlaylistTrackIfMissingAsync(connection, transaction, track, cancellationToken).ConfigureAwait(false);
            if (!seen.Add(persistedId)) continue;
            await using SqliteCommand entry = connection.CreateCommand();
            entry.Transaction = transaction;
            entry.CommandText = "INSERT INTO playlist_entries(playlist_id, track_id, position) VALUES($playlist, $track, $position);";
            entry.Parameters.AddWithValue("$playlist", id.ToString("D")); entry.Parameters.AddWithValue("$track", persistedId.ToString("D"));
            entry.Parameters.AddWithValue("$position", identities.Count);
            await entry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            identities.Add(persistedId);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(id, normalized, identities);
    }

    private static async Task<Guid> InsertPlaylistTrackIfMissingAsync(SqliteConnection connection, SqliteTransaction transaction,
        Track track, CancellationToken cancellationToken)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        // New loose tracks do not create library_sources/track_sources. Existing
        // catalog rows (including local overrides, ratings and history) are untouched.
        (string Column, object Value)[] fields =
        [
            ("id", track.Id.ToString("D")), ("location", track.Location.AbsoluteUri),
            ("title", track.Title), ("artist", track.Artist), ("album", track.Album), ("genre", track.Genre),
            ("album_artist", track.AlbumArtist), ("track_number", track.TrackNumber), ("year", track.Year),
            ("rating", track.Rating), ("duration_ticks", track.Duration.Ticks), ("bit_rate", track.BitRate),
            ("bpm", track.BeatsPerMinute), ("channels", track.Channels), ("comment", track.Comment), ("composer", track.Composer),
            ("date_added_utc", track.DateAdded?.ToString("O") ?? now), ("date_modified_utc", track.DateModified?.ToString("O") ?? (object)DBNull.Value),
            ("description", track.Description), ("disc_number", track.DiscNumber), ("episode_number", track.EpisodeNumber),
            ("keywords", track.Keywords), ("sample_rate", track.SampleRate), ("season_number", track.SeasonNumber),
            ("show_name", track.ShowName), ("file_size", track.FileSize), ("source", track.Source), ("updated_at_utc", now)
        ];
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT OR IGNORE INTO tracks({string.Join(',', fields.Select(field => field.Column))}) VALUES({string.Join(',', Enumerable.Range(0, fields.Length).Select(index => "$p" + index))});";
        for (int index = 0; index < fields.Length; index++) command.Parameters.AddWithValue("$p" + index, fields[index].Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.Parameters.Clear();
        command.CommandText = "SELECT id FROM tracks WHERE location = $location;";
        command.Parameters.AddWithValue("$location", track.Location.AbsoluteUri);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not string persisted || !Guid.TryParse(persisted, out Guid id))
            throw new InvalidDataException("A playlist track conflicts with an existing catalog identity.");
        return id;
    }
}
