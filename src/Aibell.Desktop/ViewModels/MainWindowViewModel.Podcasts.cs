using Aibell.Desktop.Localization;
using Avalonia.Threading;
using Aibell.Application.Podcasts;
using Aibell.Domain.Media;
using Aibell.Application.Playback;
using Aibell.Desktop.Services;
using Aibell.Domain.Podcasts;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : IPodcastPlaybackHandoff, IPodcastPositionSource, IPodcastProgressNotifications, IPodcastPlaybackControl, IPodcastCatalogNotifications
{
    public event EventHandler? PodcastPlayingChanged;

    public Uri? PodcastPlayingFile => !IsRadioPlayback && playbackEngine.Snapshot is { Status: PlaybackStatus.Playing, Track.Location.IsFile: true } playing
        ? playing.Track.Location : null;

    public Task PausePodcastAsync(Uri file, CancellationToken cancellationToken = default) => RunPlaybackOperation(async () =>
    {
        if (PodcastPlayingFile == file) await playbackEngine.PauseAsync(cancellationToken);
    });

    private bool podcastAutoRefresh = true;
    private PodcastAutoRefresher? podcastRefresher;

    public event EventHandler<PodcastRefreshAllResult>? PodcastCatalogRefreshed;

    /// <summary>Starts the hourly check for new episodes. Called once by the composition root.</summary>
    public void ConfigurePodcastAutoRefresh(PodcastService service, TimeSpan? initialDelay = null, TimeSpan? interval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (podcastRefresher is not null) throw new InvalidOperationException("Podcast auto-refresh is already configured.");
        podcastRefresher = new PodcastAutoRefresher(token => service.RefreshAllAsync(null, token), () => podcastAutoRefresh && !playbackShutdown,
            initialDelay, interval, delay);
        podcastRefresher.Refreshed += (_, result) =>
        {
            PodcastCatalogRefreshed?.Invoke(this, result);
            if (result.NewEpisodes > 0)
                Dispatcher.UIThread.Post(() =>
                {
                    if (!playbackShutdown) StatusMessage = result.NewEpisodes == 1 ? "1 new podcast episode." : $"{result.NewEpisodes} new podcast episodes.";
                });
        };
        podcastRefresher.Start();
    }

    public Task ShutdownPodcastAutoRefreshAsync() => podcastRefresher?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    private double podcastRate = 1.0;
    private bool podcastInputActive;

    public double PodcastPlaybackRate => podcastRate;

    public async Task SetPodcastPlaybackRateAsync(double rate, CancellationToken cancellationToken = default)
    {
        if (rate is not (1.0 or 1.25 or 1.5 or 2.0)) throw new ArgumentOutOfRangeException(nameof(rate), "Choose 1x, 1.25x, 1.5x or 2x.");
        podcastRate = rate;
        if (podcastInputActive && playbackEngine.Snapshot.Track is not null && playbackEngine is IPlaybackRateControl control)
            await control.SetRateAsync(rate, cancellationToken);
        await PersistPreferencesAsync(cancellationToken);
    }

    // Only owned podcast episodes use the chosen speed; every other input starts at normal speed.
    private Task ApplyInputRateAsync(bool podcast, CancellationToken cancellationToken)
    {
        podcastInputActive = podcast;
        return playbackEngine is IPlaybackRateControl control
            ? control.SetRateAsync(podcast ? podcastRate : 1.0, cancellationToken) : Task.CompletedTask;
    }

    private PodcastProgressCoordinator? podcastProgress;
    private PodcastService? podcastProgressService;

    public event EventHandler<StoredPodcastEpisode>? PositionSaved;

    public void ConfigurePodcastProgress(PodcastService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (podcastProgress is not null) throw new InvalidOperationException("Podcast progress is already configured.");
        podcastProgressService = service;
        podcastProgress = new PodcastProgressCoordinator(service);
        podcastProgress.PositionSaved += (_, episode) => PositionSaved?.Invoke(this, episode);
        podcastProgress.SaveFailed += (_, exception) => Dispatcher.UIThread.Post(() =>
        {
            if (!playbackShutdown) StatusMessage = Loc.Format("Podcasts.ProgressSaveFailed", exception.Message);
        });
    }

    public async Task PreparePodcastInputAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (podcastProgressService is null || podcastProgress is null) { await ApplyInputRateAsync(false, cancellationToken); return; }
        cancellationToken.ThrowIfCancellationRequested();
        long stopGeneration = playbackStopGeneration;
        StoredPodcastEpisode? episode = await podcastProgressService.FindDownloadedEpisodeAsync(track.Location, cancellationToken);
        ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
        // The outgoing input can advance while the cache lookup is pending.
        await FlushPodcastProgressAsync(cancellationToken);
        ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
        if (episode is null) podcastProgress.Forget(track.Location);
        else podcastProgress.Register(episode, track.Location);
        await ApplyInputRateAsync(episode is not null, cancellationToken);
    }

    private void ObservePodcastProgress(PlaybackSnapshot snapshot) => podcastProgress?.Observe(snapshot);

    public Task FlushPodcastProgressAsync(CancellationToken cancellationToken = default) =>
        podcastProgress?.FlushAsync(playbackEngine.Snapshot, cancellationToken) ?? Task.CompletedTask;

    public Task ShutdownPodcastProgressAsync() =>
        podcastProgress?.ShutdownAsync(playbackEngine.Snapshot) ?? Task.CompletedTask;

    // PodcastService resumes off the UI thread. Serialize this handoff with the
    // desktop lifecycle before touching the queue or starting the shared engine.
    public async Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.File.IsAbsoluteUri || !request.File.IsFile)
            throw new ArgumentException("Only downloaded local podcast episodes can be played.", nameof(request));
        await Dispatcher.UIThread.InvokeAsync(() => RunPlaybackOperation(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (playbackShutdown) return;
            long stopGeneration = playbackStopGeneration;
            Track read = await importService.ReadTrackAsync(request.File, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (playbackShutdown) return;
            ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            if (changingTrack) throw new InvalidOperationException("Wait for the current playback transition to finish.");
            Track episode = read with
            {
                Title = string.IsNullOrWhiteSpace(request.Title) ? read.Title : request.Title,
                Album = request.Show,
                ShowName = request.Show
            };
            if (request.Episode is { } stored) podcastProgress?.Register(stored, request.File);
            QueueTracks([episode]);
            Guid entryId = playQueue.Items[^1].Id;
            TimeSpan start = request.StartPosition > TimeSpan.Zero && request.StartPosition < episode.Duration
                ? request.StartPosition : TimeSpan.Zero;
            await PlayQueueEntryCoreAsync(entryId, cancellationToken, start, propagateFailure: true);
        }));
    }
    public async ValueTask<PodcastPlaybackSnapshot?> GetPodcastPlaybackAsync(CancellationToken cancellationToken = default) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (playbackShutdown || IsRadioPlayback) return null;
            PlaybackSnapshot snapshot = playbackEngine.Snapshot;
            return snapshot.Track is { Location.IsFile: true } track
                ? new PodcastPlaybackSnapshot(track.Location, snapshot.Position,
                    snapshot.Status is PlaybackStatus.Playing or PlaybackStatus.Paused) : null;
        });
}
