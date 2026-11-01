using Avalonia.Controls;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

/// <summary>
/// Compatibility host that shows <see cref="PodcastsView"/> in its own window. The application itself embeds the view in the
/// main window; this host exists for tests and any caller that still wants a detached window. Whoever shows it must await
/// <see cref="ShutdownAsync"/>; closing the window itself also cancels and drains work.
/// </summary>
public sealed partial class PodcastsWindow : Window
{
    private readonly PodcastsViewModel viewModel;
    private Task? shutdown;
    private bool closeAfterShutdown;

    public PodcastsWindow() : this(null!) { }

    public PodcastsWindow(PodcastsViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        if (viewModel is null) return;
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.LoadAsync();
        Closing += OnClosing;
    }

    /// <summary>Cancels and drains in-flight operations. Idempotent.</summary>
    public Task ShutdownAsync() => shutdown ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        if (viewModel is null) return;
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
