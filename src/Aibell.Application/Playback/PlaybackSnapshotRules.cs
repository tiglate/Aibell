namespace Aibell.Application.Playback;

public static class PlaybackSnapshotRules
{
    public static PlaybackSnapshot AfterNativeStop(PlaybackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // LibVLC can report Stopped after EndReached or EncounteredError. Keep
        // the terminal state until a user command explicitly resets it.
        return snapshot.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted
            ? snapshot
            : snapshot with { Status = PlaybackStatus.Stopped, Position = TimeSpan.Zero, Error = null };
    }

    public static PlaybackSnapshot WithEnginePosition(PlaybackSnapshot snapshot, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused) || snapshot.Track is null)
        {
            return snapshot;
        }
        bool unboundedStream = snapshot.Track.Duration == TimeSpan.Zero && !snapshot.Track.Location.IsFile;
        TimeSpan clamped = position < TimeSpan.Zero ? TimeSpan.Zero
            : !unboundedStream && position > snapshot.Track.Duration ? snapshot.Track.Duration : position;
        return snapshot with { Position = clamped };
    }
}
