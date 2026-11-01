using Aibell.Domain.Podcasts;

namespace Aibell.Application.Podcasts;

public interface IPodcastRepository
{
    Task<IReadOnlyList<PodcastSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredPodcastEpisode>> ListEpisodesAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    // Atomically stores the subscription and its first episodes. Returns false, changing nothing, when the feed URL is already subscribed.
    Task<bool> AddSubscriptionAsync(PodcastSubscription subscription, IReadOnlyList<StoredPodcastEpisode> episodes,
        CancellationToken cancellationToken = default);

    // One transaction: replaces show details/validators and upserts episodes by (subscription, episode id). Download
    // columns of existing episodes and episodes absent from the new feed are kept. A null episode list only records the
    // refresh time (HTTP 304). Returns false when the subscription no longer exists.
    Task<bool> ApplyRefreshAsync(PodcastSubscription subscription, IReadOnlyList<StoredPodcastEpisode>? episodes,
        CancellationToken cancellationToken = default);

    // Removes cached rows only; it never touches downloaded files.
    Task<bool> RemoveSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    Task<bool> RecordDownloadAsync(Guid subscriptionId, string episodeId, string localPath, long bytes, DateTimeOffset at,
        CancellationToken cancellationToken = default);
    Task<bool> ClearDownloadAsync(Guid subscriptionId, string episodeId, CancellationToken cancellationToken = default);
}

public interface IPodcastPlaybackHandoff
{
    Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default);
}

public sealed record PodcastPlaybackRequest(Uri File, string Title, string Show, TimeSpan? Duration, TimeSpan StartPosition = default,
    StoredPodcastEpisode? Episode = null);
