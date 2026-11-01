namespace Aibell.Audio.Aac;

/// <summary>
/// Cuts a byte stream into ADTS frames. Streams may start mid-frame, carry ID3 tags or contain damage, so a frame is only accepted
/// when the next header follows exactly where it should (or the stream ends). Bytes that belong to no frame are skipped and counted.
/// Memory is bounded: one buffer of a few frames, reused.
/// </summary>
public sealed class AdtsFrameReader
{
    private const int BufferSize = 4 * (AdtsHeader.MaximumFrameLength + AdtsHeader.MinimumHeaderLength);
    private readonly byte[] buffer = new byte[BufferSize];
    private int start;
    private int end;
    private int consumedFrame;
    private bool ended;
    private bool pendingGap;
    private bool locked;
    private long baseOffset;
    private int lastFrameStart;
    private int lastFrameLength;

    /// <summary>Bytes discarded since the start because they were not part of a frame.</summary>
    public long SkippedBytes { get; private set; }

    public long Frames { get; private set; }

    /// <summary>Offset, counted from the first byte ever written, of the frame most recently returned by <see cref="TryRead"/>.</summary>
    public long LastFrameOffset { get; private set; }

    /// <summary>The whole frame (header, CRC bytes and payload) most recently returned by <see cref="TryRead"/>; valid until input is next written.</summary>
    public ReadOnlySpan<byte> LastFrame => buffer.AsSpan(lastFrameStart, lastFrameLength);

    /// <summary>Forgets everything, as if new: used when the input is repositioned.</summary>
    public void Reset()
    {
        start = end = consumedFrame = 0;
        lastFrameStart = lastFrameLength = 0;
        baseOffset = 0;
        ended = pendingGap = locked = false;
    }

    /// <summary>True once after bytes were skipped or <see cref="MarkDiscontinuity"/> was called; reading it clears it.</summary>
    public bool TakeGap()
    {
        bool gap = pendingGap;
        pendingGap = false;
        return gap;
    }

    /// <summary>Free space for the caller to write input into; call <see cref="Advance"/> afterwards.</summary>
    public Span<byte> GetWriteBuffer()
    {
        Compact();
        return buffer.AsSpan(end);
    }

    public void Advance(int count)
    {
        if (count < 0 || end + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        end += count;
    }

    /// <summary>Signals that no more input will come, so a final frame need not be confirmed by a following header.</summary>
    public void Complete() => ended = true;

    /// <summary>The input had a hole (lost packets, a segment join): the partial frame in the buffer cannot be trusted.</summary>
    public void MarkDiscontinuity()
    {
        // Drop everything that is not yet a confirmed frame; the next sync search starts fresh.
        SkippedBytes += end - start;
        baseOffset += end;
        start = end = consumedFrame = 0;
        pendingGap = true;
        locked = false;
    }

    /// <summary>
    /// Finds the next frame. Returns false when more input is needed (or, after <see cref="Complete"/>, when the data is exhausted).
    /// The returned payload is the raw data block(s), valid until the next call that writes input.
    /// </summary>
    public bool TryRead(out AdtsHeader header, out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;
        if (consumedFrame > 0)
        {
            start += consumedFrame;
            consumedFrame = 0;
        }
        while (true)
        {
            ReadOnlySpan<byte> window = buffer.AsSpan(start, end - start);
            if (window.Length == 0) return false;
            int tag = Id3.TagLength(window);
            if (tag > 0)
            {
                // A tag is skipped whole, even when it is larger than the buffer; if it is not all here, wait.
                if (tag <= window.Length) { start += tag; continue; }
                if (!ended && window.Length < buffer.Length) return false;
                Discard(window.Length);
                continue;
            }
            if (window.Length < 10 && !ended && "ID3"u8.StartsWith(window[..Math.Min(3, window.Length)]) && window[0] == 'I') return false;
            if (window[0] != 0xff)
            {
                int next = window.IndexOf((byte)0xff);
                Discard(next < 0 ? window.Length : next);
                continue;
            }
            if (window.Length < AdtsHeader.MinimumHeaderLength + 2 && !ended) return false;
            if (!AdtsHeader.TryParse(window, out AdtsHeader candidate)) { Discard(1); continue; }
            if (window.Length < candidate.FrameLength)
            {
                if (ended) { Discard(window.Length); continue; }
                return false;
            }
            // Confirm with the following header so a stray 0xFFF in the payload is not taken for a frame.
            // Directly after an accepted frame the stream is in sync, so that frame needs no such confirmation.
            ReadOnlySpan<byte> after = window[candidate.FrameLength..];
            if (locked) { }
            else if (after.Length >= AdtsHeader.MinimumHeaderLength)
            {
                bool followed = AdtsHeader.TryParse(after, out AdtsHeader following) && following.SamplingIndex == candidate.SamplingIndex
                    || Id3.TagLength(after) > 0;
                if (!followed) { Discard(1); continue; }
            }
            else if (!ended) return false;
            header = candidate;
            payload = window.Slice(candidate.HeaderLength, candidate.PayloadLength);
            consumedFrame = candidate.FrameLength;
            LastFrameOffset = baseOffset + start;
            lastFrameStart = start;
            lastFrameLength = candidate.FrameLength;
            locked = true;
            Frames++;
            return true;
        }
    }

    private void Discard(int count)
    {
        locked = false;
        start += count;
        SkippedBytes += count;
        pendingGap = true;
    }

    private void Compact()
    {
        if (consumedFrame > 0)
        {
            start += consumedFrame;
            consumedFrame = 0;
        }
        if (start == 0) return;
        Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
        baseOffset += start;
        end -= start;
        start = 0;
    }
}
