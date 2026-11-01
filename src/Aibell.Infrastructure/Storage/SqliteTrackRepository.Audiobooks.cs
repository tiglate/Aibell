using System.Globalization;
using Aibell.Application.Audiobooks;
using Aibell.Domain.Audiobooks;
using Microsoft.Data.Sqlite;

namespace Aibell.Infrastructure.Storage;

public sealed partial class SqliteTrackRepository : IAudiobookShelfRepository
{
    // Executed once by the schema-12 migration step. Rows only describe shelf entries; audio files are never owned by SQL.
    internal const string AudiobookSchemaV12Sql = """
        CREATE TABLE audiobook_shelf (
            location TEXT PRIMARY KEY NOT NULL CHECK (length(location) BETWEEN 8 AND 8192),
            title TEXT NOT NULL CHECK (length(trim(title)) BETWEEN 1 AND 500),
            author TEXT NOT NULL DEFAULT '',
            duration_ticks INTEGER NOT NULL CHECK (duration_ticks >= 0),
            position_ticks INTEGER NOT NULL DEFAULT 0 CHECK (position_ticks >= 0),
            position_saved_utc TEXT,
            added_utc TEXT NOT NULL,
            CHECK (position_ticks <= duration_ticks)
        );
        """;

    public async Task<IReadOnlyList<AudiobookShelfEntry>> ListShelfAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT location, title, author, duration_ticks, position_ticks, position_saved_utc, added_utc
            FROM audiobook_shelf ORDER BY title COLLATE NOCASE, location;
            """;
        List<AudiobookShelfEntry> entries = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            entries.Add(new(new Uri(reader.GetString(0), UriKind.Absolute), reader.GetString(1), reader.GetString(2),
                TimeSpan.FromTicks(reader.GetInt64(3)), TimeSpan.FromTicks(reader.GetInt64(4)),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        return entries;
    }

    public async Task<bool> AddToShelfAsync(AudiobookShelfEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Validate();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audiobook_shelf (location, title, author, duration_ticks, position_ticks, position_saved_utc, added_utc)
            VALUES ($location, $title, $author, $duration, $position, $saved, $added)
            ON CONFLICT(location) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$location", AudiobookLocation.Key(entry.Location));
        command.Parameters.AddWithValue("$title", entry.Title);
        command.Parameters.AddWithValue("$author", entry.Author);
        command.Parameters.AddWithValue("$duration", entry.Duration.Ticks);
        command.Parameters.AddWithValue("$position", entry.SavedPosition.Ticks);
        command.Parameters.AddWithValue("$saved", entry.PositionSaved is { } saved ? Format(saved) : DBNull.Value);
        command.Parameters.AddWithValue("$added", Format(entry.Added));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> SaveShelfPositionAsync(Uri location, TimeSpan position, TimeSpan duration, DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, duration);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, ResumeRules.MaximumDuration);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE audiobook_shelf SET position_ticks=$position, duration_ticks=$duration, position_saved_utc=$at WHERE location=$location;";
        command.Parameters.AddWithValue("$location", AudiobookLocation.Key(location));
        command.Parameters.AddWithValue("$position", position.Ticks);
        command.Parameters.AddWithValue("$duration", duration.Ticks);
        command.Parameters.AddWithValue("$at", Format(at));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> RemoveFromShelfAsync(Uri location, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM audiobook_shelf WHERE location=$location;";
        command.Parameters.AddWithValue("$location", AudiobookLocation.Key(location));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
