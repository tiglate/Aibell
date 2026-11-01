using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private Bitmap? coverBitmap;
    private Bitmap? defaultCoverBitmap;
    private CancellationTokenSource? coverCancellation;
    private Task pendingCoverLoad = Task.CompletedTask;

    private void InitializeArtwork()
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://Aibell.Desktop/Assets/default-cover.png"));
        defaultCoverBitmap = Bitmap.DecodeToWidth(stream, 512);
        NowPlayingCover.Source = defaultCoverBitmap;
        viewModel.PropertyChanged += OnArtworkPropertyChanged;
        ShowNowPlayingImage();
        if (viewModel.ArtworkLocation is not null)
        {
            LoadCover(viewModel.ArtworkLocation);
        }
    }

    private void OnArtworkPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(viewModel.ArtworkLocation))
        {
            LoadCover(viewModel.ArtworkLocation);
        }
        else if (eventArgs.PropertyName == nameof(viewModel.RadioNowPlayingLogo))
        {
            ShowNowPlayingImage();
        }
    }

    // A playing station's logo takes the place of the cover; the view model owns (and caches) the logo bitmap, so it is never disposed here.
    private void ShowNowPlayingImage() => NowPlayingCover.Source = viewModel.RadioNowPlayingLogo ?? coverBitmap ?? defaultCoverBitmap;

    private void LoadCover(Uri? location)
    {
        coverCancellation?.Cancel();
        coverCancellation?.Dispose();
        coverCancellation = null;
        Bitmap? previous = coverBitmap;
        coverBitmap = null;
        ShowNowPlayingImage();
        previous?.Dispose();
        if (location is null || !location.IsFile)
        {
            return;
        }
        coverCancellation = new CancellationTokenSource();
        pendingCoverLoad = DecodeCoverAsync(pendingCoverLoad, location.LocalPath, coverCancellation.Token);
    }

    private async Task DecodeCoverAsync(Task previousLoad, string path, CancellationToken cancellationToken)
    {
        Bitmap? decoded = null;
        try
        {
            await previousLoad;
            decoded = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, 512);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            coverBitmap = decoded;
            decoded = null;
            ShowNowPlayingImage();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Could not decode local artwork: {exception.Message}");
        }
        finally
        {
            decoded?.Dispose();
        }
    }

    private void DisposeArtwork()
    {
        viewModel.PropertyChanged -= OnArtworkPropertyChanged;
        coverCancellation?.Cancel();
        coverCancellation?.Dispose();
        coverCancellation = null;
        NowPlayingCover.Source = null;
        coverBitmap?.Dispose();
        coverBitmap = null;
        defaultCoverBitmap?.Dispose();
        defaultCoverBitmap = null;
    }
}
