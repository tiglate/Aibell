using Avalonia.Interactivity;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private EqualizerWindow? equalizerWindow;

    private void OpenEqualizer(object? sender, RoutedEventArgs args)
    {
        if (closeInProgress || disposed || !viewModel.CanUseEqualizer) return;
        if (equalizerWindow is { } existing) { existing.Activate(); return; }
        EqualizerWindow opened = new(new EqualizerViewModel(viewModel));
        equalizerWindow = opened;
        opened.Closed += (_, _) => { if (ReferenceEquals(equalizerWindow, opened)) equalizerWindow = null; };
        opened.Show(this);
    }

    private async Task ShutdownEqualizerAsync()
    {
        if (equalizerWindow is not { } opened) return;
        await opened.ShutdownAsync();
        opened.Close();
    }
}
