using Aibell.Domain.Media;

namespace Aibell.Application.Playback;

public enum PlaybackStatus
{
    Stopped,
    Playing,
    Paused,
    Ended,
    Faulted
}

public sealed record PlaybackSnapshot(
    PlaybackStatus Status,
    Track? Track,
    TimeSpan Position,
    double Volume,
    bool IsMuted = false,
    string? Error = null,
    string? ErrorDetails = null);

public interface IPlaybackEngine
{
    PlaybackSnapshot Snapshot { get; }

    event EventHandler<PlaybackSnapshot>? StateChanged;

    Task LoadAsync(Track track, CancellationToken cancellationToken = default);

    Task PlayAsync(CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default);

    Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default);
}
