using System.Globalization;
using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;
using Microsoft.Data.Sqlite;

namespace Aibell.Infrastructure.Storage;

public sealed partial class SqliteTrackRepository : IPodcastRepository
{
    // Executed once by the schema-11 migration step. Foreign keys cascade only cached rows; downloaded files are never owned by SQL.
    internal const string PodcastSchemaV11Sql = """
        CREATE TABLE podcast_subscriptions (
            id TEXT PRIMARY KEY NOT NULL,
            feed_url TEXT NOT NULL UNIQUE,
            title TEXT NOT NULL CHECK (length(trim(title)) BETWEEN 1 AND 500),
            description TEXT NOT NULL DEFAULT '',
            entity_tag TEXT, last_modified_utc TEXT,
            subscribed_utc TEXT NOT NULL, last_refreshed_utc TEXT
        );
        CREATE TABLE podcast_episodes (
            subscription_id TEXT NOT NULL REFERENCES podcast_subscriptions(id) ON DELETE CASCADE,
            episode_id TEXT NOT NULL CHECK (length(episode_id) BETWEEN 1 AND 4096),
            title TEXT NOT NULL, enclosure_url TEXT NOT NULL, media_type TEXT,
            length_bytes INTEGER CHECK (length_bytes IS NULL OR length_bytes >= 0),
            published_utc TEXT, duration_seconds REAL CHECK (duration_seconds IS NULL OR duration_seconds >= 0), description TEXT NOT NULL DEFAULT '',
            local_path TEXT, downloaded_bytes INTEGER CHECK (downloaded_bytes IS NULL OR downloaded_bytes >= 0), downloaded_utc TEXT,
            PRIMARY KEY (subscription_id, episode_id)
        );
        CREATE INDEX ix_podcast_episodes_published ON podcast_episodes(subscription_id, published_utc DESC);
        """;

    public async Task<IReadOnlyList<PodcastSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, feed_url, title, description, entity_tag, last_modified_utc, subscribed_utc, last_refreshed_utc
            FROM podcast_subscriptions ORDER BY title COLLATE NOCASE, id;
            """;
        List<PodcastSubscription> subscriptions = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            subscriptions.Add(new(Guid.Parse(reader.GetString(0)), new Uri(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                ParseTime(reader.GetString(6))!.Value, reader.IsDBNull(4) ? null : reader.GetString(4), ParseTime(reader, 5), ParseTime(reader, 7)));
        return subscriptions;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountNewEpisodesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT subscription_id, COUNT(*) FROM podcast_episodes WHERE is_new=1 GROUP BY subscription_id;";
        Dictionary<Guid, int> counts = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) counts[Guid.Parse(reader.GetString(0))] = reader.GetInt32(1);
        return counts;
    }

    public async Task<int> MarkEpisodesSeenAsync(Guid subscriptionId, IReadOnlyCollection<string>? episodeIds, CancellationToken cancellationToken = default)
    {
        if (episodeIds is { Count: 0 }) return 0;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        int changed = 0;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = episodeIds is null
                ? "UPDATE podcast_episodes SET is_new=0 WHERE subscription_id=$sub AND is_new=1;"
                : "UPDATE podcast_episodes SET is_new=0 WHERE subscription_id=$sub AND episode_id=$episode AND is_new=1;";
            command.Parameters.AddWithValue("$sub", subscriptionId.ToString("D"));
            SqliteParameter episode = command.Parameters.Add("$episode", SqliteType.Text);
            if (episodeIds is null) changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            else foreach (string id in episodeIds)
                {
                    episode.Value = id;
                    changed += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private static Uri? ReadImage(SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && Uri.TryCreate(reader.GetString(ordinal), UriKind.Absolute, out Uri? uri) && PodcastLocation.IsSupported(uri) ? uri : null;

    public async Task<IReadOnlyList<StoredPodcastEpisode>> ListEpisodesAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.episode_id, e.title, e.enclosure_url, e.media_type, e.length_bytes, e.published_utc, e.duration_seconds, e.description,
                   e.local_path, e.downloaded_bytes, e.downloaded_utc, p.position_ticks, p.duration_ticks, p.saved_utc, e.rating, e.image_url, e.is_new
            FROM podcast_episodes e LEFT JOIN podcast_playback_positions p
                ON p.subscription_id=e.subscription_id AND p.episode_id=e.episode_id AND p.local_path=e.local_path
            WHERE e.subscription_id=$id
            ORDER BY e.published_utc IS NULL, e.published_utc DESC, e.title COLLATE NOCASE, e.episode_id;
            """;
        command.Parameters.AddWithValue("$id", subscriptionId.ToString("D"));
        List<StoredPodcastEpisode> episodes = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            episodes.Add(new(subscriptionId, reader.GetString(0), reader.GetString(1), new Uri(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4), ParseTime(reader, 5),
                reader.IsDBNull(6) ? null : TimeSpan.FromSeconds(reader.GetDouble(6)), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), ParseTime(reader, 10),
                reader.IsDBNull(11) ? null : new PodcastPlaybackProgress(TimeSpan.FromTicks(reader.GetInt64(11)),
                    TimeSpan.FromTicks(reader.GetInt64(12)), ParseTime(reader, 13)!.Value), reader.GetInt32(14), ReadImage(reader, 15), reader.GetInt32(16) != 0));
        return episodes;
    }

    public async Task<bool> AddSubscriptionAsync(PodcastSubscription subscription, IReadOnlyList<StoredPodcastEpisode> episodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription); ArgumentNullException.ThrowIfNull(episodes);
        subscription.Validate(); StoredPodcastEpisode.ValidateBatch(subscription.Id, episodes);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO podcast_subscriptions (id, feed_url, title, description, entity_tag, last_modified_utc, subscribed_utc, last_refreshed_utc)
                VALUES ($id, $url, $title, $description, $etag, $modified, $subscribed, $refreshed)
                ON CONFLICT(feed_url) DO NOTHING;
                """;
            AddSubscriptionParameters(command, subscription);
            command.Parameters.AddWithValue("$subscribed", FormatTime(subscription.Subscribed));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
        }
        await UpsertEpisodesAsync(connection, transaction, subscription.Id, episodes, markNew: false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ApplyRefreshAsync(PodcastSubscription subscription, IReadOnlyList<StoredPodcastEpisode>? episodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription); subscription.Validate();
        if (episodes is not null) StoredPodcastEpisode.ValidateBatch(subscription.Id, episodes);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // A 304 keeps the stored details and validators; a 200 replaces them (absent validators clear stale ones).
            command.CommandText = episodes is null
                ? "UPDATE podcast_subscriptions SET last_refreshed_utc=$refreshed WHERE id=$id;"
                : """
                  UPDATE podcast_subscriptions SET title=$title, description=$description, entity_tag=$etag,
                      last_modified_utc=$modified, last_refreshed_utc=$refreshed WHERE id=$id;
                  """;
            command.Parameters.AddWithValue("$id", subscription.Id.ToString("D"));
            command.Parameters.AddWithValue("$refreshed", (object?)FormatTime(subscription.LastRefreshed) ?? DBNull.Value);
            if (episodes is not null)
            {
                command.Parameters.AddWithValue("$title", subscription.Title); command.Parameters.AddWithValue("$description", subscription.Description);
                command.Parameters.AddWithValue("$etag", (object?)subscription.EntityTag ?? DBNull.Value);
                command.Parameters.AddWithValue("$modified", (object?)FormatTime(subscription.LastModified) ?? DBNull.Value);
            }
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
        }
        if (episodes is not null)
            await UpsertEpisodesAsync(connection, transaction, subscription.Id, episodes, markNew: true, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task UpsertEpisodesAsync(SqliteConnection connection, SqliteTransaction transaction, Guid subscriptionId,
        IReadOnlyList<StoredPodcastEpisode> episodes, bool markNew, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        // Download columns are deliberately absent from the update list so refresh preserves downloaded state.
        command.CommandText = """
            INSERT INTO podcast_episodes (subscription_id, episode_id, title, enclosure_url, media_type, length_bytes, published_utc, duration_seconds, description, rating, image_url, is_new)
            VALUES ($sub, $episode, $title, $enclosure, $type, $length, $published, $duration, $description, $rating, $image, $new)
            ON CONFLICT(subscription_id, episode_id) DO UPDATE SET title=excluded.title, enclosure_url=excluded.enclosure_url,
                media_type=excluded.media_type, length_bytes=excluded.length_bytes, published_utc=excluded.published_utc,
                duration_seconds=excluded.duration_seconds, description=excluded.description, image_url=excluded.image_url;
            """;
        SqliteParameter episode = command.Parameters.Add("$episode", SqliteType.Text), title = command.Parameters.Add("$title", SqliteType.Text),
            enclosure = command.Parameters.Add("$enclosure", SqliteType.Text), type = command.Parameters.Add("$type", SqliteType.Text),
            length = command.Parameters.Add("$length", SqliteType.Integer), published = command.Parameters.Add("$published", SqliteType.Text),
            duration = command.Parameters.Add("$duration", SqliteType.Real), description = command.Parameters.Add("$description", SqliteType.Text),
            rating = command.Parameters.Add("$rating", SqliteType.Integer), image = command.Parameters.Add("$image", SqliteType.Text);
        command.Parameters.AddWithValue("$sub", subscriptionId.ToString("D"));
        // Only rows inserted by a refresh are new; an update of an existing row never changes the flag.
        command.Parameters.AddWithValue("$new", markNew ? 1 : 0);
        foreach (StoredPodcastEpisode item in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            episode.Value = item.EpisodeId; title.Value = item.Title; enclosure.Value = item.Enclosure.AbsoluteUri;
            type.Value = (object?)item.MediaType ?? DBNull.Value; length.Value = (object?)item.Length ?? DBNull.Value;
            published.Value = (object?)FormatTime(item.Published) ?? DBNull.Value;
            duration.Value = item.Duration is { } span ? span.TotalSeconds : DBNull.Value; description.Value = item.Description; rating.Value = item.Rating;
            image.Value = (object?)item.ImageUrl?.AbsoluteUri ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> RemoveSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM podcast_subscriptions WHERE id=$id;";
        command.Parameters.AddWithValue("$id", subscriptionId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> RecordDownloadAsync(Guid subscriptionId, string episodeId, string localPath, long bytes, DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath); ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE podcast_episodes SET local_path=$path, downloaded_bytes=$bytes, downloaded_utc=$at WHERE subscription_id=$sub AND episode_id=$episode;";
        command.Parameters.AddWithValue("$sub", subscriptionId.ToString("D")); command.Parameters.AddWithValue("$episode", episodeId);
        command.Parameters.AddWithValue("$path", localPath); command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$at", FormatTime(at));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> ClearDownloadAsync(Guid subscriptionId, string episodeId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE podcast_episodes SET local_path=NULL, downloaded_bytes=NULL, downloaded_utc=NULL WHERE subscription_id=$sub AND episode_id=$episode;";
        command.Parameters.AddWithValue("$sub", subscriptionId.ToString("D")); command.Parameters.AddWithValue("$episode", episodeId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void AddSubscriptionParameters(SqliteCommand command, PodcastSubscription subscription)
    {
        command.Parameters.AddWithValue("$id", subscription.Id.ToString("D")); command.Parameters.AddWithValue("$url", subscription.FeedUrl.AbsoluteUri);
        command.Parameters.AddWithValue("$title", subscription.Title); command.Parameters.AddWithValue("$description", subscription.Description);
        command.Parameters.AddWithValue("$etag", (object?)subscription.EntityTag ?? DBNull.Value);
        command.Parameters.AddWithValue("$modified", (object?)FormatTime(subscription.LastModified) ?? DBNull.Value);
        command.Parameters.AddWithValue("$refreshed", (object?)FormatTime(subscription.LastRefreshed) ?? DBNull.Value);
    }

    private static string? FormatTime(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset? ParseTime(string? value) => value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static DateTimeOffset? ParseTime(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ParseTime(reader.GetString(ordinal));
}
