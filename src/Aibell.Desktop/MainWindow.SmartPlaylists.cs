using Avalonia.Interactivity;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private async void CreateSmartPlaylist(object? sender, RoutedEventArgs eventArgs)
    {
        if (viewModel.IsBusy) return;
        SmartPlaylistDialog dialog = new();
        SmartPlaylistEdit? result = await dialog.ShowDialog<SmartPlaylistEdit?>(this);
        if (result is not null)
            await RunUserActionAsync(() => viewModel.CreateSmartPlaylistAsync(result.Name, result.Definition, lifetimeCancellation.Token));
    }

    private async void EditSmartPlaylist(object? sender, RoutedEventArgs eventArgs)
    {
        if (viewModel.IsBusy || viewModel.SelectedPlaylist is not { IsSmart: true } playlist) return;
        SmartPlaylistDialog dialog = new(playlist.Name, playlist.Definition);
        SmartPlaylistEdit? result = await dialog.ShowDialog<SmartPlaylistEdit?>(this);
        if (result is null) return;
        await RunUserActionAsync(() => viewModel.UpdateSelectedSmartPlaylistAsync(result.Definition, lifetimeCancellation.Token));
    }
}
