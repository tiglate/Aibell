using Avalonia.Controls;
using Avalonia.Interactivity;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

/// <summary>
/// The Devices ("Sync to device") workspace. The view owns no work: its <see cref="DeviceSyncViewModel"/> is created, loaded and shut
/// down by the host (the main window), so navigating away only hides it and a running copy continues.
/// </summary>
public sealed partial class DeviceSyncView : UserControl
{
    public DeviceSyncView() => InitializeComponent();

    private DeviceSyncViewModel? Model => DataContext as DeviceSyncViewModel;

    private async void RefreshVolumesClick(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.RefreshVolumesAsync(); }
    private async void PreviewClick(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.PreviewAsync(); }
    private async void StartClick(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.StartAsync(); }
    private void CancelClick(object? sender, RoutedEventArgs args) => Model?.CancelOperation();
}
