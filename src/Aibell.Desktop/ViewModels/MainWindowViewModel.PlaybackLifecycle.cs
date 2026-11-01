using Aibell.Desktop.Localization;
using System.Diagnostics;
using Avalonia.Threading;
using Aibell.Application.Playback;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly HashSet<Task> playbackOperations = [];
    private readonly object playbackOperationGate = new();
    private bool playbackShutdown;
    private long playbackStopGeneration;

    public void BeginPlaybackShutdown() => playbackShutdown = true;

    private Task RunPlaybackOperation(Func<Task> operation)
    {
        if (playbackShutdown) return Task.CompletedTask;
        Task task = operation();
        lock (playbackOperationGate) playbackOperations.Add(task);
        _ = ObservePlaybackOperationAsync(task);
        return task;
    }

    private async Task ObservePlaybackOperationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Trace.WriteLine($"Playback operation ended: {exception.Message}");
            StatusMessage = Loc.Format("Playback.OperationFailed", exception.Message);
        }
        finally { lock (playbackOperationGate) playbackOperations.Remove(task); }
    }

    public async Task WaitForPlaybackOperationsAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (playbackOperationGate) pending = playbackOperations.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending); }
            catch (Exception exception) { Trace.WriteLine($"Playback shutdown drain: {exception.Message}"); }
        }
    }

    private async Task NavigatePlaybackAsync(bool previous, CancellationToken cancellationToken)
    {
        if (changingTrack || IsRadioPlayback || !(previous ? CanGoPrevious : CanGoNext)) return;
        long stopGeneration = playbackStopGeneration;
        changingTrack = true;
        NotifyNavigationState();
        try
        {
            await RecordManualSkipAsync(cancellationToken);
            // A history write already in progress drains, but Stop/Close must not
            // be followed by the deferred request starting another audio source.
            if (playbackShutdown || stopGeneration != playbackStopGeneration) return;
            if (previous)
            {
                if (isPlayingQueuedTrack && playbackQueue.Current is not null)
                {
                    await PlayCurrentAsync(cancellationToken);
                    return;
                }
                if (!playbackQueue.TryMovePrevious()) return;
            }
            else
            {
                if (await PlayNextQueuedAsync(cancellationToken)) return;
                if (!playbackQueue.TryMoveNext()) return;
            }
            await PlayCurrentAsync(cancellationToken);
        }
        finally { CompletePlaybackTransition(); }
    }

    private void ProcessPlaybackEnd(PlaybackSnapshot ended)
    {
        PlaybackSnapshot current = playbackEngine.Snapshot;
        if (!isListening || playbackShutdown || changingTrack || ended.Status != PlaybackStatus.Ended ||
            ended.Track is null || current.Status != PlaybackStatus.Ended || current.Track?.Id != ended.Track.Id) return;
        _ = AdvanceAfterEndAsync();
    }

    private void CompletePlaybackTransition()
    {
        changingTrack = false;
        NotifyNavigationState();
        OnPropertyChanged(nameof(CanManageRadio)); OnPropertyChanged(nameof(CanEditRadioStation));
        PlaybackSnapshot current = playbackEngine.Snapshot;
        if (isListening && !playbackShutdown && current.Status == PlaybackStatus.Ended)
            Dispatcher.UIThread.Post(() => ProcessPlaybackEnd(current));
    }
}
