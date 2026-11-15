using Aibell.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Aibell.Desktop;

/// <summary>
/// The MPD group of the right-hand panel, shown under the play queue while MPD is selected in the Library list: the server connection,
/// its database and its outputs. The songs themselves are in the ordinary Music view. The view model is created and shut down by the
/// main window; nothing connects until the user presses Connect.
/// </summary>
public sealed partial class MpdPanel : UserControl
{
    public MpdPanel() => InitializeComponent();

    /// <summary>Opens the server settings window; set by the host.</summary>
    public Action? OpenSettings { get; set; }

    private MpdViewModel? Model => DataContext as MpdViewModel;

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private async void Connect(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.ConnectAsync(); }

    private async void Disconnect(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.DisconnectAsync(); }

    private void OpenServerSettings(object? sender, RoutedEventArgs args) => OpenSettings?.Invoke();

    private async void EditConnection(object? sender, RoutedEventArgs args)
    {
        if (Model is not { } m || Owner is not { } owner) return;
        await new MpdConnectionDialog(m).ShowDialog(owner);
    }

    private async void UpdateDatabase(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.UpdateDatabaseAsync(); }

    private async void OutputToggled(object? sender, RoutedEventArgs args)
    {
        if (sender is CheckBox { DataContext: MpdOutputRow row } box && Model is { } m) await m.SetOutputEnabledAsync(row, box.IsChecked == true);
    }
}
