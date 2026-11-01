using Avalonia.Interactivity;
using Aibell.Application.Discs;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private readonly Func<IDiscPlaybackHandoff, DiscViewModel>? discViewModelFactory;
    private DiscViewModel? disc;
    private bool discLoaded;

    private async void OpenDisc(object? sender, RoutedEventArgs args)
    {
        if (closeInProgress || disposed || discViewModelFactory is null) return;
        // Created on first use and then owned by the window until it closes: navigating away only hides the view.
        if (disc is null)
        {
            disc = discViewModelFactory(viewModel);
            DiscPane.DataContext = disc;
        }
        DiscViewModel model = disc;
        viewModel.ShowDiscView();
        model.NotifyCapabilities();
        if (discLoaded) return;
        discLoaded = true;
        await DiscPane.LoadAsync();
    }

    private async Task ShutdownDiscAsync()
    {
        if (disc is not { } model) return;
        await model.ShutdownAsync();
        model.Dispose();
    }
}
