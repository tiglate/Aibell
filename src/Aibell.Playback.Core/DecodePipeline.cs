namespace Aibell.Playback.Core;

/// <summary>
/// Decodes on its own thread into a ring buffer so that blocking work (files, the network, a slow codec) never runs on the audio
/// callback. The audio callback only copies frames out of the ring, and it does so without taking a lock: the callback runs on a realtime
/// thread, where waiting for another thread could cost more than the audio budget allows.
/// </summary>
internal sealed class DecodePipeline : IDisposable
{
    private const int ChunkFrames = 4096;
    private readonly IPcmSource source;
    private readonly uint sampleRate;
    private readonly AudioRingBuffer ring;
    private readonly object gate = new();
    private readonly ManualResetEventSlim ready = new(false);
    private readonly Thread producer;
    private readonly TimeStretcher stretcher = new();
    private readonly float[] stretched = new float[ChunkFrames * 8 * AudioChannels.Channels];
    private long baseFrame;
    private double sourcePosition;
    private volatile float rate = 1f;
    private bool stretching;
    private long pendingSeek = -1;
    private int epoch;
    private volatile bool endOfSource;
    private bool disposed;
    private Exception? failure;

    public DecodePipeline(IPcmSource source, uint sampleRate, long startFrame, double rate = 1.0)
    {
        this.rate = (float)rate;
        this.source = source;
        this.sampleRate = sampleRate;
        ring = new AudioRingBuffer((int)(sampleRate * 2), AudioChannels.Channels);
        baseFrame = startFrame;
        pendingSeek = startFrame > 0 ? startFrame : -1;
        producer = new Thread(Produce) { IsBackground = true, Name = "Aibell decoder" };
        producer.Start();
    }

    public TimeSpan Position
    {
        get
        {
            lock (gate) return TimeSpan.FromSeconds(SourceFrame() / sampleRate);
        }
    }

    /// <summary>True once the source is exhausted and every decoded frame has been played.</summary>
    public bool Finished
    {
        get
        {
            return endOfSource && ring.Available == 0 && Interlocked.Read(ref pendingSeek) < 0;
        }
    }

    /// <summary>Waits until about a quarter second is decoded, the source ends or decoding fails; throws the decoder's failure.</summary>
    public void WaitUntilReady(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!ready.Wait(timeout, cancellationToken)) throw new TimeoutException("The audio did not start in time.");
        lock (gate)
        {
            if (failure is { } error) throw new InvalidDataException(error.Message, error);
        }
    }

    /// <summary>The decoder's failure, if decoding stopped because of one.</summary>
    public Exception? Failure
    {
        get
        {
            lock (gate) return failure;
        }
    }

    // The source frame being heard: where decoding has got to, minus what is still waiting in the ring (in source frames).
    private double SourceFrame() => Math.Max(baseFrame, baseFrame + sourcePosition - (ring.Available * (double)rate));

    public void Seek(TimeSpan position) => SeekFrame((long)(Math.Max(0, position.TotalSeconds) * sampleRate));

    /// <summary>Changes the speed from the current position on; the frames already decoded at the old speed are dropped.</summary>
    public void SetRate(double newRate)
    {
        lock (gate)
        {
            long here = (long)SourceFrame();
            rate = (float)newRate;
            SeekFrameLocked(here);
        }
    }

    private void SeekFrame(long frame)
    {
        lock (gate) SeekFrameLocked(frame);
    }

    private void SeekFrameLocked(long frame)
    {
        pendingSeek = frame;
        epoch++;
        // The frames of the old position are dropped rather than played.
        ring.DiscardAll();
        baseFrame = frame;
        sourcePosition = 0;
        endOfSource = false;
        Monitor.PulseAll(gate);
    }

    /// <summary>
    /// Copies decoded frames into <paramref name="output"/> (called from the audio thread); the rest is filled with silence. This runs on
    /// a realtime thread, so it takes no lock and allocates nothing.
    /// </summary>
    public int Render(Span<float> output)
    {
        int copied = ring.Read(output);
        output[(copied * AudioChannels.Channels)..].Clear();
        return copied;
    }

    private void Produce()
    {
        float[] scratch = new float[ChunkFrames * AudioChannels.Channels];
        try
        {
            while (true)
            {
                long seekTo;
                int observed;
                lock (gate)
                {
                    while (!disposed && pendingSeek < 0 && endOfSource) Monitor.Wait(gate);
                    if (disposed) return;
                    seekTo = pendingSeek;
                    pendingSeek = -1;
                    observed = epoch;
                }
                if (seekTo >= 0)
                {
                    // A refused seek keeps decoding from where the source is; the position shown is then approximate.
                    source.Seek(seekTo);
                    stretcher.Reset();
                    stretching = false;
                    continue;
                }

                // Wait for room in the ring without holding the lock: the audio thread must never wait for this thread.
                bool obsolete = false;
                while (ring.Free < ChunkFrames)
                {
                    lock (gate)
                    {
                        if (disposed) return;
                        if (pendingSeek >= 0 || observed != epoch)
                        {
                            obsolete = true;
                            break;
                        }
                    }
                    Thread.Sleep(1);
                }
                if (obsolete) continue;

                int frames = source.Read(scratch, ChunkFrames);
                if (frames > 0 && !stretching && rate == 1f)
                {
                    Write(scratch, frames, observed, 1.0);
                    continue;
                }
                stretching = true;
                stretcher.Rate = rate;
                if (frames > 0) stretcher.Push(scratch, frames);
                else stretcher.Flush();
                bool current = true;
                while (current)
                {
                    int produced = stretcher.Pull(stretched);
                    if (produced == 0) break;
                    current = Write(stretched, produced, observed, rate);
                }
                if (!current) continue;
                if (frames == 0)
                {
                    lock (gate)
                    {
                        if (observed != epoch) continue;
                        endOfSource = true;
                    }
                    ready.Set();
                }
            }
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                failure = exception;
                endOfSource = true;
            }
            ready.Set();
        }
    }

    // Copies decoded frames into the ring; false when a seek or rate change made them obsolete.
    private bool Write(float[] data, int frames, int observed, double frameRate)
    {
        int done = 0;
        while (done < frames)
        {
            int written;
            lock (gate)
            {
                if (disposed || observed != epoch) return false;
                written = ring.Write(data.AsSpan(done * AudioChannels.Channels, (frames - done) * AudioChannels.Channels));
                if (written > 0)
                {
                    done += written;
                    sourcePosition += written * frameRate;
                    if (ring.Available >= sampleRate / 4) ready.Set();
                    continue;
                }
            }
            // The ring is full; wait for the audio thread to take some out.
            Thread.Sleep(1);
        }
        return true;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Monitor.PulseAll(gate);
        }
        // The producer may be blocked in a network read; the source is closed after it leaves, and never from the audio thread.
        Task.Run(() =>
        {
            producer.Join();
            try { source.Dispose(); }
            catch (Exception) { /* closing a half-read stream must not become an unobserved task failure */ }
            ready.Dispose();
        });
    }
}
