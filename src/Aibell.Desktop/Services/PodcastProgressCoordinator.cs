using Aibell.Application.Playback;
using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;

namespace Aibell.Desktop.Services;

public interface IPodcastProgressNotifications
{
    event EventHandler<StoredPodcastEpisode>? PositionSaved;
}

/// <summary>Application-lifetime, single-writer progress capture for known downloaded episodes.</summary>
public sealed class PodcastProgressCoordinator : IPodcastProgressNotifications, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, StoredPodcastEpisode> knownEpisodes = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Func<StoredPodcastEpisode, TimeSpan, TimeSpan, CancellationToken, Task<StoredPodcastEpisode>> save;
    private readonly CancellationTokenSource timerCancellation = new();
    private readonly Task timerTask;
    private Task writeTail = Task.CompletedTask;
    private Capture? active;
    private Capture? queued;
    private bool accepting = true;
    private Task? shutdown;
    private bool disposed;
    private Exception? lastError;

    public PodcastProgressCoordinator(PodcastService service, TimeProvider? timeProvider = null)
        : this(service.SaveAutomaticPositionAsync, timeProvider) { }

    // Explicit ticks allow deterministic tests without waiting for the wall clock.
    public PodcastProgressCoordinator(
        Func<StoredPodcastEpisode, TimeSpan, TimeSpan, CancellationToken, Task<StoredPodcastEpisode>> save,
        TimeProvider? timeProvider = null, bool startTimer = true)
    {
        this.save = save ?? throw new ArgumentNullException(nameof(save));
        timerTask = startTimer ? RunTimerAsync(timeProvider ?? TimeProvider.System) : Task.CompletedTask;
    }

    public event EventHandler<StoredPodcastEpisode>? PositionSaved;
    public event EventHandler<Exception>? SaveFailed;
    public Exception? LastError { get { lock (gate) return lastError; } }

    public void Register(StoredPodcastEpisode episode, Uri file)
    {
        ArgumentNullException.ThrowIfNull(episode);
        string key = FileKey(file) ?? throw new ArgumentException("Only local downloads can be tracked.", nameof(file));
        if (episode.LocalPath is null) throw new ArgumentException("The episode is not downloaded.", nameof(episode));
        if (!knownEpisodes.Comparer.Equals(key, Path.GetFullPath(episode.LocalPath)))
            throw new ArgumentException("The playback file does not match the episode download.", nameof(file));
        lock (gate)
        {
            if (!accepting) return;
            knownEpisodes[key] = episode;
        }
    }

    public void Forget(Uri file)
    {
        string? key = FileKey(file);
        if (key is null) return;
        lock (gate) knownEpisodes.Remove(key);
    }

    /// <summary>Called synchronously for every engine event, before UI event coalescing.</summary>
    public void Observe(PlaybackSnapshot snapshot)
    {
        lock (gate)
        {
            if (!accepting) return;
            string? key = FileKey(snapshot.Track?.Location);
            if (snapshot.Status is PlaybackStatus.Stopped or PlaybackStatus.Faulted || key is null
                || !knownEpisodes.TryGetValue(key, out StoredPodcastEpisode? episode))
            {
                EnqueueLocked();
                active = null;
                return;
            }
            TimeSpan duration = snapshot.Track!.Duration;
            if (duration <= TimeSpan.Zero || duration > PodcastPlaybackProgress.MaximumDuration)
            {
                EnqueueLocked();
                active = null;
                return;
            }
            if (active is { } previous)
            {
                if (!knownEpisodes.Comparer.Equals(previous.File, key)) EnqueueLocked();
                else episode = previous.Episode; // Registering a replacement must not rebind the outgoing input.
            }
            TimeSpan position = snapshot.Status == PlaybackStatus.Ended ? duration
                : TimeSpan.FromTicks(Math.Clamp(snapshot.Position.Ticks, 0, duration.Ticks));
            active = new Capture(episode, key, position, duration);
            if (snapshot.Status is PlaybackStatus.Paused or PlaybackStatus.Ended)
            {
                EnqueueLocked();
                if (snapshot.Status == PlaybackStatus.Ended) active = null;
            }
        }
    }

    public Task TickAsync()
    {
        lock (gate)
        {
            if (accepting) EnqueueLocked();
            return writeTail;
        }
    }

    /// <summary>Capture outgoing input before Stop/Load can replace its track or zero its position.</summary>
    public Task FlushAsync(PlaybackSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Observe(snapshot);
        // Accepted writes deliberately outlive caller cancellation and must drain before mutation.
        return TickAsync();
    }

    public Task ShutdownAsync(PlaybackSnapshot snapshot)
    {
        lock (gate)
        {
            if (shutdown is not null) return shutdown;
            Observe(snapshot);
            EnqueueLocked();
            accepting = false;
            timerCancellation.Cancel();
            shutdown = DrainAsync(writeTail);
            return shutdown;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task drain;
        lock (gate)
        {
            if (shutdown is null)
            {
                EnqueueLocked();
                accepting = false;
                timerCancellation.Cancel();
                shutdown = DrainAsync(writeTail);
            }
            drain = shutdown;
        }
        await drain.ConfigureAwait(false);
    }

    private async Task DrainAsync(Task writes)
    {
        await timerTask.ConfigureAwait(false);
        await writes.ConfigureAwait(false);
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timerCancellation.Dispose();
        }
    }

    private async Task RunTimerAsync(TimeProvider timeProvider)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(timerCancellation.Token).ConfigureAwait(false))
                await TickAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timerCancellation.IsCancellationRequested) { }
    }

    private void EnqueueLocked()
    {
        if (active is not { } capture || capture == queued) return;
        queued = capture;
        Task previous = writeTail;
        writeTail = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                StoredPodcastEpisode saved = await save(capture.Episode, capture.Position, capture.Duration,
                    CancellationToken.None).ConfigureAwait(false);
                lock (gate) lastError = null;
                try { PositionSaved?.Invoke(this, saved); }
                catch (Exception) { /* A consumer cannot turn a committed save into a failed write. */ }
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    lastError = exception;
                    // Retry an unchanged position on a later tick; never erase a newer queued capture.
                    if (queued == capture) queued = null;
                }
                try { SaveFailed?.Invoke(this, exception); }
                catch (Exception) { /* A notification cannot break shutdown or the serial writer. */ }
            }
        });
    }

    private static string? FileKey(Uri? file) => file is { IsAbsoluteUri: true, IsFile: true }
        ? Path.GetFullPath(file.LocalPath) : null;

    private sealed record Capture(StoredPodcastEpisode Episode, string File, TimeSpan Position, TimeSpan Duration);
}
