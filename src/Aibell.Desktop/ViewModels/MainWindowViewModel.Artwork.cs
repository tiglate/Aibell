using System.Diagnostics;
using Aibell.Domain.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private (Uri? Location, DateTimeOffset? Modified) artworkSource;
    private CancellationTokenSource? artworkCancellation;
    private Task pendingArtworkLoad = Task.CompletedTask;

    [ObservableProperty]
    private Uri? artworkLocation;

    public Task WaitForArtworkAsync() => pendingArtworkLoad;

    public void Dispose() { radioLogoCancellation.Cancel(); BeginLibraryShutdown(); BeginPlaybackShutdown(); StopListening(); }

    private void UpdateArtwork(Track? track)
    {
        UpdateRadioNowPlayingLogo(track);
        (Uri? Location, DateTimeOffset? Modified) source = (track?.Location, track?.DateModified);
        if (artworkSource == source)
        {
            return;
        }
        CancelArtwork();
        artworkSource = source;
        ArtworkLocation = null;
        if (track is null || artworkProvider is null)
        {
            return;
        }

        artworkCancellation = new CancellationTokenSource();
        pendingArtworkLoad = LoadArtworkAsync(pendingArtworkLoad, track.Location, artworkCancellation.Token);
    }

    /// <summary>Looks for the cover of the current track again, for when a cover file has just been saved beside it.</summary>
    public void ReloadArtwork()
    {
        artworkSource = default;
        UpdateArtwork(playbackEngine.Snapshot.Track);
    }

    private async Task LoadArtworkAsync(Task previousLoad, Uri location, CancellationToken cancellationToken)
    {
        try
        {
            await previousLoad;
            cancellationToken.ThrowIfCancellationRequested();
            Uri? cachedLocation = await artworkProvider!.GetAsync(location, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ArtworkLocation = cachedLocation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Could not load local artwork: {exception.Message}");
        }
    }

    private void CancelArtwork()
    {
        artworkCancellation?.Cancel();
        artworkCancellation?.Dispose();
        artworkCancellation = null;
        artworkSource = default;
    }
}
