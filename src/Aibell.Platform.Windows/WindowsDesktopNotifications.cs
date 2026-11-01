using System.Runtime.InteropServices;
using Aibell.Application.Playback;
using Aibell.Platform.Windows.Native;

namespace Aibell.Platform.Windows;

/// <summary>
/// Track-change banners through the Windows notification area. The shell shows a balloon request as a toast on Windows 10
/// and 11 without a Start-menu shortcut or package identity, which an unpackaged self-contained app does not have.
/// A single pump thread owns a hidden window and one notify icon that is added for the banner and removed after it.
/// </summary>
public sealed class WindowsDesktopNotifications : IDesktopNotifications, IDisposable
{
    private const uint WmApp = NativeMethods.WmApp;
    private const uint WmQuit = NativeMethods.WmApp + 1;
    private const uint IconId = 1;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IconLifetime = TimeSpan.FromSeconds(8);
    private readonly Queue<Action> requests = new();
    private readonly Lock gate = new();
    private Thread? thread;
    private TaskCompletionSource? started;
    private volatile nint window;
    private bool disposed;

    // Pump-thread state: only touched by Run and the queued Show closures.
    private bool iconAdded;
    private nuint timer;

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240);

    public async Task ShowAsync(NowPlayingNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsSupported) throw new PlatformNotSupportedException("Windows notifications require Windows 10 or later.");
        (string title, string body) = Text(notification);
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task ready = EnsureStarted();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        await ready.WaitAsync(deadline.Token).ConfigureAwait(false);
        nint target;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            target = window;
            requests.Enqueue(() =>
            {
                try { Show(title, body); done.TrySetResult(); }
                catch (Exception exception) { done.TrySetException(exception); }
            });
        }
        if (target == 0 || !NativeMethods.PostMessage(target, WmApp, 0, 0))
            throw new InvalidOperationException("The notification window is not running.");
        await done.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Single-line, NUL-free text bounded to the fixed NOTIFYICONDATA buffers (title 63, body 255 characters).</summary>
    internal static (string Title, string Body) Text(NowPlayingNotification notification)
    {
        string title = Limit(notification.Title, 63);
        string artist = Limit(notification.Artist, 126), album = Limit(notification.Album, 126);
        string body = artist.Length == 0 ? album : album.Length == 0 ? artist : artist + "\n" + album;
        return (title.Length == 0 ? "Unknown title" : title, body);
        static string Limit(string value, int length)
        {
            string text = value.Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length <= length) return text;
            text = text[..length];
            return char.IsHighSurrogate(text[^1]) ? text[..^1] : text;
        }
    }

    private Task EnsureStarted()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows notifications require Windows 10 or later.");
            if (started is not null && started.Task.IsFaulted) { thread = null; started = null; }
            if (started is not null) return started.Task;
            started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            thread = new Thread(Run) { Name = "Aibell.WindowsNotifications", IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start(started);
            return started.Task;
        }
    }

    private void Run(object? state)
    {
        TaskCompletionSource ready = (TaskCompletionSource)state!;
        nint hwnd = 0;
        try
        {
            hwnd = NativeMethods.CreateWindowEx(0, "Static", "Aibell notifications", NativeMethods.WsPopup, 0, 0, 0, 0, 0, 0, 0, 0);
            if (hwnd == 0) throw new InvalidOperationException("The notification window could not be created.");
            lock (gate) window = hwnd;
            ready.TrySetResult();
            while (NativeMethods.GetMessage(out NativeMethods.Message message, 0, 0, 0) > 0)
            {
                if (message.Window == hwnd && message.Id == WmQuit) break;
                if (message.Window == hwnd && message.Id == WmApp)
                {
                    RunRequests();
                    continue;
                }
                if (message.Window == 0 && message.Id == NativeMethods.WmTimer && message.WParam == timer && timer != 0)
                {
                    RemoveIcon(hwnd);
                    continue;
                }
                NativeMethods.TranslateMessage(message);
                NativeMethods.DispatchMessage(message);
            }
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
        }
        finally
        {
            if (hwnd != 0)
            {
                RemoveIcon(hwnd);
                lock (gate) window = 0;
                _ = NativeMethods.DestroyWindow(hwnd);
            }
            ready.TrySetCanceled();
            Action[] abandoned;
            lock (gate) { abandoned = [.. requests]; requests.Clear(); }
            foreach (Action request in abandoned) request();
        }
    }

    private void RunRequests()
    {
        while (true)
        {
            Action? request;
            lock (gate) request = requests.Count > 0 ? requests.Dequeue() : null;
            if (request is null) return;
            request();
        }
    }

    private unsafe void Show(string title, string body)
    {
        nint hwnd = window;
        NativeMethods.NotifyIconData data = new()
        {
            Size = (uint)sizeof(NativeMethods.NotifyIconData),
            Window = hwnd,
            Id = IconId,
            Flags = NativeMethods.NifIcon | NativeMethods.NifTip,
            Icon = NativeMethods.LoadIcon(0, NativeMethods.IdiApplication)
        };
        Copy("Aibell", data.Tip, 128);
        if (!iconAdded)
        {
            if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, data)) throw new InvalidOperationException("The notification icon could not be added.");
            iconAdded = true;
            data.TimeoutOrVersion = NativeMethods.NotifyIconVersion4;
            _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimSetVersion, data);
        }
        data.Flags = NativeMethods.NifInfo;
        data.InfoFlags = NativeMethods.NiifNoSound | NativeMethods.NiifRespectQuietTime;
        data.TimeoutOrVersion = 5000;
        Copy(title, data.InfoTitle, 64);
        Copy(body, data.Info, 256);
        if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, data)) throw new InvalidOperationException("The notification could not be shown.");
        if (timer != 0) NativeMethods.KillTimer(0, timer);
        timer = NativeMethods.SetTimer(0, 0, (uint)IconLifetime.TotalMilliseconds, 0);
    }

    private static unsafe void Copy(string value, char* destination, int capacity)
    {
        int length = Math.Min(value.Length, capacity - 1);
        value.AsSpan(0, length).CopyTo(new Span<char>(destination, length));
        destination[length] = '\0';
    }

    private unsafe void RemoveIcon(nint hwnd)
    {
        if (timer != 0) { NativeMethods.KillTimer(0, timer); timer = 0; }
        if (!iconAdded) return;
        iconAdded = false;
        NativeMethods.NotifyIconData data = new() { Size = (uint)sizeof(NativeMethods.NotifyIconData), Window = hwnd, Id = IconId };
        _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, data);
    }

    public void Dispose()
    {
        Thread? running;
        nint target;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            running = thread;
            target = window;
        }
        if (running is null) return;
        if (target != 0) NativeMethods.PostMessage(target, WmQuit, 0, 0);
        if (!running.Join(Deadline)) System.Diagnostics.Trace.WriteLine("Windows notification pump thread did not stop within the shutdown bound.");
    }
}
