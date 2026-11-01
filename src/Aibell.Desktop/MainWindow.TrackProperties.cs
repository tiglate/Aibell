using Aibell.Desktop.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private bool trackPropertiesOpen;
    private async void ViewTrackProperties(object? sender, RoutedEventArgs args)
    {
        TrackRowViewModel? row = (sender as MenuItem)?.Tag as TrackRowViewModel
            ?? (sender as Control)?.DataContext as TrackRowViewModel ?? viewModel.SelectedTrack;
        if (row is not null) await ShowTrackPropertiesAsync(row);
    }

    private async void TrackPropertiesKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Alt) && viewModel.SelectedTrack is { } row)
        {
            args.Handled = true;
            await ShowTrackPropertiesAsync(row);
        }
    }

    private async Task ShowTrackPropertiesAsync(TrackRowViewModel row)
    {
        if (viewModel.IsBusy || trackPropertiesOpen) return;
        trackPropertiesOpen = true;
        try
        {
            TrackPropertiesData data = await viewModel.GetTrackPropertiesAsync(row.Source, lifetimeCancellation.Token);
            if (closeInProgress || lifetimeCancellation.IsCancellationRequested) return;
            await new TrackPropertiesDialog(data, (metadata, toFile, artwork, token) => toFile
                ? viewModel.SaveTrackMetadataToFileAsync(data, metadata, artwork, token)
                : viewModel.SaveTrackMetadataAsync(row.Source.Id, metadata, token),
                token => viewModel.RestoreTrackFileAsync(data, token),
                (owner, token) => new MetadataBackupsDialog(data.Track,
                    ct => viewModel.GetTrackBackupsAsync(data.Track, ct),
                    (backup, ct) => viewModel.RestoreTrackFileAsync(data with { FileBackup = backup }, ct),
                    (backup, ct) => viewModel.DeleteTrackBackupAsync(backup, ct), token).ShowDialog<bool>(owner))
                .ShowDialog<bool>(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { viewModel.StatusMessage = Loc.Format("Main.StatusTrackProps", exception.Message); }
        finally { trackPropertiesOpen = false; }
    }
}
