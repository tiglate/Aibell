using Aibell.Domain.Podcasts;

namespace Aibell.Application.Podcasts;

public interface IPodcastRatingRepository
{
    Task<bool> SetEpisodeRatingAsync(Guid subscriptionId, string episodeId, int rating,
        CancellationToken cancellationToken = default);
}

/// <summary>Episodes first seen by a refresh are "new" until the user plays, downloads or dismisses them.</summary>
public interface IPodcastNewEpisodeRepository
{
    Task<IReadOnlyDictionary<Guid, int>> CountNewEpisodesAsync(CancellationToken cancellationToken = default);

    // Null clears every new episode of the subscription. Returns how many rows changed.
    Task<int> MarkEpisodesSeenAsync(Guid subscriptionId, IReadOnlyCollection<string>? episodeIds, CancellationToken cancellationToken = default);
}

public interface IPodcastAutomaticPositionRepository
{
    // Guard every component of the download identity, including a same-path replacement.
    Task<bool> SaveAutomaticEpisodePositionAsync(StoredPodcastEpisode episode, PodcastPlaybackProgress progress,
        CancellationToken cancellationToken = default);
}

/// <summary>A reversible rename within the owned folder. Dispose restores until Complete is called.</summary>
public interface IPodcastDownloadDeletion : IDisposable
{
    void Complete();
}

public interface IPodcastDownloadDeletionStore
{
    // Platform-specific canonical local-file comparison; no filesystem deletion.
    bool IsSameFile(string path, Uri file);
    // Reject unsafe paths, including links. A missing, otherwise owned path is allowed.
    IPodcastDownloadDeletion StageDelete(Guid subscriptionId, string episodeId, string path);
}

public interface IPodcastDownloadRepository
{
    Task<bool> ClearOwnedDownloadAsync(StoredPodcastEpisode episode, CancellationToken cancellationToken = default);
}

/// <summary>Optional read-only lookup; ambiguous paths must return null.</summary>
public interface IPodcastDownloadLookupRepository
{
    Task<StoredPodcastEpisode?> FindDownloadedEpisodeAsync(Uri file, CancellationToken cancellationToken = default);
}

public interface IPodcastDownloadIdentityStore
{
    bool OwnsDownload(Guid subscriptionId, string episodeId, string path);
}
