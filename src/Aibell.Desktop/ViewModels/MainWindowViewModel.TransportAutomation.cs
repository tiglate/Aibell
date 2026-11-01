using Aibell.Application.Playback;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly object playbackRequestGate = new();
    private Guid? playbackRequestTrackId;
    private bool playbackRequestActive;
    private long playbackRequestGeneration;

    // Read by the UI when publishing an immutable automation snapshot. Engine events update this
    // before UI dispatch, so coalesced snapshots cannot hide A→B→A or Stop→Play transitions.
    internal long PlaybackRequestGeneration => Interlocked.Read(ref playbackRequestGeneration);

    private void ObservePlaybackRequestBoundary(PlaybackSnapshot snapshot)
    {
        bool active = snapshot.Status is PlaybackStatus.Playing or PlaybackStatus.Paused;
        Guid? trackId = snapshot.Track?.Id;
        lock (playbackRequestGate)
        {
            if (trackId != playbackRequestTrackId || active != playbackRequestActive)
            {
                Interlocked.Increment(ref playbackRequestGeneration);
            }
            playbackRequestTrackId = trackId;
            playbackRequestActive = active;
        }
    }
}
