using System.Buffers.Binary;
using System.Text;

namespace Aibell.Audio.Aiff.Tests;

internal static class AiffBuilder
{
    public static byte[] Build(int channels, int rate, int bits, byte[] data, string? compression = null, uint? ssndSize = null,
        uint? numSampleFrames = null, uint ssndOffset = 0, byte[]? extraChunk = null, bool ssndFirst = false)
    {
        bool aifc = compression is not null;
        int bytesPerSample = compression is "alaw" or "ALAW" or "ulaw" or "ULAW" or "raw " ? 1 : (bits + 7) / 8;
        uint frames = numSampleFrames ?? (uint)(data.Length / (channels * bytesPerSample));
        using MemoryStream output = new();
        using BinaryWriter w = new(output);
        w.Write("FORM"u8);
        w.Write(0u);
        w.Write(aifc ? "AIFC"u8 : "AIFF"u8);
        if (aifc)
        {
            w.Write("FVER"u8);
            WriteUInt32Be(w, 4);
            WriteUInt32Be(w, 0xA2805140);
        }
        if (ssndFirst) WriteSsnd(w, data, ssndSize, ssndOffset);
        WriteComm(w, channels, frames, bits, rate, compression);
        if (extraChunk is not null)
        {
            w.Write("ANNO"u8);
            WriteUInt32Be(w, (uint)extraChunk.Length);
            w.Write(extraChunk);
            if ((extraChunk.Length & 1) != 0) w.Write((byte)0);
        }
        if (!ssndFirst) WriteSsnd(w, data, ssndSize, ssndOffset);
        byte[] bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static void WriteComm(BinaryWriter w, int channels, uint frames, int bits, int rate, string? compression)
    {
        w.Write("COMM"u8);
        WriteUInt32Be(w, compression is not null ? 24u : 18u);
        WriteUInt16Be(w, (ushort)channels);
        WriteUInt32Be(w, frames);
        WriteUInt16Be(w, (ushort)bits);
        Span<byte> ext = stackalloc byte[10];
        WriteExtended80(ext, rate);
        w.Write(ext);
        if (compression is not null)
        {
            w.Write(Encoding.ASCII.GetBytes(compression));
            w.Write((byte)0);
            w.Write((byte)0);
        }
    }

    private static void WriteSsnd(BinaryWriter w, byte[] data, uint? ssndSize, uint ssndOffset)
    {
        w.Write("SSND"u8);
        uint chunkSize = ssndSize ?? (uint)(8 + ssndOffset + data.Length);
        WriteUInt32Be(w, chunkSize);
        WriteUInt32Be(w, ssndOffset);
        WriteUInt32Be(w, 0);
        if (ssndOffset > 0) w.Write(new byte[ssndOffset]);
        w.Write(data);
        if (((8 + ssndOffset + data.Length) & 1) != 0) w.Write((byte)0);
    }

    public static void WriteExtended80(Span<byte> destination, double value)
    {
        destination.Clear();
        if (value <= 0 || !double.IsFinite(value)) return;
        int exp = Math.ILogB(value);
        double norm = Math.ScaleB(value, 63 - exp);
        ulong mantissa = (ulong)Math.Round(norm);
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(exp + 16383));
        BinaryPrimitives.WriteUInt64BigEndian(destination[2..], mantissa);
    }

    public static byte[] Pcm16Be(params short[] samples)
    {
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(i * 2), samples[i]);
        return data;
    }

    public static byte[] Pcm16Le(params short[] samples)
    {
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        return data;
    }

    private static void WriteUInt16Be(BinaryWriter w, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        w.Write(b);
    }

    private static void WriteUInt32Be(BinaryWriter w, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        w.Write(b);
    }
}

/// <summary>A stream that cannot seek, like an HTTP body.</summary>
internal sealed class ForwardOnlyStream(byte[] bytes) : Stream
{
    private readonly MemoryStream inner = new(bytes);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 7));
    public override int Read(Span<byte> buffer) => inner.Read(buffer[..Math.Min(buffer.Length, 7)]);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A stream that says it can seek (like a live HTTP body) but has no length.</summary>
internal sealed class NoLengthStream(byte[] bytes) : MemoryStream(bytes)
{
    public override long Length => throw new NotSupportedException("The stream has no known length.");
}
