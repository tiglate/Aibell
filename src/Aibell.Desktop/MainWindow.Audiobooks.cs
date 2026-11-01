using Avalonia.Interactivity;
using Aibell.Application.Audiobooks;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private readonly Func<IAudiobookPlaybackHandoff, AudiobooksViewModel>? audiobooksViewModelFactory;
    private AudiobooksViewModel? audiobooks;
    private bool audiobooksLoaded;

    private async void OpenAudiobooks(object? sender, RoutedEventArgs args)
    {
        if (closeInProgress || disposed || audiobooksViewModelFactory is null) return;
        // Created on first use and then owned by the window until it closes: navigating away only hides the view.
        if (audiobooks is null)
        {
            audiobooks = audiobooksViewModelFactory(viewModel);
            AudiobooksPane.DataContext = audiobooks;
        }
        AudiobooksViewModel model = audiobooks;
        viewModel.ShowAudiobooksView();
        if (audiobooksLoaded) return;
        audiobooksLoaded = true;
        await model.LoadAsync();
    }

    private async Task ShutdownAudiobooksAsync()
    {
        if (audiobooks is not { } model) return;
        await model.ShutdownAsync();
        model.Dispose();
    }
}
