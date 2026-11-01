using System.Globalization;
using System.Text.Json;
using Aibell.Domain.Media;
using Aibell.Domain.Playlists;
using Microsoft.Data.Sqlite;

namespace Aibell.Infrastructure.Storage;

public sealed partial class SqliteTrackRepository
{
    public Task<Playlist> CreateSmartAsync(string name, SmartPlaylistDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        return CreatePlaylistAsync(name, null, cancellationToken,
            definition with { Rules = definition.Rules.ToArray() });
    }

    public async Task UpdateSmartDefinitionAsync(Guid playlistId, SmartPlaylistDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        string json = JsonSerializer.Serialize(definition);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE smart_playlist_rules SET definition_json=$definition WHERE playlist_id=$playlist;";
        command.Parameters.AddWithValue("$definition", json);
        command.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new InvalidOperationException("The smart playlist no longer exists.");
    }

    private static SmartPlaylistDefinition ReadSmartDefinition(string json)
    {
        try
        {
            SmartPlaylistDefinition? definition = JsonSerializer.Deserialize<SmartPlaylistDefinition>(json);
            definition?.Validate();
            return definition ?? SmartPlaylistDefinition.Unavailable;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return SmartPlaylistDefinition.Unavailable;
        }
    }

    private static async Task<SmartPlaylistDefinition?> GetSmartDefinitionAsync(SqliteConnection connection,
        Guid playlistId, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT definition_json FROM smart_playlist_rules WHERE playlist_id=$playlist;";
        command.Parameters.AddWithValue("$playlist", playlistId.ToString("D"));
        object? json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return json is string value ? ReadSmartDefinition(value) : null;
    }

    private static async Task EnsureManualPlaylistAsync(SqliteConnection connection, Guid playlistId,
        CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        if (await GetSmartDefinitionAsync(connection, playlistId, cancellationToken, transaction).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Smart playlist membership is determined by its rules.");
    }

    private static async Task<IReadOnlyList<Track>> QuerySmartTracksAsync(SqliteConnection connection,
        SmartPlaylistDefinition definition, string query, CancellationToken cancellationToken)
    {
        if (definition.Rules.Count == 0) return [];
        const string projection = """
            id, location, title, artist, album, duration_ticks, genre,
            album_artist, track_number, year, rating, bit_rate, bpm,
            channels, comment, composer, date_added_utc, date_modified_utc,
            description, disc_number, episode_number, keywords,
            last_played_utc, last_skipped_utc, play_count, sample_rate,
            season_number, show_name, file_size, skip_count, source, metadata_json
            """;
        await using SqliteCommand command = CreateSmartCommand(connection, definition, query, projection);
        List<Track> result = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadTrack(reader));
        return result;
    }

    private static async Task<IReadOnlyList<Guid>> QuerySmartTrackIdsAsync(SqliteConnection connection,
        SmartPlaylistDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.Rules.Count == 0) return [];
        await using SqliteCommand command = CreateSmartCommand(connection, definition, string.Empty, "id");
        List<Guid> result = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    private static SqliteCommand CreateSmartCommand(SqliteConnection connection,
        SmartPlaylistDefinition definition, string query, string projection)
    {
        definition.Validate();
        SqliteCommand command = connection.CreateCommand();
        List<string> conditions = [];
        for (int index = 0; index < definition.Rules.Count; index++)
        {
            SmartPlaylistRule rule = definition.Rules[index];
            string column = rule.Field switch
            {
                SmartPlaylistField.Title => "title",
                SmartPlaylistField.Artist => "artist",
                SmartPlaylistField.Album => "album",
                SmartPlaylistField.Genre => "genre",
                SmartPlaylistField.Rating => "rating",
                SmartPlaylistField.Year => "year",
                SmartPlaylistField.PlayCount => "play_count",
                SmartPlaylistField.SkipCount => "skip_count",
                _ => throw new ArgumentOutOfRangeException(nameof(definition))
            };
            string parameter = "$rule" + index.ToString(CultureInfo.InvariantCulture);
            string comparison = rule.Operator switch
            {
                SmartPlaylistOperator.Equals => "=",
                SmartPlaylistOperator.NotEquals => "<>",
                SmartPlaylistOperator.GreaterOrEqual => ">=",
                SmartPlaylistOperator.LessOrEqual => "<=",
                SmartPlaylistOperator.Contains => "",
                _ => throw new ArgumentOutOfRangeException(nameof(definition))
            };
            conditions.Add(rule.Operator == SmartPlaylistOperator.Contains
                ? $"instr(lower({column}), lower({parameter})) > 0"
                : $"{column} {comparison} {parameter}" + (rule.IsNumeric ? "" : " COLLATE NOCASE"));
            command.Parameters.AddWithValue(parameter, rule.IsNumeric
                ? int.Parse(rule.Value, CultureInfo.InvariantCulture) : (object)rule.Value);
        }
        string predicate = string.Join(definition.Match == SmartPlaylistMatch.All ? " AND " : " OR ", conditions);
        command.CommandText = $"""
            SELECT {projection}
            FROM tracks
            WHERE ({predicate}) AND ($query = ''
                OR instr(lower(title), lower($query)) > 0
                OR instr(lower(artist), lower($query)) > 0
                OR instr(lower(album), lower($query)) > 0
                OR instr(lower(genre), lower($query)) > 0)
            ORDER BY artist COLLATE NOCASE, album COLLATE NOCASE, title COLLATE NOCASE, id;
            """;
        command.Parameters.AddWithValue("$query", query.Trim());
        return command;
    }
}
