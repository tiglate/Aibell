namespace Aibell.Playback.Core;

/// <summary>
/// A ring buffer for audio frames that one thread fills and another empties, without a lock
/// between them.
///
/// The audio thread must never wait for another thread, so the two sides only exchange counters:
/// the producer publishes how much it has written and the reader publishes how much it has taken.
/// The data is written before the counter that says it is there, and the counters are read and
/// written with the ordering the runtime guarantees for them, so the reader never sees a frame
/// before its contents.
/// </summary>
internal sealed class AudioRingBuffer
{
    private readonly float[] buffer;
    private readonly int capacityFrames;
    private readonly int channels;

    // Written by the producer, read by the reader.
    private long produced;

    // Written by the reader, read by the producer.
    private long consumed;

    // The producer asks the reader to drop what is in the buffer; -1 means there is nothing to drop.
    private long discardBefore = -1;

    // The reader says it has dropped what was asked of it.
    private int discardDone;

    public AudioRingBuffer(int capacityFrames, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        this.capacityFrames = capacityFrames;
        this.channels = channels;
        buffer = new float[capacityFrames * channels];
    }

    /// <summary>How many frames fit in the buffer.</summary>
    public int Capacity => capacityFrames;

    /// <summary>The frames waiting to be read.</summary>
    public int Available
    {
        get
        {
            long write = Volatile.Read(ref produced);
            return (int)(write - EffectiveConsumed(write));
        }
    }

    /// <summary>The frames that can still be written.</summary>
    public int Free => capacityFrames - (int)(Volatile.Read(ref produced) - Volatile.Read(ref consumed));

    /// <summary>
    /// Writes as many of the frames as fit and returns how many were taken. The producer keeps
    /// the rest for the next call.
    /// </summary>
    public int Write(ReadOnlySpan<float> frames)
    {
        int count = frames.Length / channels;
        if (count <= 0) return 0;

        long write = Volatile.Read(ref produced);
        int free = capacityFrames - (int)(write - Volatile.Read(ref consumed));
        if (free <= 0) return 0;
        if (count > free) count = free;

        int offset = (int)(write % capacityFrames);
        int first = Math.Min(count, capacityFrames - offset);
        frames[..(first * channels)].CopyTo(buffer.AsSpan(offset * channels));
        if (first < count)
        {
            frames[(first * channels)..(count * channels)].CopyTo(buffer.AsSpan(0));
        }

        // The data is in place before the count that says it is there.
        Volatile.Write(ref produced, write + count);
        return count;
    }

    /// <summary>Reads as many of the frames as are there and returns how many were taken.</summary>
    public int Read(Span<float> destination)
    {
        int count = destination.Length / channels;
        if (count <= 0) return 0;

        ApplyDiscard();

        long read = Volatile.Read(ref consumed);
        int available = (int)(Volatile.Read(ref produced) - read);
        if (available <= 0) return 0;
        if (count > available) count = available;

        int offset = (int)(read % capacityFrames);
        int first = Math.Min(count, capacityFrames - offset);
        buffer.AsSpan(offset * channels, first * channels).CopyTo(destination);
        if (first < count)
        {
            buffer.AsSpan(0, (count - first) * channels).CopyTo(destination[(first * channels)..]);
        }

        Volatile.Write(ref consumed, read + count);
        return count;
    }

    /// <summary>
    /// Asks the reader to drop everything that is in the buffer now. A seek uses this so the
    /// frames of the old position are not played after it.
    /// </summary>
    public void DiscardAll()
    {
        Volatile.Write(ref discardBefore, Volatile.Read(ref produced));
        Volatile.Write(ref discardDone, 0);
    }

    /// <summary>
    /// Waits a short while for the reader to drop what was asked of it. The producer uses this so
    /// the frames of the old position are gone before the new ones arrive. It gives up after the
    /// timeout so a reader that has stopped cannot hold the producer up.
    /// </summary>
    public bool WaitForDiscard(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref discardDone) == 0)
        {
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(1);
        }
        return true;
    }

    /// <summary>The read position once any pending discard has been accounted for.</summary>
    private long EffectiveConsumed(long write)
    {
        long read = Volatile.Read(ref consumed);
        long target = Volatile.Read(ref discardBefore);
        if (target <= read) return read;
        return Math.Min(target, write);
    }

    /// <summary>Drops the frames the producer asked to have dropped.</summary>
    private void ApplyDiscard()
    {
        long target = Volatile.Read(ref discardBefore);
        if (target < 0) return;
        long read = Volatile.Read(ref consumed);
        if (read >= target)
        {
            Volatile.Write(ref discardDone, 1);
            return;
        }

        // Only drop what has actually been produced, so the reader never runs ahead of the writer.
        long limit = Volatile.Read(ref produced);
        long drop = Math.Min(target, limit);
        if (drop > read)
        {
            Volatile.Write(ref consumed, drop);
        }
        Volatile.Write(ref discardDone, 1);
    }
}
