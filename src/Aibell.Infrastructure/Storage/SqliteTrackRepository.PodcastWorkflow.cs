using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;
using Microsoft.Data.Sqlite;

namespace Aibell.Infrastructure.Storage;

public sealed partial class SqliteTrackRepository : IPodcastRatingRepository, IPodcastAutomaticPositionRepository, IPodcastDownloadRepository, IPodcastDownloadLookupRepository
{
    public async Task<StoredPodcastEpisode?> FindDownloadedEpisodeAsync(Uri file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        if (!file.IsAbsoluteUri || !file.IsFile) return null;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // A .NET ordinal collation covers Unicode Windows paths as well as ASCII drive letters.
        connection.CreateCollation("PODCAST_PATH", (left, right) =>
            (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Compare(left, right));
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.subscription_id, e.episode_id, e.title, e.enclosure_url, e.media_type, e.length_bytes,
                e.published_utc, e.duration_seconds, e.description, e.local_path, e.downloaded_bytes, e.downloaded_utc,
                p.position_ticks, p.duration_ticks, p.saved_utc, e.rating, e.image_url
            FROM podcast_episodes e LEFT JOIN podcast_playback_positions p
                ON p.subscription_id=e.subscription_id AND p.episode_id=e.episode_id AND p.local_path=e.local_path
            WHERE e.local_path=$path COLLATE PODCAST_PATH AND e.downloaded_bytes IS NOT NULL AND e.downloaded_utc IS NOT NULL
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$path", Path.GetFullPath(file.LocalPath));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        StoredPodcastEpisode episode = new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), new Uri(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), ParseTime(reader, 6),
            reader.IsDBNull(7) ? null : TimeSpan.FromSeconds(reader.GetDouble(7)), reader.GetString(8), reader.GetString(9),
            reader.GetInt64(10), ParseTime(reader, 11), reader.IsDBNull(12) ? null : new PodcastPlaybackProgress(
                TimeSpan.FromTicks(reader.GetInt64(12)), TimeSpan.FromTicks(reader.GetInt64(13)), ParseTime(reader, 14)!.Value), reader.GetInt32(15), ReadImage(reader, 16));
        // A corrupted/externally edited cache must never attach one input to an arbitrary episode.
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? null : episode;
    }

    public async Task<bool> ClearOwnedDownloadAsync(StoredPodcastEpisode episode, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE podcast_episodes SET local_path=NULL, downloaded_bytes=NULL, downloaded_utc=NULL
            WHERE subscription_id=$sub AND episode_id=$episode AND local_path IS $path
                AND downloaded_utc IS $downloaded AND downloaded_bytes IS $bytes;
            """;
        command.Parameters.AddWithValue("$sub", episode.SubscriptionId.ToString("D"));
        command.Parameters.AddWithValue("$episode", episode.EpisodeId);
        command.Parameters.AddWithValue("$path", (object?)episode.LocalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$downloaded", (object?)FormatTime(episode.DownloadedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", (object?)episode.DownloadedBytes ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> SetEpisodeRatingAsync(Guid subscriptionId, string episodeId, int rating,
        CancellationToken cancellationToken = default)
    {
        if (rating is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(rating));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE podcast_episodes SET rating=$rating WHERE subscription_id=$sub AND episode_id=$episode;";
        command.Parameters.AddWithValue("$rating", rating);
        command.Parameters.AddWithValue("$sub", subscriptionId.ToString("D"));
        command.Parameters.AddWithValue("$episode", episodeId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> SaveAutomaticEpisodePositionAsync(StoredPodcastEpisode episode, PodcastPlaybackProgress progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode); ArgumentNullException.ThrowIfNull(progress); progress.Validate();
        if (episode.LocalPath is null || episode.DownloadedAt is null || episode.DownloadedBytes is null) return false;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO podcast_playback_positions(subscription_id,episode_id,local_path,position_ticks,duration_ticks,saved_utc)
            SELECT subscription_id,episode_id,local_path,$position,$duration,$saved FROM podcast_episodes
            WHERE subscription_id=$sub AND episode_id=$episode AND local_path=$path
                AND downloaded_utc=$downloaded AND downloaded_bytes=$bytes
            ON CONFLICT(subscription_id,episode_id) DO UPDATE SET local_path=excluded.local_path,
                position_ticks=excluded.position_ticks,duration_ticks=excluded.duration_ticks,saved_utc=excluded.saved_utc;
            """;
        command.Parameters.AddWithValue("$sub", episode.SubscriptionId.ToString("D"));
        command.Parameters.AddWithValue("$episode", episode.EpisodeId);
        command.Parameters.AddWithValue("$path", episode.LocalPath);
        command.Parameters.AddWithValue("$downloaded", FormatTime(episode.DownloadedAt));
        command.Parameters.AddWithValue("$bytes", episode.DownloadedBytes.Value);
        command.Parameters.AddWithValue("$position", progress.Position.Ticks);
        command.Parameters.AddWithValue("$duration", progress.Duration.Ticks);
        command.Parameters.AddWithValue("$saved", FormatTime(progress.SavedAt));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
