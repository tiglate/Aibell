using Avalonia.Interactivity;
using Aibell.Application.Podcasts;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private readonly Func<IPodcastPlaybackHandoff, PodcastsViewModel>? podcastViewModelFactory;
    private PodcastsViewModel? podcasts;
    private bool podcastsLoaded;

    private void InitializePodcasts()
    {
        OpenPodcastsButton.IsEnabled = podcastViewModelFactory is not null;
    }

    private async void OpenPodcasts(object? sender, RoutedEventArgs args)
    {
        if (closeInProgress || disposed || podcastViewModelFactory is null) return;
        // Created on first use and then owned by the window until it closes: navigating away only hides the view, so
        // downloads, searches and position work continue and nothing is re-read or re-created on return.
        if (podcasts is null)
        {
            podcasts = podcastViewModelFactory(viewModel);
            PodcastsPane.DataContext = podcasts;
        }
        PodcastsViewModel model = podcasts;
        viewModel.ShowPodcastsView();
        if (podcastsLoaded) return;
        podcastsLoaded = true;
        await model.LoadAsync();
    }

    private async Task ShutdownPodcastsAsync()
    {
        if (podcasts is not { } model) return;
        await model.ShutdownAsync();
        model.Dispose();
    }
}
