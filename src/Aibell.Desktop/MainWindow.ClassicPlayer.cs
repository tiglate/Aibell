using Aibell.Application.Library;
using Aibell.Desktop.Classic;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Skins.Classic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;

namespace Aibell.Desktop;

/// <summary>Switching between the standard window and Classic Player Mode. Both drive the same player, so music never stops.</summary>
public sealed partial class MainWindow
{
    private ClassicPlayerViewModel? classicModel;
    private ClassicPlayerWindow? classicWindow;

    internal ClassicPlayerWindow? ClassicWindow => classicWindow;

    internal ClassicPlayerViewModel? ClassicModel => classicModel;

    /// <summary>Whether the classic window is showing instead of this one.</summary>
    internal bool IsClassicModeActive => classicWindow?.IsVisible == true;

    private void SwitchToClassicMode(object? sender, RoutedEventArgs args) => EnterClassicMode();

    internal void EnterClassicMode()
    {
        if (closeInProgress || disposed) return;
        classicModel ??= new ClassicPlayerViewModel(viewModel);
        if (classicWindow is null)
        {
            IServiceProvider? services = App.Services;
            SkinLibrary skins = services?.GetService<SkinLibrary>() ?? new SkinLibrary(Path.Combine(Path.GetTempPath(), "aibell-skins"));
            classicWindow = new ClassicPlayerWindow(classicModel, skins, services?.GetService<SkinGalleryClient>(), filePicker,
                services?.GetService<IMediaFileDiscovery>(), ExitClassicMode, Close)
            { Icon = Icon };
        }
        _ = viewModel.UpdateClassicPlayerAsync(preferences => preferences with { IsEnabled = true });
        classicWindow.Show();
        classicWindow.Activate();
        Hide();
    }

    internal void ExitClassicMode()
    {
        if (closeInProgress || disposed) return;
        _ = viewModel.UpdateClassicPlayerAsync(preferences => preferences with { IsEnabled = false });
        Opacity = 1;
        Show();
        Activate();
        classicWindow?.Hide();
    }

    /// <summary>Files from another launch, while the classic window shows: added to its playlist, the first one playing.</summary>
    private async Task OpenInClassicModeAsync(IReadOnlyList<Uri> files)
    {
        if (classicModel is null || classicWindow is null) return;
        classicWindow.Activate();
        if (files.Count == 0) return;
        IReadOnlyList<Track> tracks = await viewModel.ReadTracksAsync(files, lifetimeCancellation.Token);
        int first = classicModel.Add(tracks);
        if (first >= 0) await classicModel.PlayEntryAsync(first, lifetimeCancellation.Token);
    }

    private void CloseClassicWindow()
    {
        classicWindow?.CloseForExit();
        classicWindow = null;
    }
}
