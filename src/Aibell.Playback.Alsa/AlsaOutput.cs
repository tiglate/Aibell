using Aibell.Playback.Core;

namespace Aibell.Playback.Alsa;

/// <summary>
/// The ALSA playback device. A thread of its own pulls the audio and writes it to the device,
/// because the library has no callback of its own the way miniaudio has: the writing blocks until
/// the device has room, so it cannot happen on the thread that decodes.
/// </summary>
internal sealed class AlsaOutput : IDisposable
{
    /// <summary>The callback that fills a buffer with audio.</summary>
    public delegate void RenderCallback(Span<float> output);

    private readonly RenderCallback render;
    private readonly int channels;
    private readonly int format;
    private readonly int periodFrames;
    private short[]? sixteenBuffer;
    private nint handle;
    private Thread? worker;
    private volatile bool running;
    private volatile bool disposed;
    private long framesWritten;
    private Action? drainCompleted;

    /// <summary>Opens the default playback device.</summary>
    public AlsaOutput(RenderCallback render, string device = "default", uint sampleRate = 48000)
    {
        ArgumentNullException.ThrowIfNull(render);
        this.render = render;
        channels = AudioChannels.Channels;
        // A period of about twenty milliseconds is a good balance between latency and the number
        // of writes the thread makes.
        uint askedPeriod = Math.Max(64, sampleRate / 50);
        handle = AlsaNative.Open(device, sampleRate, (uint)channels, askedPeriod, out string? problem, out format, out uint actualRate, out uint actualPeriodFrames);
        if (handle == 0)
        {
            throw new InvalidOperationException(problem ?? "No sound device could be opened.");
        }
        // The device may run at another rate or period than the one that was asked for.
        SampleRate = actualRate;
        periodFrames = (int)actualPeriodFrames;
    }

    /// <summary>The rate the device runs at.</summary>
    public uint SampleRate { get; }

    /// <summary>The failure that stopped the playback, if any.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>How many frames the device has taken. Used to check that sound really went out.</summary>
    public long FramesWritten => Interlocked.Read(ref framesWritten);

    /// <summary>Starts the thread that writes the audio.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (running) return;
        if (worker is { IsAlive: true }) throw new InvalidOperationException("The previous ALSA writer has not stopped.");
        Failure = null;
        if (AlsaNative.snd_pcm_prepare(handle) < 0)
            throw new InvalidOperationException("The sound device could not be prepared.");
        running = true;
        worker = new Thread(Play) { IsBackground = true, Name = "Aibell ALSA" };
        worker.Start();
    }

    /// <summary>Stops the thread that writes the audio.</summary>
    public void Stop()
    {
        Interlocked.Exchange(ref drainCompleted, null);
        running = false;
        nint current = Volatile.Read(ref handle);
        if (current != 0) _ = AlsaNative.snd_pcm_drop(current);
        if (worker is { } thread && !thread.Join(TimeSpan.FromSeconds(2)))
            throw new TimeoutException("The ALSA writer did not stop; the device remains open.");
        worker = null;
        Interlocked.Exchange(ref drainCompleted, null);
    }

    public void Dispose()
    {
        if (disposed && handle == 0) return;
        disposed = true;
        Interlocked.Exchange(ref drainCompleted, null);
        running = false;
        nint current = Volatile.Read(ref handle);
        if (current != 0) _ = AlsaNative.snd_pcm_drop(current);
        if (worker is { } thread && !thread.Join(TimeSpan.FromSeconds(2)))
            throw new TimeoutException("The ALSA writer did not stop; the device remains open.");
        worker = null;
        nint native = Interlocked.Exchange(ref handle, 0);
        if (native == 0) return;
        _ = AlsaNative.snd_pcm_close(native);
    }

    /// <summary>The thread that pulls the audio and writes it to the device.</summary>
    private void Play()
    {
        float[] block = new float[periodFrames * channels];
        try
        {
            while (running)
            {
                render(block);
                Write(block);
                Action? completed = Interlocked.Exchange(ref drainCompleted, null);
                if (completed is not null && running)
                {
                    int result = AlsaNative.snd_pcm_drain(handle);
                    if (result < 0 && running) throw new InvalidOperationException("The sound device could not drain: " + AlsaNative.Error(result));
                    if (running) completed();
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            // The thread cannot let the exception out; the engine reads it from Failure.
            Failure = exception;
            running = false;
        }
    }

    public void RequestDrain(Action completed) => Interlocked.Exchange(ref drainCompleted, completed);

    /// <summary>
    /// Writes one buffer of audio to the device. A short write means the device is full, so the
    /// frames that did not fit are written again.
    /// </summary>
    public unsafe void Write(Span<float> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!running || handle == 0) return;

        int frames = samples.Length / channels;
        if (frames <= 0) return;

        if (format == AlsaNative.FormatFloatLe)
        {
            fixed (float* ptr = samples)
            {
                WriteFrames((nint)ptr, frames, sizeof(float) * channels);
            }
            return;
        }

        int totalSamples = frames * channels;
        if (sixteenBuffer is null || sixteenBuffer.Length < totalSamples)
        {
            sixteenBuffer = new short[totalSamples];
        }
        for (int i = 0; i < totalSamples; i++)
        {
            float value = Math.Clamp(samples[i], -1f, 1f);
            sixteenBuffer[i] = (short)Math.Round(value * short.MaxValue);
        }
        fixed (short* ptr = sixteenBuffer)
        {
            WriteFrames((nint)ptr, frames, sizeof(short) * channels);
        }
    }

    private void WriteFrames(nint buffer, int frames, int frameBytes)
    {
        int written = 0;
        while (written < frames && running)
        {
            nint current = Volatile.Read(ref handle);
            if (current == 0) return;
            nint result = AlsaNative.snd_pcm_writei(current, buffer + (written * frameBytes), (nuint)(frames - written));
            if (result < 0)
            {
                if (!running) return;
                if (result == -11) continue;
                // The device can be interrupted; recovering lets the playback carry on.
                if (AlsaNative.snd_pcm_recover(current, (int)result, 1) < 0)
                {
                    if (!running) return;
                    throw new InvalidOperationException("The sound device failed: " + AlsaNative.Error((int)result));
                }
                continue;
            }
            if (result == 0) break;
            written += (int)result;
            Interlocked.Add(ref framesWritten, result);
        }
    }
}
