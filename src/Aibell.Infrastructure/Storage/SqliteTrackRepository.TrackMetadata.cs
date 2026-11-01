using System.Text.Json;
using Aibell.Domain.Media;
using Microsoft.Data.Sqlite;

namespace Aibell.Infrastructure.Storage;

public sealed partial class SqliteTrackRepository
{
    public Task<Track> SaveMetadataAsync(Guid trackId, TrackMetadata metadata, CancellationToken cancellationToken = default) =>
        SaveMetadataAsync(trackId, metadata, null, cancellationToken);

    public async Task<Track> SaveMetadataAsync(Guid trackId, TrackMetadata metadata, Aibell.Application.Library.TrackFileState? fileState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Validate();
        if (fileState?.Size < 0) throw new ArgumentOutOfRangeException(nameof(fileState));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tracks SET title=$title, artist=$artist, album=$album, album_artist=$albumArtist,
                genre=$genre, year=$year, track_number=$trackNumber, disc_number=$discNumber,
                rating=$rating, bpm=$bpm, composer=$composer, comment=$comment,
                description=$description, keywords=$keywords, show_name=$showName,
                season_number=$seasonNumber, episode_number=$episodeNumber, metadata_json=$metadata,
                file_size=COALESCE($fileSize,file_size), date_modified_utc=COALESCE($fileModified,date_modified_utc)
            WHERE id=$id
            RETURNING id, location, title, artist, album, duration_ticks, genre,
                album_artist, track_number, year, rating, bit_rate, bpm, channels, comment,
                composer, date_added_utc, date_modified_utc, description, disc_number,
                episode_number, keywords, last_played_utc, last_skipped_utc, play_count,
                sample_rate, season_number, show_name, file_size, skip_count, source, metadata_json;
            """;
        command.Parameters.AddWithValue("$fileSize", (object?)fileState?.Size ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileModified", fileState is null ? DBNull.Value : fileState.ModifiedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$id", trackId.ToString("D"));
        command.Parameters.AddWithValue("$title", metadata.Title);
        command.Parameters.AddWithValue("$artist", metadata.Artist);
        command.Parameters.AddWithValue("$album", metadata.Album);
        command.Parameters.AddWithValue("$albumArtist", metadata.AlbumArtist);
        command.Parameters.AddWithValue("$genre", metadata.Genre);
        command.Parameters.AddWithValue("$year", metadata.Year);
        command.Parameters.AddWithValue("$trackNumber", metadata.TrackNumber);
        command.Parameters.AddWithValue("$discNumber", metadata.DiscNumber);
        command.Parameters.AddWithValue("$rating", metadata.Rating);
        command.Parameters.AddWithValue("$bpm", metadata.BeatsPerMinute);
        command.Parameters.AddWithValue("$composer", metadata.Composer);
        command.Parameters.AddWithValue("$comment", metadata.Comment);
        command.Parameters.AddWithValue("$description", metadata.Description);
        command.Parameters.AddWithValue("$keywords", metadata.Keywords);
        command.Parameters.AddWithValue("$showName", metadata.ShowName);
        command.Parameters.AddWithValue("$seasonNumber", metadata.SeasonNumber);
        command.Parameters.AddWithValue("$episodeNumber", metadata.EpisodeNumber);
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The track is no longer in the library.");
        return ReadTrack(reader);
    }
}
