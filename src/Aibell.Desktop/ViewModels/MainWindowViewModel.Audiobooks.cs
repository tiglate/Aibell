using Avalonia.Threading;
using Aibell.Application.Audiobooks;
using Aibell.Application.Playback;
using Aibell.Domain.Audiobooks;
using Aibell.Domain.Media;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : IAudiobookPlaybackHandoff
{
    public async Task PlayAsync(AudiobookPlaybackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AudiobookLocation.IsSupported(request.File))
            throw new ArgumentException("Choose a supported local audiobook file.", nameof(request));
        await Dispatcher.UIThread.InvokeAsync(() => RunPlaybackOperation(async () =>
        {
            long stopGeneration = playbackStopGeneration;
            ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            if (changingTrack) throw new InvalidOperationException("Wait for the current playback transition to finish.");
            Track read = await importService.ReadTrackAsync(request.File, cancellationToken);
            ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            if (changingTrack) throw new InvalidOperationException("Wait for the current playback transition to finish.");
            TimeSpan duration = ResumeRules.BoundDuration(read.Duration);
            TimeSpan position = ResumeRules.ResumeStart(request.StartPosition, duration);
            Track book = read with { Title = request.Title, Artist = request.Author, Duration = duration };
            QueueTracks([book]);
            await PlayQueueEntryCoreAsync(playQueue.Items[^1].Id, cancellationToken, position, propagateFailure: true);
        }));
    }

    public async ValueTask<AudiobookPlaybackSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (playbackShutdown || IsRadioPlayback) return null;
            PlaybackSnapshot current = playbackEngine.Snapshot;
            return current.Track is { Location.IsFile: true } track
                ? new AudiobookPlaybackSnapshot(track.Location, current.Position,
                    current.Status is PlaybackStatus.Playing or PlaybackStatus.Paused) : null;
        });

    private void ThrowIfPlaybackAbandoned(CancellationToken cancellationToken, long? stopGeneration = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (playbackShutdown || stopGeneration is { } generation && generation != playbackStopGeneration)
            throw new OperationCanceledException("Playback was stopped or the player is shutting down.", cancellationToken);
    }
}
