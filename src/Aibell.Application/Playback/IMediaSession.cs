using Aibell.Domain.Media;
using Aibell.Domain.Playback;

namespace Aibell.Application.Playback;

public sealed record MediaSessionState(PlaybackSnapshot Playback, TrackMetadata? Metadata, Uri? Artwork,
    bool CanPlay, bool CanPause, bool CanSeek, bool CanGoNext, bool CanGoPrevious,
    bool Shuffle, RepeatMode Repeat);

public enum MediaSessionAction
{
    Play, Pause, PlayPause, Stop, Next, Previous, Seek, SetPosition, SetVolume, SetShuffle, SetRepeat, Raise
}

// Seek values are microseconds. TrackId makes absolute seeks safe against stale clients.
public sealed record MediaSessionCommand(MediaSessionAction Action, long Position = 0, Guid? TrackId = null,
    double Volume = 0, bool Shuffle = false, RepeatMode Repeat = RepeatMode.Off);

public interface IMediaSessionController
{
    MediaSessionState State { get; }
    event EventHandler? StateChanged;
    event EventHandler<TimeSpan>? Seeked;
    Task ExecuteAsync(MediaSessionCommand command, CancellationToken cancellationToken = default);
}

public interface IMediaSession : IAsyncDisposable
{
    Task StartAsync(IMediaSessionController controller, CancellationToken cancellationToken = default);
}
