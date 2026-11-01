using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public event EventHandler<TimeSpan>? PlaybackSeeked;

    private async Task SeekPlaybackAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        await playbackEngine.SeekAsync(position, cancellationToken);
        PlaybackSeeked?.Invoke(this, playbackEngine.Snapshot.Position);
    }

    public MediaSessionState GetMediaSessionState()
    {
        PlaybackSnapshot snapshot = playbackEngine.Snapshot;
        TrackMetadata? metadata = snapshot.Track is { } track
            ? editedMetadata.GetValueOrDefault(track.Id) ?? TrackMetadata.FromTrack(refreshedTracks.GetValueOrDefault(track.Id) ?? track) : null;
        bool loaded = snapshot.Track is not null && !playbackShutdown && !changingTrack && snapshot.Status != PlaybackStatus.Faulted;
        // The current engine cannot prepare the next track paused without starting it.
        // Advertise navigation only while playing instead of unexpectedly resuming audio.
        bool navigating = loaded && snapshot.Status == PlaybackStatus.Playing;
        return new(snapshot.Track is null ? snapshot with { Volume = Volume, IsMuted = preferredMuted } : snapshot, metadata, ArtworkLocation,
            !playbackShutdown && !changingTrack && CanPlay, loaded && !IsRadioPlayback, loaded && snapshot.Track!.Duration > TimeSpan.Zero,
            navigating && CanGoNext, navigating && CanGoPrevious, !IsRadioPlayback && playbackQueue.ShuffleEnabled, IsRadioPlayback ? RepeatMode.Off : playbackQueue.RepeatMode);
    }

    public async Task ExecuteMediaSessionCommandAsync(MediaSessionCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (playbackShutdown || changingTrack) return;
        PlaybackSnapshot snapshot = playbackEngine.Snapshot;
        MediaSessionState state = GetMediaSessionState();
        switch (command.Action)
        {
            case MediaSessionAction.Play:
                if (!state.CanPlay || snapshot.Status == PlaybackStatus.Playing) return;
                if (IsRadioPlayback)
                {
                    if (snapshot.Status != PlaybackStatus.Stopped || !radioConnectionRequested)
                        await PlayRadioStationAsync(playingRadioStation!.Id, cancellationToken);
                }
                else if (snapshot.Track is null) await TogglePlaybackAsync(cancellationToken);
                else await playbackEngine.PlayAsync(cancellationToken);
                break;
            case MediaSessionAction.Pause:
                if (snapshot.Status == PlaybackStatus.Playing && state.CanPause) await playbackEngine.PauseAsync(cancellationToken);
                break;
            case MediaSessionAction.PlayPause:
                if (snapshot.Track is null) await TogglePlaybackAsync(cancellationToken);
                else if (snapshot.Status == PlaybackStatus.Playing)
                { if (state.CanPause) await playbackEngine.PauseAsync(cancellationToken); }
                else if (IsRadioPlayback)
                {
                    if (snapshot.Status != PlaybackStatus.Stopped || !radioConnectionRequested)
                        await PlayRadioStationAsync(playingRadioStation!.Id, cancellationToken);
                }
                else await playbackEngine.PlayAsync(cancellationToken);
                break;
            case MediaSessionAction.Stop:
                await StopPlaybackAsync(cancellationToken);
                break;
            case MediaSessionAction.Next:
                if (state.CanGoNext) await MoveNextAsync(cancellationToken);
                break;
            case MediaSessionAction.Previous:
                if (state.CanGoPrevious) await MovePreviousAsync(cancellationToken);
                break;
            case MediaSessionAction.Seek:
                if (!state.CanSeek) return;
                decimal position = snapshot.Position.Ticks / 10m + command.Position;
                if (position > snapshot.Track!.Duration.Ticks / 10m)
                {
                    if (state.CanGoNext) await MoveNextAsync(cancellationToken);
                }
                else await SeekPlaybackAsync(TimeSpan.FromTicks((long)Math.Max(0, position) * 10), cancellationToken);
                break;
            case MediaSessionAction.SetPosition:
                if (!state.CanSeek || command.TrackId != snapshot.Track!.Id || command.Position < 0 ||
                    command.Position > snapshot.Track.Duration.Ticks / 10) return;
                await SeekPlaybackAsync(TimeSpan.FromTicks(command.Position * 10), cancellationToken);
                break;
            case MediaSessionAction.SetVolume:
                if (!double.IsFinite(command.Volume)) throw new ArgumentOutOfRangeException(nameof(command));
                await ChangeVolumeAsync(Math.Clamp(command.Volume, 0, 1), cancellationToken);
                // MPRIS exposes effective volume: raising it also leaves mute mode.
                if (preferredMuted && command.Volume > 0)
                {
                    preferredMuted = false;
                    if (snapshot.Track is not null) await playbackEngine.SetMutedAsync(false, cancellationToken);
                    await PersistPreferencesAsync(cancellationToken);
                }
                break;
            case MediaSessionAction.SetShuffle:
                if (IsRadioPlayback) return;
                if (playbackQueue.ShuffleEnabled != command.Shuffle) await ToggleShuffleAsync(cancellationToken);
                break;
            case MediaSessionAction.SetRepeat:
                if (!Enum.IsDefined(command.Repeat)) throw new ArgumentOutOfRangeException(nameof(command));
                if (IsRadioPlayback) return;
                if (playbackQueue.RepeatMode != command.Repeat) await SetRepeatModeAsync(command.Repeat, cancellationToken);
                break;
            default:
                throw new NotSupportedException("This media command is unavailable.");
        }
    }
}
