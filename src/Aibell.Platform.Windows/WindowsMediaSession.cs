using System.Diagnostics;
using System.Threading.Channels;
using Aibell.Application.Playback;
using Aibell.Platform.Windows.Native;

namespace Aibell.Platform.Windows;

/// <summary>
/// Publishes playback state to the Windows System Media Transport Controls (media keys, lock screen and
/// volume flyout) and dispatches transport button presses to the media session controller.
/// </summary>
public sealed class WindowsMediaSession : IMediaSession
{
    private readonly Func<IMediaTransport> transportFactory;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<MediaSessionCommand> commands = Channel.CreateBounded<MediaSessionCommand>(
        new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private IMediaTransport? transport;
    private IMediaSessionController? controller;
    private Task dispatcher = Task.CompletedTask;
    private TransportSnapshot? published;
    private bool started;
    private bool disposed;
    private Task? disposal;

    public WindowsMediaSession() : this(() => new SmtcMediaTransport()) { }

    internal WindowsMediaSession(Func<IMediaTransport> transportFactory) =>
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240);

    public async Task StartAsync(IMediaSessionController controller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("The media session has already started.");
            started = true;
            this.controller = controller;
        }
        IMediaTransport? candidate = null;
        bool adopted = false;
        try
        {
            candidate = transportFactory();
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                transport = candidate;
                adopted = true;
                candidate.ButtonPressed += OnButtonPressed;
                controller.StateChanged += OnStateChanged;
            }
            await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                dispatcher = Task.Run(() => DispatchAsync(controller), CancellationToken.None);
            }
            Publish();
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ObjectDisposedException or InvalidOperationException))
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Windows system media controls are unavailable: " + exception.Message, exception);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            // A transport created but never adopted (disposed during startup) must still be released.
            if (candidate is not null && !adopted) await candidate.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnStateChanged(object? sender, EventArgs eventArgs) => Publish();

    private void Publish()
    {
        lock (gate)
        {
            if (disposed || controller is null || transport is null) return;
            TransportSnapshot next = TransportMapper.Map(controller.State);
            if (next == published) return;
            published = next;
            transport.Apply(next);
        }
    }

    private void OnButtonPressed(TransportButton button)
    {
        if (TransportMapper.ToCommand(button) is { } command) commands.Writer.TryWrite(command);
    }

    private async Task DispatchAsync(IMediaSessionController active)
    {
        try
        {
            await foreach (MediaSessionCommand command in commands.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                try { await active.ExecuteAsync(command, lifetime.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
                catch (Exception exception) { Trace.WriteLine($"Windows media command {command.Action} failed: {exception.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        IMediaTransport? active;
        Task pending;
        lock (gate)
        {
            disposed = true;
            if (controller is not null) controller.StateChanged -= OnStateChanged;
            active = transport;
            transport = null;
            controller = null;
            pending = dispatcher;
        }
        if (active is not null) active.ButtonPressed -= OnButtonPressed;
        commands.Writer.TryComplete();
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (active is not null) await active.DisposeAsync().ConfigureAwait(false);
        await pending.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
