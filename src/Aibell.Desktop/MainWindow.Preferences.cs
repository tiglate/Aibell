using Aibell.Desktop.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private bool preferencesOpen;
    private async void OpenPreferences(object? sender, RoutedEventArgs args) => await ShowPreferencesAsync();
    private async void PreferencesKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.OemComma && args.KeyModifiers.HasFlag(KeyModifiers.Control))
        { args.Handled = true; await ShowPreferencesAsync(); }
    }

    private async Task ShowPreferencesAsync()
    {
        if (!CanRunUserAction || preferencesOpen || viewModel.IsBusy) return;
        preferencesOpen = true;
        try
        {
            await new PreferencesDialog(viewModel, async owner =>
            {
                Uri? path = await filePicker.PickFolderAsync(owner, new FolderPickerOpenOptions
                { Title = Loc.Get("Picker.AddMusicFolder"), AllowMultiple = false });
                if (path is { IsFile: true } && CanRunUserAction)
                    await viewModel.ImportAsync(path.LocalPath, lifetimeCancellation.Token);
            }, async owner =>
            {
                await new MetadataRecoveryDialog(viewModel.LibrarySources.Select(source => source.Path).ToArray(),
                    viewModel.ScanMetadataRecoveryAsync, viewModel.DiscardMetadataPreviewAsync, cancellationToken: lifetimeCancellation.Token).ShowDialog<bool>(owner);
            }, async (owner, current) =>
            {
                Uri? path = await filePicker.PickFolderAsync(owner, new FolderPickerOpenOptions
                { Title = Loc.Get("Picker.PodcastFolder"), AllowMultiple = false });
                return path is { IsFile: true } ? path.LocalPath : null;
            }, async owner =>
            {
                Uri? path = await filePicker.PickFolderAsync(owner, new FolderPickerOpenOptions
                { Title = Loc.Get("Picker.PluginFolder"), AllowMultiple = false });
                return path is { IsFile: true } ? path.LocalPath : null;
            }, async owner =>
            {
                Uri? path = await filePicker.PickFileAsync(owner, new FilePickerOpenOptions
                { Title = Loc.Get("Picker.PluginZip"), AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(Loc.Get("Picker.PluginArchive")) { Patterns = ["*.zip"] }] });
                return path is { IsFile: true } ? path.LocalPath : null;
            }, async owner =>
            {
                Uri? path = await filePicker.PickFileAsync(owner, new FilePickerOpenOptions
                { Title = Loc.Get("Picker.SoundFont"), AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(Loc.Get("Picker.SoundFontFiles")) { Patterns = ["*.sf2"] }] });
                return path is { IsFile: true } ? path.LocalPath : null;
            }).ShowDialog<bool>(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { viewModel.StatusMessage = Loc.Format("Main.StatusPrefs", exception.Message); }
        finally { preferencesOpen = false; }
    }
}
