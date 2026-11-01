namespace Aibell.Audio.Mp3;

/// <summary>A small read-ahead window over a stream that can look at bytes before consuming them, skip, and jump to an absolute position.</summary>
internal sealed class Mp3ByteSource(Stream stream)
{
    private readonly byte[] buffer = new byte[16 * 1024];
    private int start;
    private int end;
    private long origin = stream.CanSeek ? stream.Position : 0;

    public long Position => origin + start;

    public bool CanSeek => stream.CanSeek;

    public ReadOnlySpan<byte> Window => buffer.AsSpan(start, end - start);

    /// <summary>Makes at least <paramref name="count"/> bytes available (at most the buffer size); false at the end of the data.</summary>
    public bool Ensure(int count)
    {
        if (end - start >= count) return true;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length);
        if (start > 0)
        {
            Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
            origin += start;
            end -= start;
            start = 0;
        }
        while (end < count)
        {
            int read = stream.Read(buffer, end, buffer.Length - end);
            if (read <= 0) return false;
            end += read;
        }
        return true;
    }

    public void Advance(int count) => start += count;

    /// <summary>Skips forward, seeking when the stream can.</summary>
    public void Skip(long count)
    {
        if (count <= end - start)
        {
            start += (int)count;
            return;
        }
        long target = Position + count;
        if (stream.CanSeek)
        {
            Seek(target);
            return;
        }
        long remaining = count - (end - start);
        start = end;
        Span<byte> scratch = stackalloc byte[512];
        while (remaining > 0)
        {
            int read = stream.Read(scratch[..(int)Math.Min(remaining, scratch.Length)]);
            if (read <= 0) break;
            remaining -= read;
            origin += read;
        }
        origin += start;
        start = end = 0;
    }

    public void Seek(long position)
    {
        stream.Seek(position, SeekOrigin.Begin);
        origin = position;
        start = end = 0;
    }
}
