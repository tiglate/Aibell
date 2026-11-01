using Avalonia.Controls;
using Avalonia.Interactivity;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

/// <summary>Non-modal ten-band equalizer. Closing it writes the current curve to the preferences.</summary>
public sealed partial class EqualizerWindow : Window
{
    private readonly EqualizerViewModel viewModel;
    private bool closeAfterFlush;

    public EqualizerWindow() : this(null!) { }

    public EqualizerWindow(EqualizerViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        if (viewModel is null) return;
        DataContext = viewModel;
        Closing += OnClosing;
    }

    /// <summary>Saves the curve and releases the view model. Idempotent.</summary>
    public async Task ShutdownAsync()
    {
        if (viewModel is null || closeAfterFlush) return;
        closeAfterFlush = true;
        try { await viewModel.FlushAsync(); }
        finally { viewModel.Dispose(); }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (closeAfterFlush) return;
        args.Cancel = true;
        await ShutdownAsync();
        Close();
    }

    private void Reset(object? sender, RoutedEventArgs args) => viewModel.Reset();
}
