using Aibell.Desktop.Localization;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private async void ImportPlaylistFile(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(async () =>
        {
            if (!viewModel.CanImportPlaylistFile) return;
            Uri? source = await filePicker.PickFileAsync(this, new FilePickerOpenOptions
            {
                Title = Loc.Get("Picker.ImportPlaylist"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Loc.Get("Picker.LocalPlaylists")) { Patterns = ["*.m3u", "*.m3u8", "*.pls"] }]
            });
            if (source is { IsFile: true } && CanRunUserAction) await ShowPlaylistImportAsync(source);
        });
    }

    public Task<bool> ShowPlaylistImportAsync(Uri source)
    {
        if (!CanRunUserAction) return Task.FromResult(false);
        PlaylistImportDialog dialog = new(source, token => viewModel.PreparePlaylistFileAsync(source, token),
            async (name, preview, token) => { await viewModel.ImportPlaylistFileAsync(name, preview, token); }, lifetimeCancellation.Token);
        return dialog.ShowDialog<bool>(this);
    }

    private async void ExportPlaylistFile(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(async () =>
        {
            if (!viewModel.CanExportPlaylistFile || viewModel.SelectedPlaylist is not { Id: { } id } playlist) return;
            string filename = string.Concat(playlist.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            Uri? destination = await filePicker.PickSaveFileAsync(this, new FilePickerSaveOptions
            {
                Title = Loc.Get("Picker.ExportPlaylist"),
                SuggestedFileName = filename + ".m3u8",
                DefaultExtension = "m3u8",
                FileTypeChoices = [new FilePickerFileType(Loc.Get("Picker.M3u8Playlist")) { Patterns = ["*.m3u8"] }],
                ShowOverwritePrompt = true
            });
            if (destination is { IsFile: true } && CanRunUserAction)
                await viewModel.ExportPlaylistFileAsync(id, destination, lifetimeCancellation.Token);
        });
    }
}
