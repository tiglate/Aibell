using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Aibell.Application.Playback;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop.Services;

internal sealed class DesktopMediaSessionController : IMediaSessionController, IAsyncDisposable
{
    private readonly MainWindowViewModel viewModel;
    private readonly Window window;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private MediaSessionState state;
    private bool updatePending;
    private bool disposed;

    public DesktopMediaSessionController(MainWindowViewModel viewModel, Window window)
    {
        this.viewModel = viewModel;
        this.window = window;
        state = viewModel.GetMediaSessionState();
        viewModel.PropertyChanged += OnPropertyChanged;
        viewModel.PlaybackSeeked += OnSeeked;
    }

    // Platform callbacks read an immutable publication; only the UI thread reads view-model dictionaries.
    public MediaSessionState State => Volatile.Read(ref state);
    public event EventHandler? StateChanged;
    public event EventHandler<TimeSpan>? Seeked;

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (disposed || updatePending || eventArgs.PropertyName is not
            ("CanPlay" or "CanGoNext" or "CanGoPrevious" or "PositionSeconds" or "Volume" or "MuteButtonText" or
             "ArtworkLocation" or "ShuffleButtonText" or "RepeatButtonText")) return;
        updatePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            updatePending = false;
            if (disposed) return;
            Volatile.Write(ref state, viewModel.GetMediaSessionState());
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnSeeked(object? sender, TimeSpan position)
    {
        if (!disposed) Seeked?.Invoke(this, position);
    }

    public async Task ExecuteAsync(MediaSessionCommand command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        await commandGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Task execution = await Dispatcher.UIThread.InvokeAsync<Task>(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                if (command.Action == MediaSessionAction.Raise)
                {
                    window.Show();
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                }
                else return ExecuteViewModelCommandAsync(command, linked.Token);
                return Task.CompletedTask;
            }, DispatcherPriority.Normal, linked.Token);
            await execution.ConfigureAwait(false);
        }
        finally { commandGate.Release(); }
    }

    private async Task ExecuteViewModelCommandAsync(MediaSessionCommand command, CancellationToken cancellationToken)
    {
        await viewModel.ExecuteMediaSessionCommandAsync(command, cancellationToken);
        // A completed platform command must expose its new state immediately, even before
        // the coalesced PropertyChanged notification reaches the dispatcher queue.
        if (!disposed) Volatile.Write(ref state, viewModel.GetMediaSessionState());
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        viewModel.PropertyChanged -= OnPropertyChanged;
        viewModel.PlaybackSeeked -= OnSeeked;
        await lifetime.CancelAsync();
        await commandGate.WaitAsync();
        commandGate.Release();
        lifetime.Dispose();
    }
}
