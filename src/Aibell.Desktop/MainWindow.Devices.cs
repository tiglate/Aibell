using Avalonia.Interactivity;
using Aibell.Application.Devices;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private readonly Func<IDeviceTrackSource, DeviceSyncViewModel>? deviceSyncViewModelFactory;
    private DeviceSyncViewModel? deviceSync;
    private bool deviceSyncLoaded;

    private async void OpenDeviceSync(object? sender, RoutedEventArgs args)
    {
        if (closeInProgress || disposed || deviceSyncViewModelFactory is null) return;
        // Created on first use and then owned by the window until it closes: navigating away only hides the view, so a running copy continues.
        if (deviceSync is null)
        {
            deviceSync = deviceSyncViewModelFactory(new WindowTrackSource(this));
            DeviceSyncPane.DataContext = deviceSync;
        }
        DeviceSyncViewModel model = deviceSync;
        viewModel.ShowDevicesView();
        if (deviceSyncLoaded) return;
        deviceSyncLoaded = true;
        await model.LoadAsync();
    }

    private async Task ShutdownDeviceSyncAsync()
    {
        if (deviceSync is not { } model) return;
        await model.ShutdownAsync();
        model.Dispose();
    }

    /// <summary>The tracks the sync window can offer: the list selection, the current view and the whole library.</summary>
    private sealed class WindowTrackSource(MainWindow owner) : IDeviceTrackSource
    {
        public IReadOnlyList<Track> SelectedTracks() =>
            [.. owner.TrackList.SelectedItems?.OfType<TrackRowViewModel>().Select(row => row.Source) ?? []];

        public IReadOnlyList<Track> VisibleTracks() => [.. owner.viewModel.Tracks.Select(row => row.Source)];

        public Task<IReadOnlyList<Track>> AllTracksAsync(CancellationToken cancellationToken = default) => owner.viewModel.ListAllTracksAsync(cancellationToken);
    }
}
