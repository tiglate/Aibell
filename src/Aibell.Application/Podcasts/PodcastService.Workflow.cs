using System.Collections.Concurrent;
using Aibell.Domain.Podcasts;

namespace Aibell.Application.Podcasts;

public sealed partial class PodcastService
{
    private readonly IPodcastRatingRepository? ratings;
    private readonly ConcurrentDictionary<(Guid, string), SemaphoreSlim> episodeLocks = new();
    private readonly ConcurrentDictionary<(Guid, string), int> pendingPlaybacks = new();
    public bool SupportsRatings => ratings is not null;

    /// <summary>Unseen episodes per subscription. Empty when the repository does not track them.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> CountNewEpisodesAsync(CancellationToken cancellationToken = default) =>
        repository is IPodcastNewEpisodeRepository tracker
            ? await tracker.CountNewEpisodesAsync(cancellationToken).ConfigureAwait(false)
            : new Dictionary<Guid, int>();

    /// <summary>Clears the "new" flag of the given episodes, or of the whole subscription when <paramref name="episodeIds"/> is null.</summary>
    public Task<int> MarkEpisodesSeenAsync(Guid subscriptionId, IReadOnlyCollection<string>? episodeIds = null,
        CancellationToken cancellationToken = default) =>
        repository is IPodcastNewEpisodeRepository tracker
            ? tracker.MarkEpisodesSeenAsync(subscriptionId, episodeIds, cancellationToken)
            : Task.FromResult(0);

    /// <summary>
    /// Refreshes every subscription one after another with the stored validators, so unchanged feeds cost a 304. A failing
    /// feed is recorded and the rest continue. A podcast that is already refreshing is skipped, not an error.
    /// </summary>
    public async Task<PodcastRefreshAllResult> RefreshAllAsync(IProgress<PodcastRefreshAllProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PodcastSubscription> subscriptions = await repository.ListSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        int refreshed = 0, newEpisodes = 0;
        List<PodcastRefreshFailure> failures = [];
        for (int index = 0; index < subscriptions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PodcastSubscription subscription = subscriptions[index];
            progress?.Report(new(index + 1, subscriptions.Count, subscription.Title));
            try
            {
                PodcastRefreshResult result = await RefreshAsync(subscription.Id, cancellationToken).ConfigureAwait(false);
                refreshed++; newEpisodes += Math.Max(0, result.NewEpisodes);
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException exception) when (exception.Message.Contains("already refreshing", StringComparison.Ordinal)) { }
            catch (Exception exception) { failures.Add(new(subscription.Title, exception.Message)); }
        }
        return new(subscriptions.Count, refreshed, newEpisodes, failures);
    }
    public bool SupportsDeletion => store is IPodcastDownloadDeletionStore && repository is IPodcastDownloadRepository;

    /// <summary>Recognizes an owned cached episode even before the Podcasts screen has ever played it.</summary>
    public async Task<StoredPodcastEpisode?> FindDownloadedEpisodeAsync(Uri file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        if (!file.IsAbsoluteUri || !file.IsFile || repository is not IPodcastDownloadLookupRepository lookup ||
            store is not IPodcastDownloadIdentityStore identity || store is not IPodcastDownloadDeletionStore comparison)
            return null;
        StoredPodcastEpisode? episode = await lookup.FindDownloadedEpisodeAsync(file, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return episode is { LocalPath: not null, DownloadedAt: not null, DownloadedBytes: not null } &&
            comparison.IsSameFile(episode.LocalPath, file) && identity.OwnsDownload(episode.SubscriptionId, episode.EpisodeId, episode.LocalPath)
            ? episode : null;
    }

    public async Task PlayAsync(PodcastSubscription subscription, StoredPodcastEpisode episode, IPodcastPlaybackHandoff handoff,
        bool resume, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = EpisodeLock(episode.SubscriptionId, episode.EpisodeId);
        var key = (episode.SubscriptionId, episode.EpisodeId);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(handoff);
        if (ResolvePlayableFile(episode) is null)
            throw new FileNotFoundException("The downloaded file is missing. Download the episode again.");
        PodcastPlaybackRequest request;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredPodcastEpisode current = (await repository.ListEpisodesAsync(episode.SubscriptionId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.EpisodeId == episode.EpisodeId)
                ?? throw new InvalidOperationException("The episode no longer exists.");
            if (current.LocalPath != episode.LocalPath || current.DownloadedAt != episode.DownloadedAt || current.DownloadedBytes != episode.DownloadedBytes)
                throw new InvalidOperationException("The downloaded episode changed. Select the episode again.");
            request = await PreparePlaybackAsync(subscription, current, handoff, resume, cancellationToken).ConfigureAwait(false);
            pendingPlaybacks.AddOrUpdate(key, 1, (_, count) => count + 1);
        }
        finally { gate.Release(); }
        try
        {
            // No gate across the callback: the shared player may flush this same episode's outgoing progress first.
            cancellationToken.ThrowIfCancellationRequested();
            await handoff.PlayAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { pendingPlaybacks.AddOrUpdate(key, 0, (_, count) => count - 1); }
    }

    public async Task<StoredPodcastEpisode> SetRatingAsync(StoredPodcastEpisode episode, int rating,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        if (rating is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(rating));
        if (ratings is null) throw new InvalidOperationException("Episode ratings are unavailable.");
        if (!await ratings.SetEpisodeRatingAsync(episode.SubscriptionId, episode.EpisodeId, rating, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The episode no longer exists.");
        return episode with { Rating = rating };
    }

    public async Task<StoredPodcastEpisode> DownloadAsync(Guid subscriptionId, string episodeId,
        IProgress<PodcastDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsDownloading(subscriptionId, episodeId)) throw new InvalidOperationException("This episode is already downloading.");
        SemaphoreSlim gate = EpisodeLock(subscriptionId, episodeId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await DownloadCoreAsync(subscriptionId, episodeId, progress, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<StoredPodcastEpisode> SavePositionAsync(StoredPodcastEpisode episode, IPodcastPositionSource source,
        CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = EpisodeLock(episode.SubscriptionId, episode.EpisodeId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await SavePositionCoreAsync(episode, source, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<StoredPodcastEpisode> SaveAutomaticPositionAsync(StoredPodcastEpisode episode,
        TimeSpan position, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        if (repository is not IPodcastAutomaticPositionRepository automatic)
            throw new InvalidOperationException("Automatic episode position saving is unavailable.");
        if (duration <= TimeSpan.Zero) throw new InvalidDataException("The episode has no readable duration; the previous position was kept.");
        duration = duration > PodcastPlaybackProgress.MaximumDuration ? PodcastPlaybackProgress.MaximumDuration : duration;
        position = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
        SemaphoreSlim gate = EpisodeLock(episode.SubscriptionId, episode.EpisodeId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ResolvePlayableFile(episode) is null) throw new FileNotFoundException("The downloaded episode is missing or unsafe.");
            PodcastPlaybackProgress saved = new(position, duration, time.GetUtcNow());
            if (!await automatic.SaveAutomaticEpisodePositionAsync(episode, saved, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The episode or its download changed; the previous position was kept.");
            return episode with { SavedProgress = saved };
        }
        finally { gate.Release(); }
    }

    public async Task<StoredPodcastEpisode> DeleteDownloadAsync(StoredPodcastEpisode episode, IPodcastPositionSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode); ArgumentNullException.ThrowIfNull(source);
        if (store is not IPodcastDownloadDeletionStore deletionStore || repository is not IPodcastDownloadRepository downloads)
            throw new InvalidOperationException("Deleting downloaded episodes is unavailable.");
        SemaphoreSlim gate = EpisodeLock(episode.SubscriptionId, episode.EpisodeId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredPodcastEpisode current = (await repository.ListEpisodesAsync(episode.SubscriptionId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.EpisodeId == episode.EpisodeId)
                ?? throw new InvalidOperationException("The episode no longer exists.");
            if (current.LocalPath != episode.LocalPath || current.DownloadedAt != episode.DownloadedAt || current.DownloadedBytes != episode.DownloadedBytes)
                throw new InvalidOperationException("The download changed. Select the episode again before deleting it.");
            if (pendingPlaybacks.TryGetValue((episode.SubscriptionId, episode.EpisodeId), out int pending) && pending > 0)
                throw new InvalidOperationException("This episode is starting playback. Stop it before deleting its download.");
            if (current.LocalPath is null) return current;
            PodcastPlaybackSnapshot? snapshot = await source.GetPodcastPlaybackAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot is { CanSavePosition: true } && deletionStore.IsSameFile(current.LocalPath, snapshot.File))
                throw new InvalidOperationException("Stop this episode in the main player before deleting its download.");
            using IPodcastDownloadDeletion staged = deletionStore.StageDelete(current.SubscriptionId, current.EpisodeId, current.LocalPath);
            cancellationToken.ThrowIfCancellationRequested();
            // Once the DB commits, finish the accepted deletion even if the caller subsequently cancels.
            if (!await downloads.ClearOwnedDownloadAsync(current, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The download changed; the file was kept.");
            staged.Complete();
            return current with { LocalPath = null, DownloadedAt = null, DownloadedBytes = null, SavedProgress = null };
        }
        finally { gate.Release(); }
    }

    private SemaphoreSlim EpisodeLock(Guid subscriptionId, string episodeId) =>
        episodeLocks.GetOrAdd((subscriptionId, episodeId), _ => new SemaphoreSlim(1, 1));
}
