using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;
using Microsoft.Data.Sqlite;

namespace Aibell.Infrastructure.Storage;

public sealed partial class SqliteTrackRepository : IPodcastPositionRepository
{
    internal const string PodcastPositionsSchemaV13Sql = """
        CREATE TABLE podcast_playback_positions (
            subscription_id TEXT NOT NULL,
            episode_id TEXT NOT NULL,
            local_path TEXT NOT NULL,
            position_ticks INTEGER NOT NULL CHECK (position_ticks >= 0),
            duration_ticks INTEGER NOT NULL CHECK (duration_ticks > 0 AND duration_ticks <= 25920000000000),
            saved_utc TEXT NOT NULL,
            PRIMARY KEY (subscription_id, episode_id),
            FOREIGN KEY (subscription_id, episode_id) REFERENCES podcast_episodes(subscription_id, episode_id) ON DELETE CASCADE,
            CHECK (position_ticks <= duration_ticks)
        );
        CREATE TRIGGER clear_podcast_position_after_download_change
        AFTER UPDATE OF local_path, downloaded_utc, downloaded_bytes ON podcast_episodes
        WHEN OLD.local_path IS NOT NEW.local_path OR OLD.downloaded_utc IS NOT NEW.downloaded_utc
            OR OLD.downloaded_bytes IS NOT NEW.downloaded_bytes
        BEGIN
            DELETE FROM podcast_playback_positions WHERE subscription_id=NEW.subscription_id AND episode_id=NEW.episode_id;
        END;
        """;

    public async Task<bool> SaveEpisodePositionAsync(Guid subscriptionId, string episodeId, string localPath,
        PodcastPlaybackProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress); progress.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO podcast_playback_positions(subscription_id,episode_id,local_path,position_ticks,duration_ticks,saved_utc)
            SELECT subscription_id,episode_id,local_path,$position,$duration,$saved FROM podcast_episodes
            WHERE subscription_id=$sub AND episode_id=$episode AND local_path=$path
            ON CONFLICT(subscription_id,episode_id) DO UPDATE SET local_path=excluded.local_path,
                position_ticks=excluded.position_ticks,duration_ticks=excluded.duration_ticks,saved_utc=excluded.saved_utc;
            """;
        command.Parameters.AddWithValue("$sub", subscriptionId.ToString("D"));
        command.Parameters.AddWithValue("$episode", episodeId); command.Parameters.AddWithValue("$path", localPath);
        command.Parameters.AddWithValue("$position", progress.Position.Ticks); command.Parameters.AddWithValue("$duration", progress.Duration.Ticks);
        command.Parameters.AddWithValue("$saved", FormatTime(progress.SavedAt));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
