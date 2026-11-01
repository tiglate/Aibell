using System.Globalization;
using Aibell.Application.Radio;
using Aibell.Domain.Radio;
using Microsoft.Data.Sqlite;

namespace Aibell.Infrastructure.Storage;

public sealed partial class SqliteTrackRepository : IRadioStationRepository
{
    public async Task<IReadOnlyList<RadioStation>> ListStationsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, stream, genre, creator, description, rating, play_count, last_played_utc, country, country_code, logo_url, catalog_id FROM radio_stations ORDER BY name COLLATE NOCASE, id;";
        List<RadioStation> stations = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            RadioStation station = new(Guid.Parse(reader.GetString(0)), reader.GetString(1), new Uri(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                Optional(reader, 9), Optional(reader, 10), Optional(reader, 11) is { } logo ? new Uri(logo) : null, Optional(reader, 12));
            station.Validate(); stations.Add(station);
        }
        return stations;
    }

    private static string? Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public Task AddStationAsync(RadioStation station, CancellationToken cancellationToken = default) => SaveStationAsync(station, true, cancellationToken);
    public Task UpdateStationAsync(RadioStation station, CancellationToken cancellationToken = default) => SaveStationAsync(station, false, cancellationToken);

    public async Task<RadioStationImportResult> ImportStationsAsync(IReadOnlyList<RadioStation> stations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stations);
        cancellationToken.ThrowIfCancellationRequested();
        if (stations.Count > 10000) throw new ArgumentException("Import at most 10,000 stations at a time.", nameof(stations));
        foreach (RadioStation station in stations)
        {
            ArgumentNullException.ThrowIfNull(station);
            station.Validate();
        }
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        int imported = 0;
        foreach (RadioStation station in stations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO radio_stations (id, name, stream, genre, creator, description, rating, country, country_code, logo_url, catalog_id)
                VALUES ($id, $name, $stream, $genre, $creator, $description, $rating, $country, $countryCode, $logo, $catalogId)
                ON CONFLICT(stream) DO NOTHING
                ON CONFLICT(catalog_id) WHERE catalog_id IS NOT NULL DO NOTHING;
                """;
            AddStationParameters(command, station);
            imported += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(imported, stations.Count - imported);
    }

    private async Task SaveStationAsync(RadioStation station, bool add, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(station); station.Validate();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = add
            ? "INSERT INTO radio_stations (id, name, stream, genre, creator, description, rating, country, country_code, logo_url, catalog_id) VALUES ($id, $name, $stream, $genre, $creator, $description, $rating, $country, $countryCode, $logo, $catalogId);"
            : "UPDATE radio_stations SET name=$name, stream=$stream, genre=$genre, creator=$creator, description=$description, rating=$rating, country=$country, country_code=$countryCode, logo_url=$logo, catalog_id=$catalogId WHERE id=$id;";
        AddStationParameters(command, station);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The station no longer exists. Refresh Radio before editing.");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        { throw new InvalidOperationException("A station with this stream URL or identifier already exists.", exception); }
    }

    private static void AddStationParameters(SqliteCommand command, RadioStation station)
    {
        command.Parameters.AddWithValue("$id", station.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", station.Name.Trim()); command.Parameters.AddWithValue("$stream", station.Stream.AbsoluteUri);
        command.Parameters.AddWithValue("$genre", station.Genre.Trim()); command.Parameters.AddWithValue("$creator", station.Creator.Trim());
        command.Parameters.AddWithValue("$description", station.Description.Trim()); command.Parameters.AddWithValue("$rating", station.Rating);
        command.Parameters.AddWithValue("$country", Blank(station.Country)); command.Parameters.AddWithValue("$countryCode", Blank(station.CountryCode));
        command.Parameters.AddWithValue("$logo", station.LogoUri is null ? DBNull.Value : station.LogoUri.AbsoluteUri);
        command.Parameters.AddWithValue("$catalogId", Blank(station.CatalogId));
    }

    private static object Blank(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    public async Task RemoveStationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM radio_stations WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordStationPlayedAsync(Guid id, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE radio_stations SET play_count=play_count+1, last_played_utc=$played WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D")); command.Parameters.AddWithValue("$played", occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
