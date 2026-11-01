namespace Aibell.Domain.Podcasts;

public sealed record PodcastSubscription(Guid Id, Uri FeedUrl, string Title, string Description,
    DateTimeOffset Subscribed, string? EntityTag = null, DateTimeOffset? LastModified = null,
    DateTimeOffset? LastRefreshed = null)
{
    public const int MaximumTitleLength = 500;
    public const int MaximumDescriptionLength = 4000;

    public void Validate()
    {
        if (Id == Guid.Empty) throw new ArgumentException("A subscription needs an identifier.");
        if (!PodcastLocation.IsSupported(FeedUrl))
            throw new ArgumentException("Use a complete HTTP or HTTPS feed URL without embedded credentials or a fragment.");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > MaximumTitleLength)
            throw new ArgumentException($"A podcast title needs 1–{MaximumTitleLength} characters.");
        if (Description is null || Description.Length > MaximumDescriptionLength)
            throw new ArgumentException("The podcast description is too long.");
    }

    /// <summary>Returns the canonical identity used to detect duplicate subscriptions, or null when unsupported.</summary>
    public static Uri? NormalizeFeedUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri)) return null;
        return PodcastLocation.IsSupported(uri) ? new Uri(uri.AbsoluteUri, UriKind.Absolute) : null;
    }
}

public enum PodcastDownloadState
{
    NotDownloaded,
    Downloaded
}

/// <summary>A cached episode. Identity is (SubscriptionId, EpisodeId), where EpisodeId is the feed's opaque identifier.</summary>
public sealed record StoredPodcastEpisode(Guid SubscriptionId, string EpisodeId, string Title, Uri Enclosure,
    string? MediaType = null, long? Length = null, DateTimeOffset? Published = null, TimeSpan? Duration = null,
    string Description = "", string? LocalPath = null, long? DownloadedBytes = null, DateTimeOffset? DownloadedAt = null,
    PodcastPlaybackProgress? SavedProgress = null, int Rating = 0, Uri? ImageUrl = null, bool IsNew = false)
{
    public const int MaximumEpisodesPerFeed = 5000;

    public void Validate()
    {
        SavedProgress?.Validate();
        if (Rating is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(Rating), "Episode rating must be between 0 and 5.");
        if (SubscriptionId == Guid.Empty) throw new ArgumentException("An episode needs a subscription identifier.");
        if (string.IsNullOrWhiteSpace(EpisodeId) || EpisodeId.Length > 4096)
            throw new ArgumentException("An episode identifier needs 1–4096 characters.");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 500) throw new ArgumentException("An episode title needs 1–500 characters.");
        if (Description is null || Description.Length > PodcastSubscription.MaximumDescriptionLength)
            throw new ArgumentException("The episode description is too long.");
        if (!PodcastLocation.IsSupported(Enclosure)) throw new ArgumentException("The episode enclosure must be a plain HTTP or HTTPS URL.");
        if (ImageUrl is not null && !PodcastLocation.IsSupported(ImageUrl)) throw new ArgumentException("The episode image must be a plain HTTP or HTTPS URL.");
        if (Length is < 0 || DownloadedBytes is < 0) throw new ArgumentException("Episode sizes cannot be negative.");
        if (Duration is { } duration && duration < TimeSpan.Zero) throw new ArgumentException("An episode duration cannot be negative.");
    }

    /// <summary>Checks a whole batch for one subscription before anything is stored.</summary>
    public static void ValidateBatch(Guid subscriptionId, IReadOnlyList<StoredPodcastEpisode> episodes)
    {
        if (episodes.Count > MaximumEpisodesPerFeed) throw new ArgumentException($"A feed can cache at most {MaximumEpisodesPerFeed} episodes.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (StoredPodcastEpisode episode in episodes)
        {
            ArgumentNullException.ThrowIfNull(episode);
            if (episode.SubscriptionId != subscriptionId) throw new ArgumentException("An episode belongs to a different subscription.");
            episode.Validate();
            if (!ids.Add(episode.EpisodeId)) throw new ArgumentException("Episode identifiers must be unique within a feed.");
        }
    }

    public PodcastDownloadState DownloadState =>
        LocalPath is null ? PodcastDownloadState.NotDownloaded : PodcastDownloadState.Downloaded;
}
