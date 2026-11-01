using System.Buffers.Binary;

namespace Aibell.Audio.Wav.Tests;

internal static class WavBuilder
{
    public static byte[] Build(ushort tag, int channels, int rate, int bits, byte[] data, bool extensible = false, bool rf64 = false, uint? dataSize = null, byte[]? extraChunk = null)
    {
        using MemoryStream output = new();
        using BinaryWriter w = new(output);
        w.Write(rf64 ? "RF64"u8 : "RIFF"u8);
        w.Write(rf64 ? uint.MaxValue : 0u);
        w.Write("WAVE"u8);
        if (rf64)
        {
            w.Write("ds64"u8);
            w.Write(28u);
            w.Write(0UL);
            w.Write((ulong)data.Length);
            w.Write((ulong)(data.Length / (channels * bits / 8)));
            w.Write(0u);
        }
        w.Write("fmt "u8);
        w.Write(extensible ? 40u : 16u);
        w.Write(extensible ? (ushort)0xFFFE : tag);
        w.Write((ushort)channels);
        w.Write((uint)rate);
        w.Write((uint)(rate * channels * bits / 8));
        w.Write((ushort)(channels * bits / 8));
        w.Write((ushort)bits);
        if (extensible)
        {
            w.Write((ushort)22);
            w.Write((ushort)bits);
            w.Write(3u);
            w.Write(tag);
            w.Write(new byte[] { 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 });
        }
        if (extraChunk is not null)
        {
            w.Write("LIST"u8);
            w.Write((uint)extraChunk.Length);
            w.Write(extraChunk);
            if ((extraChunk.Length & 1) != 0) w.Write((byte)0);
        }
        w.Write("data"u8);
        w.Write(dataSize ?? (rf64 ? uint.MaxValue : (uint)data.Length));
        w.Write(data);
        if ((data.Length & 1) != 0) w.Write((byte)0);
        byte[] bytes = output.ToArray();
        if (!rf64) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    public static byte[] Pcm16(params short[] samples)
    {
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        return data;
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
