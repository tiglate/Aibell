using System.Runtime.InteropServices;
using Aibell.Playback.Core;

namespace Aibell.Playback.PipeWire;

/// <summary>
/// A playback stream to the PipeWire daemon: it negotiates an audio format with the daemon and
/// hands the audio over through shared memory buffers on the realtime process callback.
///
/// The audio itself is produced by the decoder on its own thread; this class only carries it to
/// the daemon, so the callback that fills the buffer must not block or wait.
/// </summary>
internal sealed unsafe class PipeWireStream : IDisposable
{
    /// <summary>The callback that fills a buffer with audio.</summary>
    public delegate void RenderCallback(Span<float> output);

    /// <summary>How many frames to write when the daemon does not ask for a specific quantum.</summary>
    private const int BufferFrames = 1024;

    private static int initialized;

    private readonly RenderCallback render;
    private readonly object lifetimeGate = new();
    private readonly ManualResetEventSlim readyGate = new(false);
    private GCHandle selfHandle;
    private nint eventsPtr;
    private nint threadLoop;
    private nint stream;
    private volatile bool running;
    private volatile bool disposed;
    private volatile Exception? failure;
    private long framesWritten;
    private Action? drainCompleted;
    private int drainRequested;
    private int drainGeneration;
    private int draining;

    /// <summary>Opens a stream that plays through the daemon's default sink.</summary>
    public PipeWireStream(RenderCallback render, string nodeName = "Aibell")
    {
        ArgumentNullException.ThrowIfNull(render);
        this.render = render;

        string? socketPath = PipeWireRuntime.SocketPath();
        if (socketPath is null || !File.Exists(socketPath))
        {
            throw new IOException("There is no PipeWire socket to connect to.");
        }

        EnsureInitialized();

        bool succeeded = false;
        try
        {
            selfHandle = GCHandle.Alloc(this);
            NativeMethods.PwStreamEvents* events = (NativeMethods.PwStreamEvents*)NativeMemory.AllocZeroed((nuint)sizeof(NativeMethods.PwStreamEvents));
            events->Version = NativeMethods.PwVersionStreamEvents;
            events->StateChanged = (nint)(delegate* unmanaged<nint, int, int, nint, void>)&OnStateChanged;
            events->ParamChanged = (nint)(delegate* unmanaged<nint, uint, nint, void>)&OnParamChanged;
            events->Process = (nint)(delegate* unmanaged<nint, void>)&OnProcess;
            events->Drained = (nint)(delegate* unmanaged<nint, void>)&OnDrained;
            eventsPtr = (nint)events;

            threadLoop = NativeMethods.PwThreadLoopNew(nodeName, 0);
            if (threadLoop == 0)
            {
                throw new IOException("The PipeWire thread loop could not be created.");
            }

            nint loop = NativeMethods.PwThreadLoopGetLoop(threadLoop);
            nint props = NativeMethods.PwPropertiesNewString(
                "media.type=Audio media.category=Playback media.role=Music node.autoconnect=true");
            stream = NativeMethods.PwStreamNewSimple(loop, nodeName, props, eventsPtr, GCHandle.ToIntPtr(selfHandle));
            if (stream == 0)
            {
                throw new IOException("The PipeWire stream could not be created.");
            }

            byte[] formatBytes = FormatPod(new AudioFormat(SampleRate, (uint)Channels, SpaAudioFormat.F32Le), SpaParams.EnumFormat).Write();
            fixed (byte* formatPtr = formatBytes)
            {
                nint paramPtr = (nint)formatPtr;
                uint flags = NativeMethods.PwStreamFlagAutoconnect
                    | NativeMethods.PwStreamFlagMapBuffers
                    | NativeMethods.PwStreamFlagRtProcess;
                int connectResult = NativeMethods.PwStreamConnect(
                    stream,
                    NativeMethods.PwDirectionOutput,
                    NativeMethods.PwIdAny,
                    flags,
                    &paramPtr,
                    1);
                if (connectResult < 0)
                {
                    throw new IOException($"The PipeWire stream could not connect (error {connectResult}).");
                }
            }

            if (NativeMethods.PwThreadLoopStart(threadLoop) < 0)
            {
                throw new IOException("The PipeWire thread loop could not be started.");
            }

            succeeded = true;
        }
        finally
        {
            if (!succeeded)
            {
                Cleanup();
            }
        }
    }

    /// <summary>The rate the daemon and this stream agreed on.</summary>
    public uint SampleRate { get; private set; } = FormatChooser.PreferredRate;

    /// <summary>How many channels the stream carries.</summary>
    public static int Channels => AudioChannels.Channels;

    /// <summary>The failure that stopped the stream, if any.</summary>
    public Exception? Failure => failure;

    /// <summary>True once the stream is ready to play.</summary>
    public bool Ready { get; private set; }

    /// <summary>How many frames the daemon has taken. Used to check that sound really went out.</summary>
    public long FramesWritten => Interlocked.Read(ref framesWritten);

    /// <summary>
    /// Waits for the daemon to bind the node and for the format to be negotiated, so the caller
    /// knows the rate to decode at.
    /// </summary>
    public bool WaitUntilReady(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (failure is { } error)
            {
                throw new InvalidOperationException("The PipeWire stream could not be set up.", error);
            }
            if (Ready) return true;
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            readyGate.Wait(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50));
        }
        if (failure is { } finalError)
        {
            throw new InvalidOperationException("The PipeWire stream could not be set up.", finalError);
        }
        if (Ready) return true;
        throw new TimeoutException("The PipeWire stream was not ready in time.");
    }

    /// <summary>Starts filling the shared buffer.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (running) return;
        running = true;
        if (threadLoop != 0 && stream != 0)
        {
            NativeMethods.PwThreadLoopLock(threadLoop);
            try
            {
                _ = NativeMethods.PwStreamSetActive(stream, true);
            }
            finally
            {
                NativeMethods.PwThreadLoopUnlock(threadLoop);
            }
        }
    }

    /// <summary>Stops filling the shared buffer and pauses the PipeWire stream.</summary>
    public void Stop()
    {
        Interlocked.Exchange(ref drainCompleted, null);
        Interlocked.Exchange(ref drainRequested, 0);
        Interlocked.Increment(ref drainGeneration);
        if (!running) return;
        running = false;
        if (threadLoop != 0 && stream != 0)
        {
            NativeMethods.PwThreadLoopLock(threadLoop);
            try
            {
                _ = NativeMethods.PwStreamSetActive(stream, false);
            }
            finally
            {
                NativeMethods.PwThreadLoopUnlock(threadLoop);
            }
        }
    }

    public void RequestDrain(Action completed)
    {
        Interlocked.Exchange(ref drainCompleted, completed);
        Volatile.Write(ref drainRequested, 1);
    }

    public void CancelDrain()
    {
        Interlocked.Exchange(ref drainCompleted, null);
        Interlocked.Exchange(ref drainRequested, 0);
        Interlocked.Increment(ref drainGeneration);
        if (threadLoop == 0 || stream == 0) return;
        NativeMethods.PwThreadLoopLock(threadLoop);
        try
        {
            if (Interlocked.Exchange(ref draining, 0) != 0)
                _ = NativeMethods.PwStreamFlush(stream, false);
        }
        finally { NativeMethods.PwThreadLoopUnlock(threadLoop); }
    }

    public void Dispose()
    {
        lock (lifetimeGate)
        {
            if (disposed) return;
            disposed = true;
            running = false;
            Cleanup();
        }
    }

    private void Cleanup()
    {
        if (threadLoop != 0)
        {
            NativeMethods.PwThreadLoopStop(threadLoop);
        }
        if (stream != 0)
        {
            NativeMethods.PwStreamDestroy(stream);
            stream = 0;
        }
        if (threadLoop != 0)
        {
            NativeMethods.PwThreadLoopDestroy(threadLoop);
            threadLoop = 0;
        }
        if (eventsPtr != 0)
        {
            NativeMemory.Free((void*)eventsPtr);
            eventsPtr = 0;
        }
        if (selfHandle.IsAllocated)
        {
            selfHandle.Free();
        }
        readyGate.Dispose();
    }

    private static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref initialized, 1) == 0)
        {
            NativeMethods.PwInit(0, 0);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnStateChanged(nint data, int oldState, int state, nint error)
    {
        if (data == 0) return;
        if (GCHandle.FromIntPtr(data).Target is not PipeWireStream self || self.disposed) return;

        if (state == NativeMethods.PwStreamStateError)
        {
            string message = error != 0
                ? Marshal.PtrToStringUTF8(error) ?? "The PipeWire stream failed."
                : "The PipeWire stream failed.";
            self.failure = new IOException(message);
            self.readyGate.Set();
        }
        else if (state is NativeMethods.PwStreamStatePaused or NativeMethods.PwStreamStateStreaming)
        {
            self.Ready = true;
            self.readyGate.Set();
        }
    }

    [UnmanagedCallersOnly]
    private static void OnParamChanged(nint data, uint id, nint param)
    {
        if (data == 0 || param == 0 || id != SpaParams.Format) return;
        if (GCHandle.FromIntPtr(data).Target is not PipeWireStream self || self.disposed) return;

        try
        {
            uint bodySize = *(uint*)param;
            int totalBytes = SpaPod.AllocatedSize((int)bodySize);
            ReadOnlySpan<byte> raw = new((void*)param, totalBytes);
            SpaPod pod = SpaPod.Read(raw);
            foreach (SpaProperty prop in pod.Properties)
            {
                if (prop.Key == SpaFormatKeys.Rate && prop.Value.Type == SpaPodType.Int)
                {
                    int rate = prop.Value.AsInt();
                    if (rate > 0) self.SampleRate = (uint)rate;
                }
            }
        }
        catch
        {
            // Keep the preferred rate if the daemon's format POD cannot be parsed.
        }
    }

    [UnmanagedCallersOnly]
    private static void OnDrained(nint data)
    {
        try
        {
            if (data != 0 && GCHandle.FromIntPtr(data).Target is PipeWireStream self && !self.disposed)
            {
                Interlocked.Exchange(ref self.draining, 0);
                Interlocked.Exchange(ref self.drainCompleted, null)?.Invoke();
            }
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly]
    private static void OnProcess(nint data)
    {
        if (data == 0) return;
        if (GCHandle.FromIntPtr(data).Target is not PipeWireStream self || self.disposed || self.stream == 0) return;

        NativeMethods.PwBuffer* pwBuffer = NativeMethods.PwStreamDequeueBuffer(self.stream);
        if (pwBuffer == null) return;

        try
        {
            NativeMethods.SpaBuffer* spaBuffer = pwBuffer->Buffer;
            if (spaBuffer == null || spaBuffer->NDatas == 0 || spaBuffer->Datas == null) return;

            NativeMethods.SpaData* spaData = &spaBuffer->Datas[0];
            if (spaData->Data == 0 || spaData->Chunk == null) return;

            int stride = Channels * sizeof(float);
            uint maxFrames = spaData->MaxSize / (uint)stride;
            if (maxFrames == 0) return;

            uint requested = pwBuffer->Requested > 0
                ? (uint)Math.Min(pwBuffer->Requested, maxFrames)
                : Math.Min(maxFrames, (uint)BufferFrames);

            Span<float> destination = new((void*)spaData->Data, (int)(requested * (uint)Channels));
            if (self.running)
            {
                try { self.render(destination); }
                catch (Exception exception)
                {
                    destination.Clear();
                    self.failure = exception;
                    self.running = false;
                }
                Interlocked.Add(ref self.framesWritten, requested);
            }
            else
            {
                destination.Clear();
            }

            spaData->Chunk->Offset = 0;
            spaData->Chunk->Stride = stride;
            spaData->Chunk->Size = requested * (uint)stride;
            spaData->Chunk->Flags = 0;
        }
        catch (Exception exception)
        {
            self.failure = exception;
            self.running = false;
        }
        finally
        {
            _ = NativeMethods.PwStreamQueueBuffer(self.stream, pwBuffer);
        }
        if (Interlocked.Exchange(ref self.drainRequested, 0) != 0)
        {
            int generation = Volatile.Read(ref self.drainGeneration);
            _ = Task.Run(() => self.BeginDrain(generation));
        }
    }

    private void BeginDrain(int generation)
    {
        lock (lifetimeGate)
        {
            if (disposed || !running || generation != Volatile.Read(ref drainGeneration) || Volatile.Read(ref drainCompleted) is null) return;
            NativeMethods.PwThreadLoopLock(threadLoop);
            try
            {
                if (!disposed && running && generation == Volatile.Read(ref drainGeneration) && Volatile.Read(ref drainCompleted) is not null)
                {
                    int result = NativeMethods.PwStreamFlush(stream, true);
                    if (result < 0) failure = new IOException($"PipeWire could not drain the stream (error {result}).");
                    else Volatile.Write(ref draining, 1);
                }
            }
            finally { NativeMethods.PwThreadLoopUnlock(threadLoop); }
        }
    }

    /// <summary>Builds the format the client asks the daemon for.</summary>
    internal static SpaPod FormatPod(AudioFormat format, uint paramId = SpaParams.Format) =>
        SpaPod.Object(SpaObjectType.Format, paramId,
            new SpaProperty(SpaFormatKeys.MediaType, SpaPropFlags.None, SpaPod.Id(SpaMediaType.Audio)),
            new SpaProperty(SpaFormatKeys.MediaSubtype, SpaPropFlags.None, SpaPod.Id(SpaMediaSubtype.Raw)),
            new SpaProperty(SpaFormatKeys.Format, SpaPropFlags.None, SpaPod.Id(format.Format)),
            new SpaProperty(SpaFormatKeys.Rate, SpaPropFlags.None, SpaPod.Int((int)format.SampleRate)),
            new SpaProperty(SpaFormatKeys.Channels, SpaPropFlags.None, SpaPod.Int((int)format.Channels)),
            new SpaProperty(SpaFormatKeys.Position, SpaPropFlags.None,
                SpaPod.ArrayPod(SpaPod.Id(0), SpaPod.Id(SpaAudioPosition.FrontLeft), SpaPod.Id(SpaAudioPosition.FrontRight))));

    /// <summary>Reads the formats out of the daemon's answer.</summary>
    internal static List<AudioFormat> ReadFormats(SpaPod param)
    {
        List<AudioFormat> formats = [];
        foreach (SpaPod child in param.Children)
        {
            if (child.Type != SpaPodType.Object) continue;
            uint rate = 0, channels = 0;
            foreach (SpaProperty property in child.Properties)
            {
                if ((property.Key == SpaFormatKeys.Rate || property.Key == SpaParamKey.Rate) && property.Value.Type == SpaPodType.Int)
                    rate = (uint)property.Value.AsInt();
                if ((property.Key == SpaFormatKeys.Channels || property.Key == SpaParamKey.Channels) && property.Value.Type == SpaPodType.Int)
                    channels = (uint)property.Value.AsInt();
            }
            if (rate > 0 && channels > 0) formats.Add(new AudioFormat(rate, channels, SpaAudioFormat.F32Le));
        }
        return formats;
    }
}
