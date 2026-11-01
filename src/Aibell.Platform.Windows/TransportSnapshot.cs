using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;

namespace Aibell.Platform.Windows;

// Values match Windows.Media.MediaPlaybackStatus.
internal enum TransportPlayback { Closed = 0, Changing = 1, Stopped = 2, Playing = 3, Paused = 4 }

// Values match Windows.Media.SystemMediaTransportControlsButton.
internal enum TransportButton { Play = 0, Pause = 1, Stop = 2, Record = 3, FastForward = 4, Rewind = 5, Next = 6, Previous = 7, ChannelUp = 8, ChannelDown = 9 }

internal sealed record TransportSnapshot(TransportPlayback Playback, bool PlayEnabled, bool PauseEnabled, bool StopEnabled,
    bool NextEnabled, bool PreviousEnabled, bool HasTrack, string Title, string Artist, string AlbumArtist, string Album,
    string ArtworkPath = "")
{
    public static TransportSnapshot Empty { get; } = new(TransportPlayback.Closed, false, false, false, false, false, false, "", "", "", "");
}

internal static class TransportMapper
{
    public static TransportSnapshot Map(MediaSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Playback.Track is not { } track) return TransportSnapshot.Empty;
        TrackMetadata metadata = state.Metadata ?? TrackMetadata.FromTrack(track);
        TransportPlayback playback = state.Playback.Status switch
        {
            PlaybackStatus.Playing => TransportPlayback.Playing,
            PlaybackStatus.Paused => TransportPlayback.Paused,
            _ => TransportPlayback.Stopped
        };
        return new(playback, state.CanPlay, state.CanPause, state.CanPlay && playback != TransportPlayback.Stopped,
            state.CanGoNext, state.CanGoPrevious, true, metadata.Title, metadata.Artist, metadata.AlbumArtist, metadata.Album,
            ArtworkPathOf(state.Artwork));
    }

    // Only local files are sent to the shell: a UNC path could stall the pump thread on a slow share.
    private static string ArtworkPathOf(Uri? artwork) =>
        artwork is { IsAbsoluteUri: true, IsFile: true, IsUnc: false } ? artwork.LocalPath : "";

    public static MediaSessionCommand? ToCommand(TransportButton button) => button switch
    {
        TransportButton.Play => new(MediaSessionAction.Play),
        TransportButton.Pause => new(MediaSessionAction.Pause),
        TransportButton.Stop => new(MediaSessionAction.Stop),
        TransportButton.Next => new(MediaSessionAction.Next),
        TransportButton.Previous => new(MediaSessionAction.Previous),
        _ => null
    };
}

internal interface IMediaTransport : IAsyncDisposable
{
    event Action<TransportButton>? ButtonPressed;
    Task StartAsync(CancellationToken cancellationToken);
    // Non-blocking and latest-wins: intermediate snapshots may be coalesced.
    void Apply(TransportSnapshot snapshot);
}
