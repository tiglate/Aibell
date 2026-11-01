using Avalonia.Controls;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop.UiTests;

/// <summary>
/// Hosts <see cref="AudiobooksView"/> the way the main window does: the host loads the view-model and, on close, cancels and drains
/// accepted work before the window goes away. Whoever shows it must await <see cref="ShutdownAsync"/> when the test ends.
/// </summary>
internal sealed class AudiobooksTestWindow : Window
{
    private readonly AudiobooksViewModel viewModel;
    private Task? shutdown;
    private bool closeAfterShutdown;

    public AudiobooksTestWindow(AudiobooksViewModel viewModel, IDesktopFilePicker? picker = null)
    {
        this.viewModel = viewModel;
        Width = 820; Height = 560; MinWidth = 620; MinHeight = 380;
        Content = new AudiobooksView { DataContext = viewModel, Picker = picker };
        Opened += async (_, _) => await viewModel.LoadAsync();
        Closing += OnClosing;
    }

    public Task ShutdownAsync() => shutdown ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        await viewModel.ShutdownAsync();
        viewModel.Dispose();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (closeAfterShutdown) return;
        args.Cancel = true;
        await ShutdownAsync();
        closeAfterShutdown = true;
        Close();
    }
}
