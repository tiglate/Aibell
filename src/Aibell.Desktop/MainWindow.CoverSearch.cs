using Avalonia.Controls;
using Avalonia.Interactivity;
using Aibell.Desktop.Localization;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private readonly Func<Track, ArtworkWindow>? artworkWindowFactory;
    private bool artworkOpen;

    private string? ArtworkUnavailableReason(TrackRowViewModel? track) =>
        artworkWindowFactory is null ? Loc.Get("Artwork.Unavailable.NoFactory")
        : track is null ? Loc.Get("Artwork.Unavailable.NoTrack")
        : !track.Source.Location.IsFile ? Loc.Get("Artwork.Unavailable.NotFile")
        : string.IsNullOrWhiteSpace(track.Source.Album) ? Loc.Get("Artwork.Unavailable.NoAlbum")
        : null;

    private async void GetArtwork(object? sender, RoutedEventArgs args)
    {
        TrackRowViewModel? row = (sender as MenuItem)?.Tag as TrackRowViewModel ?? viewModel.SelectedTrack;
        if (artworkOpen || closeInProgress || disposed || viewModel.IsBusy) return;
        if (ArtworkUnavailableReason(row) is { } reason) { viewModel.StatusMessage = reason; return; }
        artworkOpen = true;
        try
        {
            if (!viewModel.OnlineLookupAllowed)
            {
                // Lookups are off by default; ask instead of failing silently or sending anything without consent.
                if (!await DialogChrome.ConfirmAsync(this, Loc.Get("Artwork.ConsentTitle"), Loc.Get("Artwork.ConsentBody"),
                        Loc.Get("Artwork.ConsentAccept"), "ConfirmEnableOnlineLookup")) return;
                await viewModel.SetOnlineLookupAsync(true);
            }
            ArtworkWindow dialog = artworkWindowFactory!(row!.Source);
            await dialog.ShowDialog<bool>(this);
            if (dialog.Model.SavedPath is { } saved)
            {
                viewModel.ReloadArtwork();
                viewModel.StatusMessage = Loc.Format("Artwork.StatusSaved", saved);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { viewModel.StatusMessage = Loc.Format("Artwork.StatusFailed", exception.Message); }
        finally { artworkOpen = false; }
    }
}
