using System.Buffers.Binary;

namespace Aibell.Audio.Ogg;

[Flags]
public enum OggPageKind : byte
{
    None = 0,
    Continued = 1,
    BeginOfStream = 2,
    EndOfStream = 4
}

/// <summary>The fixed fields of one Ogg page (RFC 3533 section 6).</summary>
public readonly record struct OggPageHeader(OggPageKind Flags, long Granule, uint Serial, uint Sequence, int SegmentCount, int BodyLength, long Offset, int HeaderLength)
{
    public int PageLength => HeaderLength + BodyLength;

    public bool IsContinued => (Flags & OggPageKind.Continued) != 0;

    public bool IsFirst => (Flags & OggPageKind.BeginOfStream) != 0;

    public bool IsLast => (Flags & OggPageKind.EndOfStream) != 0;
}

/// <summary>
/// Reads Ogg pages from a stream, finding each "OggS" capture pattern and rejecting pages whose CRC-32 does not match (the search then goes on a byte
/// further, which resynchronizes after damage or junk). One fixed buffer is reused; the spans a call returns are valid until the next call.
/// </summary>
public sealed class OggPageReader
{
    public const int MaximumPageLength = 27 + 255 + 255 * 255;
    private const int BufferLength = 4 * MaximumPageLength;

    private readonly Stream stream;
    private readonly byte[] buffer = new byte[BufferLength];
    private int start;
    private int end;
    private long bufferOrigin;
    private bool endOfInput;
    private readonly long length;
    private OggPageHeader current;

    public OggPageReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
        // A stream that claims to seek but has no length (a live http body) is treated as forward-only.
        if (!StreamLength.TryGet(stream, out length)) length = -1;
        bufferOrigin = length >= 0 ? stream.Position : 0;
    }

    public bool CanSeek => length >= 0;

    public long Length => length;

    /// <summary>The stream offset of the first byte not yet consumed.</summary>
    public long Position => bufferOrigin + start;

    public OggPageHeader Header => current;

    /// <summary>The segment (lacing) table of the page last read.</summary>
    public ReadOnlySpan<byte> Segments => buffer.AsSpan(PageStartInBuffer + 27, current.SegmentCount);

    /// <summary>The body of the page last read.</summary>
    public ReadOnlySpan<byte> Body => buffer.AsSpan(PageStartInBuffer + current.HeaderLength, current.BodyLength);

    private int PageStartInBuffer => start - current.PageLength;

    /// <summary>Moves to <paramref name="offset"/> and forgets buffered data. False when the stream cannot seek.</summary>
    public bool Seek(long offset)
    {
        if (length < 0) return false;
        stream.Seek(offset, SeekOrigin.Begin);
        bufferOrigin = offset;
        start = end = 0;
        endOfInput = false;
        current = default;
        return true;
    }

    /// <summary>Reads the next intact page. False at the end of the stream.</summary>
    public bool ReadPage()
    {
        while (true)
        {
            Fill(27);
            int available = end - start;
            if (available < 27) return false;
            int at = FindCapture(buffer.AsSpan(start, available));
            if (at < 0)
            {
                // Keep the last three bytes: they may be the start of a capture pattern split across reads.
                start = end - 3 > start ? end - 3 : start;
                if (endOfInput) return false;
                continue;
            }
            start += at;
            Fill(27 + 255);
            available = end - start;
            if (available < 27) return false;
            ReadOnlySpan<byte> head = buffer.AsSpan(start, available);
            if (head[4] != 0)
            {
                start++;
                continue;
            }
            int segments = head[26];
            int headerLength = 27 + segments;
            if (available < headerLength)
            {
                if (endOfInput) return false;
                continue;
            }
            int body = 0;
            for (int i = 0; i < segments; i++) body += head[27 + i];
            int length = headerLength + body;
            Fill(length);
            available = end - start;
            if (available < length)
            {
                // A truncated final page: it cannot be verified, so it ends the stream.
                if (endOfInput) return false;
                continue;
            }
            ReadOnlySpan<byte> page = buffer.AsSpan(start, length);
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(page[22..]);
            uint crc = Crc.Crc32Ogg(page[..22]);
            crc = Crc.Crc32Ogg([0, 0, 0, 0], crc);
            crc = Crc.Crc32Ogg(page[26..], crc);
            if (crc != stored)
            {
                start++;
                continue;
            }
            current = new OggPageHeader((OggPageKind)page[5], BinaryPrimitives.ReadInt64LittleEndian(page[6..]), BinaryPrimitives.ReadUInt32LittleEndian(page[14..]),
                BinaryPrimitives.ReadUInt32LittleEndian(page[18..]), segments, body, bufferOrigin + start, headerLength);
            start += length;
            return true;
        }
    }

    private static int FindCapture(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        while (offset + 4 <= data.Length)
        {
            int at = data[offset..].IndexOf((byte)'O');
            if (at < 0 || offset + at + 4 > data.Length) return -1;
            offset += at;
            if (data[offset + 1] == (byte)'g' && data[offset + 2] == (byte)'g' && data[offset + 3] == (byte)'S') return offset;
            offset++;
        }
        return -1;
    }

    // Makes `minimum` bytes available from `start` when the stream has them.
    private void Fill(int minimum)
    {
        if (end - start >= minimum || endOfInput) return;
        if (start > 0 && buffer.Length - start < minimum + 4096)
        {
            Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
            bufferOrigin += start;
            end -= start;
            start = 0;
        }
        while (end - start < minimum)
        {
            int read = stream.Read(buffer, end, buffer.Length - end);
            if (read <= 0)
            {
                endOfInput = true;
                return;
            }
            end += read;
        }
    }
}
