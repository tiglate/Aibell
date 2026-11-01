using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Aibell.Domain.Podcasts;
using Aibell.Application.Library;

namespace Aibell.Application.Podcasts;

public sealed record PodcastSubscribeResult(PodcastSubscription Subscription, bool Added, int EpisodeCount,
    IReadOnlyList<PodcastFeedIssue> Issues);

public sealed record PodcastRefreshAllProgress(int Position, int Total, string Title);
public sealed record PodcastRefreshFailure(string Title, string Message);
public sealed record PodcastRefreshAllResult(int Subscriptions, int Refreshed, int NewEpisodes, IReadOnlyList<PodcastRefreshFailure> Failures);

public sealed record PodcastRefreshResult(PodcastSubscription Subscription, bool NotModified, int NewEpisodes,
    int EpisodeCount, IReadOnlyList<PodcastFeedIssue> Issues);

/// <summary>
/// Persisted subscriptions over the feed/download ports. Nothing here runs on its own: every network call is the
/// direct result of Subscribe, Refresh or Download. Download destinations are chosen here, never by remote metadata,
/// and removing a subscription never deletes files.
/// </summary>
public sealed partial class PodcastService
{
    public const int MaximumEpisodes = 5000;
    private static readonly string[] AudioExtensions = [".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wav"];
    private static readonly FrozenDictionary<string, string> MimeExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mp4"] = ".m4a",
        ["audio/x-m4a"] = ".m4a",
        ["audio/m4a"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["audio/ogg"] = ".ogg",
        ["audio/opus"] = ".opus",
        ["audio/flac"] = ".flac",
        ["audio/x-flac"] = ".flac",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    private readonly IPodcastRepository repository;
    private readonly IPodcastPositionRepository? positions;
    private readonly IMediaMetadataReader? metadata;
    private readonly IPodcastFeedReader reader;
    private readonly IPodcastEpisodeDownloader downloader;
    private readonly IPodcastDownloadStore store;
    private readonly TimeProvider time;
    private readonly long maximumDownloadBytes;
    private readonly ConcurrentDictionary<(Guid, string), CancellationTokenSource> activeDownloads = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> feedLocks = new();

    public PodcastService(IPodcastRepository repository, IPodcastFeedReader reader, IPodcastEpisodeDownloader downloader,
        IPodcastDownloadStore store, TimeProvider? time = null, long maximumDownloadBytes = 1024L * 1024 * 1024,
        IPodcastPositionRepository? positions = null, IMediaMetadataReader? metadata = null,
        IPodcastRatingRepository? ratings = null)
    {
        ArgumentNullException.ThrowIfNull(repository); ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(downloader); ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDownloadBytes);
        this.positions = positions; this.metadata = metadata; this.ratings = ratings ?? repository as IPodcastRatingRepository;
        this.repository = repository; this.reader = reader; this.downloader = downloader;
        this.store = store; this.time = time ?? TimeProvider.System;
        this.maximumDownloadBytes = maximumDownloadBytes;
    }

    public Task<IReadOnlyList<PodcastSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken = default) =>
        repository.ListSubscriptionsAsync(cancellationToken);
    public Task<IReadOnlyList<StoredPodcastEpisode>> ListEpisodesAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
        repository.ListEpisodesAsync(subscriptionId, cancellationToken);
    public bool SupportsPositions => positions is not null && metadata is not null;
    public bool IsDownloading(Guid subscriptionId, string episodeId) => activeDownloads.ContainsKey((subscriptionId, episodeId));

    public async Task<PodcastSubscribeResult> SubscribeAsync(string? feedUrl, CancellationToken cancellationToken = default)
    {
        Uri url = PodcastSubscription.NormalizeFeedUrl(feedUrl)
            ?? throw new ArgumentException("Use a complete HTTP or HTTPS feed URL without embedded credentials or a fragment.");
        PodcastSubscription? existing = (await repository.ListSubscriptionsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.FeedUrl == url);
        if (existing is not null) return new(existing, false, 0, []);

        PodcastFeedResult result = await reader.ReadAsync(new PodcastFeedRequest(url), cancellationToken).ConfigureAwait(false);
        if (result.Feed is not { } feed) throw new InvalidOperationException("The server did not return a feed.");
        Guid id = Guid.NewGuid(); DateTimeOffset now = time.GetUtcNow();
        PodcastSubscription subscription = Describe(new(id, url, "-", "", now), feed, result, now);
        List<StoredPodcastEpisode> episodes = Map(id, feed);
        subscription.Validate();
        if (!await repository.AddSubscriptionAsync(subscription, episodes, cancellationToken).ConfigureAwait(false))
            return new((await repository.ListSubscriptionsAsync(cancellationToken).ConfigureAwait(false)).First(item => item.FeedUrl == url),
                false, 0, []);
        return new(subscription, true, episodes.Count, result.Issues);
    }

    public async Task<PodcastRefreshResult> RefreshAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = feedLocks.GetOrAdd(subscriptionId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("This podcast is already refreshing.");
        try
        {
            PodcastSubscription subscription = (await repository.ListSubscriptionsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.Id == subscriptionId)
                ?? throw new InvalidOperationException("The subscription no longer exists.");
            int before = (await repository.ListEpisodesAsync(subscriptionId, cancellationToken).ConfigureAwait(false)).Count;
            PodcastFeedResult result = await reader.ReadAsync(
                new PodcastFeedRequest(subscription.FeedUrl, subscription.EntityTag, subscription.LastModified), cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset now = time.GetUtcNow();
            if (result.NotModified)
            {
                PodcastSubscription touched = subscription with { LastRefreshed = now };
                if (!await repository.ApplyRefreshAsync(touched, null, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The subscription no longer exists.");
                return new(touched, true, 0, before, []);
            }
            if (result.Feed is not { } feed) throw new InvalidOperationException("The server did not return a feed.");
            PodcastSubscription updated = Describe(subscription, feed, result, now);
            updated.Validate();
            List<StoredPodcastEpisode> episodes = Map(subscriptionId, feed);
            if (!await repository.ApplyRefreshAsync(updated, episodes, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The subscription no longer exists.");
            int total = (await repository.ListEpisodesAsync(subscriptionId, cancellationToken).ConfigureAwait(false)).Count;
            return new(updated, false, total - before, total, result.Issues);
        }
        finally { gate.Release(); }
    }

    /// <summary>Forgets the subscription and its cached episodes. Returns how many downloaded files were left on disk.</summary>
    public async Task<int> RemoveAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        foreach (var (key, source) in activeDownloads.ToArray())
            if (key.Item1 == subscriptionId) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
        int kept = (await repository.ListEpisodesAsync(subscriptionId, cancellationToken).ConfigureAwait(false))
            .Count(episode => episode.LocalPath is not null);
        if (!await repository.RemoveSubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The subscription no longer exists.");
        return kept;
    }

    private async Task<StoredPodcastEpisode> DownloadCoreAsync(Guid subscriptionId, string episodeId,
        IProgress<PodcastDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        StoredPodcastEpisode episode = (await repository.ListEpisodesAsync(subscriptionId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.EpisodeId == episodeId)
            ?? throw new InvalidOperationException("The episode is no longer cached. Refresh the podcast.");
        if (store.ResolveExisting(episode.LocalPath) is not null) return episode;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!activeDownloads.TryAdd((subscriptionId, episodeId), linked))
            throw new InvalidOperationException("This episode is already downloading.");
        try
        {
            // A stored path that is missing or not safely inside the podcast folder is not "downloaded": forget it, keep the file.
            if (episode.LocalPath is not null)
                await repository.ClearDownloadAsync(subscriptionId, episodeId, CancellationToken.None).ConfigureAwait(false);
            Uri destination = store.PrepareDestination(subscriptionId, FileNameFor(episode));
            PodcastDownloadResult downloaded = await downloader.DownloadAsync(
                new PodcastDownloadRequest(episode.Enclosure, destination, maximumDownloadBytes, Progress: progress), linked.Token).ConfigureAwait(false);
            DateTimeOffset now = time.GetUtcNow();
            // The finished file is never deleted here: another process may have replaced it and silent deletion is unsafe.
            // A failed record leaves it in place with an actionable error.
            bool recorded;
            try { recorded = await repository.RecordDownloadAsync(subscriptionId, episodeId, destination.LocalPath, downloaded.BytesWritten, now, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception)
            { throw new IOException($"The episode was downloaded but could not be recorded. The file was kept at {destination.LocalPath}. {exception.Message}", exception); }
            if (!recorded)
                throw new InvalidOperationException($"The subscription was removed while the episode downloaded. The file was kept at {destination.LocalPath}.");
            // Refresh the whole row: recording invalidates previous progress, while rating/feed changes can occur during I/O.
            return (await repository.ListEpisodesAsync(subscriptionId, CancellationToken.None).ConfigureAwait(false))
                .FirstOrDefault(item => item.EpisodeId == episodeId)
                ?? throw new InvalidOperationException($"The subscription was removed after the episode downloaded. The file was kept at {destination.LocalPath}.");
        }
        finally { activeDownloads.TryRemove((subscriptionId, episodeId), out _); }
    }

    public void CancelDownload(Guid subscriptionId, string episodeId)
    {
        if (activeDownloads.TryGetValue((subscriptionId, episodeId), out CancellationTokenSource? source))
            try { source.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Returns the file to play, or null when it is missing or outside the application's podcast folder.</summary>
    public Uri? ResolvePlayableFile(StoredPodcastEpisode episode) => store.ResolveExisting(episode.LocalPath);

    public Task PlayAsync(PodcastSubscription subscription, StoredPodcastEpisode episode, IPodcastPlaybackHandoff handoff,
        CancellationToken cancellationToken = default) => PlayAsync(subscription, episode, handoff, false, cancellationToken);

    private async Task<PodcastPlaybackRequest> PreparePlaybackAsync(PodcastSubscription subscription, StoredPodcastEpisode episode, IPodcastPlaybackHandoff handoff,
        bool resume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(handoff);
        Uri file = ResolvePlayableFile(episode) ?? throw new FileNotFoundException("The downloaded file is missing. Download the episode again.");
        TimeSpan start = TimeSpan.Zero;
        if (resume && episode.SavedProgress is { } progress)
        {
            if (metadata is null) throw new InvalidOperationException("Position resume is unavailable.");
            var current = await metadata.ReadAsync(file, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            start = progress.ResumeStart(current.Duration);
        }
        return new PodcastPlaybackRequest(file, episode.Title, subscription.Title, episode.Duration, start, episode);
    }

    private async Task<StoredPodcastEpisode> SavePositionCoreAsync(StoredPodcastEpisode episode, IPodcastPositionSource source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        if (positions is null || metadata is null) throw new InvalidOperationException("Position saving is unavailable.");
        Uri file = ResolvePlayableFile(episode) ?? throw new FileNotFoundException("The downloaded file is missing. Download the episode again.");
        PodcastPlaybackSnapshot? snapshot = await source.GetPodcastPlaybackAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is not { CanSavePosition: true } || snapshot.File != file)
            throw new InvalidOperationException("Play or pause this downloaded episode in the main player before saving its position.");
        var current = await metadata.ReadAsync(file, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        PodcastPlaybackSnapshot? latest = await source.GetPodcastPlaybackAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (latest is not { CanSavePosition: true } || latest.File != file || ResolvePlayableFile(episode) != file)
            throw new InvalidOperationException("The player or downloaded file changed; the previous position was kept.");
        TimeSpan duration = current.Duration > PodcastPlaybackProgress.MaximumDuration ? PodcastPlaybackProgress.MaximumDuration : current.Duration;
        if (duration <= TimeSpan.Zero) throw new InvalidDataException("The downloaded episode has no readable duration; the previous position was kept.");
        TimeSpan position = snapshot.Position < TimeSpan.Zero ? TimeSpan.Zero : snapshot.Position > duration ? duration : snapshot.Position;
        PodcastPlaybackProgress saved = new(position, duration, time.GetUtcNow());
        if (!await positions.SaveEpisodePositionAsync(episode.SubscriptionId, episode.EpisodeId, file.LocalPath, saved, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The episode or its download changed; the previous position was kept.");
        return episode with { SavedProgress = saved };
    }

    private static string FileNameFor(StoredPodcastEpisode episode)
    {
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(episode.EpisodeId)))[..16].ToLowerInvariant();
        string extension = Path.GetExtension(episode.Enclosure.AbsolutePath).ToLowerInvariant();
        if (!AudioExtensions.Contains(extension))
        {
            extension = MediaTypeHeaderValue.TryParse(episode.MediaType, out MediaTypeHeaderValue? parsed)
                && parsed.MediaType is { } mediaType && MimeExtensions.TryGetValue(mediaType, out string? mapped)
                ? mapped : ".mp3";
        }
        return name + extension;
    }

    private static PodcastSubscription Describe(PodcastSubscription basis, PodcastFeed feed, PodcastFeedResult result, DateTimeOffset now) =>
        basis with
        {
            Title = Clean(feed.Title, PodcastSubscription.MaximumTitleLength, "Untitled podcast"),
            Description = Clean(feed.Description, PodcastSubscription.MaximumDescriptionLength, ""),
            EntityTag = result.EntityTag,
            LastModified = result.LastModified,
            LastRefreshed = now
        };

    private static List<StoredPodcastEpisode> Map(Guid subscriptionId, PodcastFeed feed)
    {
        HashSet<string> seen = new(StringComparer.Ordinal); List<StoredPodcastEpisode> episodes = [];
        foreach (PodcastEpisode episode in feed.Episodes)
        {
            if (episodes.Count >= MaximumEpisodes) break;
            if (string.IsNullOrWhiteSpace(episode.Id) || episode.Id.Length > 4096 || !PodcastLocation.IsSupported(episode.Enclosure) ||
                !seen.Add(episode.Id)) continue;
            episodes.Add(new(subscriptionId, episode.Id, Clean(episode.Title, 500, "Untitled episode"), episode.Enclosure, episode.MediaType,
                episode.Length is >= 0 ? episode.Length : null, episode.Published,
                episode.Duration is { } duration && duration >= TimeSpan.Zero ? duration : null,
                Clean(episode.Description, PodcastSubscription.MaximumDescriptionLength, ""),
                ImageUrl: PodcastLocation.IsSupported(episode.Image) ? episode.Image : null));
        }
        return episodes;
    }

    private static string Clean(string? value, int maximum, string fallback)
    {
        string text = new((value ?? "").Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
        text = text.Trim();
        if (text.Length == 0) return fallback;
        return text.Length <= maximum ? text : text[..maximum];
    }
}
