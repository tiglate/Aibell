using System.Diagnostics;
using Aibell.Application.Playback;
using Aibell.Desktop.Services;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private IMediaSession? mediaSession;
    private DesktopMediaSessionController? mediaSessionController;
    private Task mediaSessionStartup = Task.CompletedTask;
    private CancellationTokenSource? mediaSessionStartupCancellation;
    private Task? mediaSessionShutdown;

    private async Task StartMediaSessionAsync()
    {
        if (mediaSession is null) return;
        mediaSessionController = new(viewModel, this);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        mediaSessionStartupCancellation = timeout;
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await mediaSession.StartAsync(mediaSessionController, timeout.Token); }
        catch (Exception exception)
        {
            Trace.WriteLine($"System media controls unavailable: {exception.Message}");
            await mediaSessionController.DisposeAsync();
        }
        finally { mediaSessionStartupCancellation = null; }
    }

    private Task StopMediaSessionAsync() => mediaSessionShutdown ??= StopMediaSessionCoreAsync();

    private async Task StopMediaSessionCoreAsync()
    {
        mediaSessionStartupCancellation?.Cancel();
        if (mediaSession is not null) await mediaSession.DisposeAsync();
        await mediaSessionStartup;
        if (mediaSessionController is not null) await mediaSessionController.DisposeAsync();
    }
}
