using System.Runtime.InteropServices;

namespace Aibell.Platform.Windows.Native;

/// <summary>
/// System Media Transport Controls bound to a private hidden top-level window. All WinRT objects live on a dedicated
/// thread that owns the window and pumps its messages; <see cref="Apply"/> only stores the latest snapshot and posts a
/// wake-up message, so callers never block on the shell.
/// </summary>
internal sealed class SmtcMediaTransport : IMediaTransport
{
    private const uint WmQuitTransport = NativeMethods.WmApp + 1;
    private const uint WmApply = NativeMethods.WmApp;
    private const uint WmRequest = NativeMethods.WmApp + 2;
    private const long MaxArtworkBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private readonly Queue<Action> requests = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock gate = new();
    private Thread? thread;
    private volatile nint window;
    private TransportSnapshot? pending;
    private TransportSnapshot? lastApplied;
    private bool disposed;
    private Task? shutdown;

    // Native handles; only touched by the pump thread after construction.
    private nint controls, updater, music, music2, handlerPointer;
    private ButtonPressedHandler? handler;
    private long token;
    private bool registered;

    public event Action<TransportButton>? ButtonPressed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("System media transport controls require Windows 10 or later.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (thread is not null) throw new InvalidOperationException("The transport has already started.");
            thread = new Thread(Run) { Name = "Aibell.SystemMediaTransport", IsBackground = true };
            // The CLR defaults new threads to MTA; RoInitialize(RO_INIT_SINGLETHREADED)
            // must agree with the managed apartment selected before the thread starts.
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        return started.Task.WaitAsync(cancellationToken);
    }

    public void Apply(TransportSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        nint target;
        lock (gate)
        {
            if (disposed) return;
            pending = snapshot;
            target = window;
        }
        if (target != 0) NativeMethods.PostMessage(target, WmApply, 0, 0);
    }

    private void Run()
    {
        bool initialized = false;
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.RoInitialize(0));
            initialized = true;
            nint hwnd = NativeMethods.CreateWindowEx(0, "Static", "Aibell media transport", NativeMethods.WsPopup, 0, 0, 0, 0, 0, 0, 0, 0);
            if (hwnd == 0) throw new InvalidOperationException("The media transport window could not be created.");
            lock (gate) window = hwnd;
            Initialize(hwnd);
            bool cancelled;
            lock (gate) cancelled = disposed;
            if (cancelled) return;
            started.TrySetResult();
            ApplyPending();
            while (NativeMethods.GetMessage(out NativeMethods.Message message, 0, 0, 0) > 0)
            {
                if (message.Window == hwnd && message.Id == WmQuitTransport) break;
                if (message.Window == hwnd && message.Id == WmApply) { ApplyPending(); continue; }
                if (message.Window == hwnd && message.Id == WmRequest) { RunRequests(); continue; }
                NativeMethods.TranslateMessage(message);
                NativeMethods.DispatchMessage(message);
            }
        }
        catch (Exception exception)
        {
            started.TrySetException(exception);
        }
        finally
        {
            Safely(Teardown);
            nint hwnd;
            lock (gate) { hwnd = window; window = 0; }
            if (hwnd != 0) Safely(() => NativeMethods.DestroyWindow(hwnd));
            if (initialized) Safely(NativeMethods.RoUninitialize);
            started.TrySetCanceled();
            CancelRequests();
        }
    }

    private static void Safely(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            System.Diagnostics.Trace.WriteLine($"System media controls cleanup step failed: {exception.Message}");
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

    private void CancelRequests()
    {
        Action[] abandoned;
        lock (gate) { abandoned = [.. requests]; requests.Clear(); }
        foreach (Action request in abandoned) request();
    }

    private void Initialize(nint hwnd)
    {
        using (HString className = HString.Create(SmtcSlots.ClassName))
        {
            Marshal.ThrowExceptionForHR(NativeMethods.RoGetActivationFactory(className.Handle, SmtcSlots.InteropIid, out nint factory));
            try { Marshal.ThrowExceptionForHR(VTable.CallGetForWindow(factory, hwnd, SmtcSlots.ControlsIid, out controls)); }
            finally { Marshal.Release(factory); }
        }
        Marshal.ThrowExceptionForHR(VTable.CallOut(controls, SmtcSlots.GetDisplayUpdater, out updater));
        Marshal.ThrowExceptionForHR(VTable.Call(updater, SmtcSlots.UpdaterPutType, SmtcSlots.MediaPlaybackTypeMusic));
        Marshal.ThrowExceptionForHR(VTable.CallOut(updater, SmtcSlots.UpdaterGetMusicProperties, out music));
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(music, in SmtcSlots.MusicProperties2Iid, out music2));
        handler = ButtonPressedHandler.Create(button => ButtonPressed?.Invoke(button), out handlerPointer);
        Marshal.ThrowExceptionForHR(VTable.CallAddHandler(controls, SmtcSlots.AddButtonPressed, handlerPointer, out token));
        registered = true;
        Marshal.ThrowExceptionForHR(VTable.Call(controls, SmtcSlots.PutIsEnabled, (byte)1));
    }

    private void ApplyPending()
    {
        TransportSnapshot? next;
        lock (gate) { next = pending; pending = null; }
        if (next is null || next == lastApplied || controls == 0) return;
        TransportSnapshot? previous = lastApplied;
        int failure = 0;
        void Step(int hr)
        {
            if (hr < 0 && failure == 0) failure = hr;
        }
        Step(VTable.Call(controls, SmtcSlots.PutPlaybackStatus, (int)next.Playback));
        Step(VTable.Call(controls, SmtcSlots.PutIsPlayEnabled, Flag(next.PlayEnabled)));
        Step(VTable.Call(controls, SmtcSlots.PutIsPauseEnabled, Flag(next.PauseEnabled)));
        Step(VTable.Call(controls, SmtcSlots.PutIsStopEnabled, Flag(next.StopEnabled)));
        Step(VTable.Call(controls, SmtcSlots.PutIsNextEnabled, Flag(next.NextEnabled)));
        Step(VTable.Call(controls, SmtcSlots.PutIsPreviousEnabled, Flag(next.PreviousEnabled)));
        if (previous is null || previous.HasTrack != next.HasTrack || previous.Title != next.Title || previous.Artist != next.Artist ||
            previous.AlbumArtist != next.AlbumArtist || previous.Album != next.Album || previous.ArtworkPath != next.ArtworkPath)
        {
            if (!next.HasTrack) Step(VTable.Call(updater, SmtcSlots.UpdaterClearAll));
            else
            {
                Step(VTable.Call(updater, SmtcSlots.UpdaterPutType, SmtcSlots.MediaPlaybackTypeMusic));
                Step(PutString(music, SmtcSlots.MusicPutTitle, next.Title));
                Step(PutString(music, SmtcSlots.MusicPutArtist, next.Artist));
                Step(PutString(music, SmtcSlots.MusicPutAlbumArtist, next.AlbumArtist));
                Step(PutString(music2, SmtcSlots.Music2PutAlbumTitle, next.Album));
                ApplyThumbnail(next.ArtworkPath);
            }
            Step(VTable.Call(updater, SmtcSlots.UpdaterUpdate));
        }
        // A failed update is not remembered, so the next published change rewrites every property.
        lastApplied = failure < 0 ? null : next;
        if (failure < 0) System.Diagnostics.Trace.WriteLine($"System media controls update failed: 0x{failure:X8}");
    }

    /// <summary>
    /// Sets the album art, or clears the previous one when there is none. A missing or unreadable image only costs the
    /// thumbnail: it is traced and never counts as a failed update, so the text properties are not rewritten for it.
    /// </summary>
    private void ApplyThumbnail(string path)
    {
        nint reference = 0;
        try { if (path.Length > 0) reference = CreateThumbnailReference(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            System.Diagnostics.Trace.WriteLine($"System media controls artwork skipped: {exception.Message}");
        }
        try
        {
            int hr = VTable.Call(updater, SmtcSlots.UpdaterPutThumbnail, reference);
            if (hr < 0) System.Diagnostics.Trace.WriteLine($"System media controls artwork update failed: 0x{hr:X8}");
        }
        finally
        {
            if (reference != 0) Marshal.Release(reference);
        }
    }

    // The image is copied into memory so the shell never holds a handle on the user's cover file.
    private static unsafe nint CreateThumbnailReference(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length == 0 || file.Length > MaxArtworkBytes) return 0;
        byte[] bytes = File.ReadAllBytes(path);
        nint memory, stream = 0, factory = 0;
        fixed (byte* source = bytes) memory = NativeMethods.SHCreateMemStream(source, (uint)bytes.Length);
        if (memory == 0) throw new InvalidOperationException("The artwork stream could not be created.");
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.CreateRandomAccessStreamOverStream(memory, 0, SmtcSlots.RandomAccessStreamIid, out stream));
            using HString className = HString.Create(SmtcSlots.StreamReferenceClassName);
            Marshal.ThrowExceptionForHR(NativeMethods.RoGetActivationFactory(className.Handle, SmtcSlots.StreamReferenceStaticsIid, out factory));
            Marshal.ThrowExceptionForHR(VTable.CallOut(factory, SmtcSlots.StreamReferenceCreateFromStream, stream, out nint reference));
            return reference;
        }
        finally
        {
            if (factory != 0) Marshal.Release(factory);
            if (stream != 0) Marshal.Release(stream);
            Marshal.Release(memory);
        }
    }

    private static byte Flag(bool value) => value ? (byte)1 : (byte)0;

    private static int PutString(nint instance, int slot, string value)
    {
        using HString text = HString.Create(value);
        return VTable.Call(instance, slot, text.Handle);
    }

    /// <summary>Reads back what the shell holds. The read runs on the pump thread that owns the COM objects.</summary>
    internal Task<(TransportPlayback Playback, bool PlayEnabled, string Title, bool HasThumbnail)> ReadBackAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<(TransportPlayback, bool, string, bool)> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        nint target;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            target = window;
            if (target == 0) throw new InvalidOperationException("The transport is not running.");
            requests.Enqueue(() =>
            {
                try
                {
                    if (controls == 0 || music == 0) throw new InvalidOperationException("The transport is not running.");
                    Marshal.ThrowExceptionForHR(VTable.CallOut(controls, SmtcSlots.GetPlaybackStatus, out int status));
                    Marshal.ThrowExceptionForHR(VTable.CallOut(controls, SmtcSlots.GetIsPlayEnabled, out byte play));
                    Marshal.ThrowExceptionForHR(VTable.CallOut(music, SmtcSlots.MusicGetTitle, out nint title));
                    Marshal.ThrowExceptionForHR(VTable.CallOut(updater, SmtcSlots.UpdaterGetThumbnail, out nint thumbnail));
                    if (thumbnail != 0) Marshal.Release(thumbnail);
                    try { result.TrySetResult(((TransportPlayback)status, play != 0, HString.ToManaged(title), thumbnail != 0)); }
                    finally { HString.Delete(title); }
                }
                catch (Exception exception) { result.TrySetException(exception); }
            });
        }
        if (!NativeMethods.PostMessage(target, WmRequest, 0, 0)) result.TrySetException(new InvalidOperationException("The transport is not running."));
        return result.Task.WaitAsync(cancellationToken);
    }

    private void Teardown()
    {
        handler?.Detach();
        if (controls != 0 && registered) Safely(() => Marshal.ThrowExceptionForHR(VTable.CallRemoveHandler(controls, SmtcSlots.RemoveButtonPressed, token)));
        registered = false;
        if (controls != 0) Safely(() => Marshal.ThrowExceptionForHR(VTable.Call(controls, SmtcSlots.PutIsEnabled, (byte)0)));
        if (updater != 0) Safely(() => Marshal.ThrowExceptionForHR(VTable.Call(updater, SmtcSlots.UpdaterClearAll)));
        if (updater != 0) Safely(() => Marshal.ThrowExceptionForHR(VTable.Call(updater, SmtcSlots.UpdaterUpdate)));
        Release(ref music2);
        Release(ref music);
        Release(ref updater);
        Release(ref controls);
        if (handlerPointer != 0) { ButtonPressedHandler.Release(handlerPointer); handlerPointer = 0; }
    }

    private static void Release(ref nint instance)
    {
        if (instance != 0) Marshal.Release(instance);
        instance = 0;
    }

    /// <summary>
    /// Stops the pump thread. Concurrent and repeated calls share one shutdown task, which completes after the thread
    /// exited or after a 3 second bound (a stuck shell call is traced; the background thread cannot block process exit).
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (gate) return new(shutdown ??= ShutdownAsync());
    }

    private async Task ShutdownAsync()
    {
        Thread? running;
        nint target;
        lock (gate)
        {
            disposed = true;
            running = thread;
            target = window;
        }
        handler?.Detach();
        ButtonPressed = null;
        if (running is null) return;
        if (target != 0) NativeMethods.PostMessage(target, WmQuitTransport, 0, 0);
        bool exited = await Task.Run(() => running.Join(ShutdownTimeout)).ConfigureAwait(false);
        if (!exited) System.Diagnostics.Trace.WriteLine("System media controls pump thread did not stop within the shutdown bound.");
    }
}
