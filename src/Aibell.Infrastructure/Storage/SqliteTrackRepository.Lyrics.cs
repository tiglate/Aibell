using System.Globalization;
using Aibell.Application.Lyrics;
using Aibell.Domain.Lyrics;
using Microsoft.Data.Sqlite;

namespace Aibell.Infrastructure.Storage;

public sealed partial class SqliteTrackRepository : ILyricsStore
{
    // Keyed by the track's identity (a hash of its location), deliberately without a foreign key: lyrics fetched for a file stay
    // available if the library entry is removed and added again. A miss row has no content and remembers when the lookup failed.
    internal const string LyricsSchemaV19Sql = """
        CREATE TABLE IF NOT EXISTS track_lyrics (
            track_id TEXT PRIMARY KEY NOT NULL,
            kind TEXT NOT NULL CHECK (kind IN ('found', 'miss')),
            origin TEXT NOT NULL,
            source TEXT NOT NULL DEFAULT '',
            instrumental INTEGER NOT NULL DEFAULT 0 CHECK (instrumental IN (0, 1)),
            content TEXT NOT NULL DEFAULT '',
            stored_utc TEXT NOT NULL
        );
        """;

    private const string UserOrigins = "'UserSelected','UserWritten'";

    public async Task<StoredLyrics?> GetAsync(Guid trackId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT kind, origin, source, instrumental, content, stored_utc FROM track_lyrics WHERE track_id=$id;";
        command.Parameters.AddWithValue("$id", trackId.ToString("D"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        DateTimeOffset storedAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        if (reader.GetString(0) == "miss") return new StoredLyrics(null, storedAt);
        LyricsOrigin origin = Enum.TryParse(reader.GetString(1), out LyricsOrigin parsed) ? parsed : LyricsOrigin.Provider;
        TrackLyrics? lyrics = reader.GetInt64(3) == 1
            ? TrackLyrics.Instrumental(origin, reader.GetString(2))
            : TrackLyrics.FromText(reader.GetString(4), reader.GetString(4), false, origin, reader.GetString(2));
        // A row that no longer parses is treated as absent so that the next lookup replaces it.
        return lyrics is null ? null : new StoredLyrics(lyrics, storedAt);
    }

    public Task<bool> SaveFetchedAsync(Guid trackId, TrackLyrics lyrics, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        UpsertAsync(trackId, "found", lyrics, at, replaceUserEntry: false, replaceFound: true, cancellationToken);

    public Task<bool> SaveMissAsync(Guid trackId, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        UpsertAsync(trackId, "miss", null, at, replaceUserEntry: false, replaceFound: false, cancellationToken);

    public async Task SaveUserAsync(Guid trackId, TrackLyrics lyrics, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lyrics);
        await UpsertAsync(trackId, "found", lyrics, at, replaceUserEntry: true, replaceFound: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid trackId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM track_lyrics WHERE track_id=$id;";
        command.Parameters.AddWithValue("$id", trackId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> UpsertAsync(Guid trackId, string kind, TrackLyrics? lyrics, DateTimeOffset at, bool replaceUserEntry, bool replaceFound,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        // The WHERE of the conflict clause is what keeps an automatic result from overwriting the user's entry.
        string guard = replaceUserEntry ? "1=1"
            : replaceFound ? $"track_lyrics.origin NOT IN ({UserOrigins})"
            : "track_lyrics.kind = 'miss'";
        command.CommandText = $"""
            INSERT INTO track_lyrics(track_id, kind, origin, source, instrumental, content, stored_utc)
            VALUES ($id, $kind, $origin, $source, $instrumental, $content, $stored)
            ON CONFLICT(track_id) DO UPDATE SET kind=excluded.kind, origin=excluded.origin, source=excluded.source,
                instrumental=excluded.instrumental, content=excluded.content, stored_utc=excluded.stored_utc
            WHERE {guard};
            """;
        command.Parameters.AddWithValue("$id", trackId.ToString("D"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$origin", (lyrics?.Origin ?? LyricsOrigin.Provider).ToString());
        command.Parameters.AddWithValue("$source", lyrics?.Source ?? "");
        command.Parameters.AddWithValue("$instrumental", lyrics?.IsInstrumental == true ? 1 : 0);
        command.Parameters.AddWithValue("$content", lyrics?.ToText() ?? "");
        command.Parameters.AddWithValue("$stored", at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}
