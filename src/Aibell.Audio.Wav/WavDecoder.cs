using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Aibell.Audio.Wav;

/// <summary>
/// RIFF/WAVE and RF64 reader: integer PCM (8 to 32 bits), IEEE float (32 and 64 bits), A-law and mu-law, including
/// WAVE_FORMAT_EXTENSIBLE. Works on forward-only streams (an unknown data size runs to the end of the stream) and seeks exactly when the
/// stream can seek. Format reference: Microsoft's WAVE format documentation, EBU Tech 3306 (RF64) and ITU-T G.711.
/// </summary>
public sealed class WavDecoder : IAudioDecoder
{
    private const int RawBufferBytes = 32 * 1024;
    private const uint UnknownSize = 0xFFFFFFFF;

    private readonly Stream stream;
    private readonly int blockAlign;
    private readonly SampleKind kind;
    private readonly byte[] raw;
    private readonly long dataStart;
    private long dataRemaining;
    private readonly bool sizeKnown;
    private readonly bool canSeek;
    private int rawLength;
    private int rawOffset;
    private bool disposed;

    private enum SampleKind { Unsigned8, Int16, Int24, Int32, Float32, Float64, ALaw, MuLaw }

    private WavDecoder(Stream stream, int sampleRate, int channels, int blockAlign, SampleKind kind, long dataStart, long dataLength, bool sizeKnown)
    {
        this.stream = stream;
        SampleRate = sampleRate;
        Channels = channels;
        this.blockAlign = blockAlign;
        this.kind = kind;
        this.dataStart = dataStart;
        dataRemaining = dataLength;
        this.sizeKnown = sizeKnown;
        raw = new byte[RawBufferBytes / blockAlign * blockAlign];
        canSeek = StreamLength.TryGet(stream, out long streamLength);
        TotalFrames = sizeKnown ? dataLength / blockAlign : canSeek ? (streamLength - dataStart) / blockAlign : -1;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public long TotalFrames { get; }

    /// <summary>True when the bytes start a RIFF/WAVE or RF64 file.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head) =>
        head.Length >= 12 && (head[..4].SequenceEqual("RIFF"u8) || head[..4].SequenceEqual("RF64"u8)) && head.Slice(8, 4).SequenceEqual("WAVE"u8);

    /// <summary>Reads the header and leaves the stream at the first sample. Throws <see cref="InvalidDataException"/> or <see cref="NotSupportedException"/>.</summary>
    public static WavDecoder Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[12];
        ReadExactly(stream, header);
        if (!Sniff(header)) throw new InvalidDataException("This is not a WAVE file.");
        bool rf64 = header[0] == (byte)'R' && header[1] == (byte)'F' && header[2] == (byte)'6';
        long position = 12;
        bool haveFormat = false;
        ushort tag = 0;
        int channels = 0, sampleRate = 0, align = 0, bits = 0;
        long ds64DataSize = -1;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> body = stackalloc byte[40];
        while (true)
        {
            if (!TryReadExactly(stream, chunk)) throw new InvalidDataException("The WAVE file has no audio data.");
            position += 8;
            uint size32 = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long size = size32;
            ReadOnlySpan<byte> id = chunk[..4];
            if (id.SequenceEqual("data"u8))
            {
                if (!haveFormat) throw new InvalidDataException("The WAVE data comes before its format.");
                bool known = true;
                if (size32 == UnknownSize && rf64 && ds64DataSize >= 0) size = ds64DataSize;
                else if (size32 == UnknownSize || size32 == 0) { known = false; size = long.MaxValue; }
                if (known && StreamLength.TryGet(stream, out long available)) size = Math.Min(size, available - position);
                return Build(stream, tag, channels, sampleRate, align, bits, position, size, known);
            }
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || size > 4096) throw new InvalidDataException("The WAVE format chunk is invalid.");
                int toRead = (int)Math.Min(size, body.Length);
                body.Clear();
                ReadExactly(stream, body[..toRead]);
                Skip(stream, size - toRead);
                tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                align = BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (tag == 0xFFFE)
                {
                    if (size < 40) throw new InvalidDataException("The WAVE extensible format chunk is too short.");
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
                }
                haveFormat = true;
                position += size;
            }
            else if (id.SequenceEqual("ds64"u8) && size >= 24)
            {
                ReadExactly(stream, body[..24]);
                Skip(stream, size - 24);
                ds64DataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
                position += size;
            }
            else
            {
                Skip(stream, size);
                position += size;
            }
            if ((size & 1) != 0 && !id.SequenceEqual("data"u8))
            {
                Skip(stream, 1);
                position++;
            }
        }
    }

    public int Read(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int produced = 0;
        while (produced < frames)
        {
            if (rawOffset >= rawLength && !Refill()) break;
            int available = (rawLength - rawOffset) / blockAlign;
            int count = Math.Min(available, frames - produced);
            Convert(raw.AsSpan(rawOffset, count * blockAlign), destination.Slice(produced * Channels, count * Channels));
            rawOffset += count * blockAlign;
            produced += count;
        }
        return produced;
    }

    public bool Seek(long frame)
    {
        if (!canSeek || frame < 0) return false;
        if (TotalFrames >= 0 && frame > TotalFrames) frame = TotalFrames;
        long target = dataStart + frame * blockAlign;
        stream.Seek(target, SeekOrigin.Begin);
        dataRemaining = sizeKnown ? (TotalFrames - frame) * blockAlign : long.MaxValue;
        rawLength = rawOffset = 0;
        return true;
    }

    public void Dispose()
    {
        disposed = true;
        stream.Dispose();
    }

    private bool Refill()
    {
        rawOffset = rawLength = 0;
        if (dataRemaining <= 0) return false;
        int want = (int)Math.Min(raw.Length, dataRemaining);
        int total = 0;
        while (total < want)
        {
            int read = stream.Read(raw, total, want - total);
            if (read <= 0) break;
            total += read;
        }
        total -= total % blockAlign;
        if (total == 0)
        {
            dataRemaining = 0;
            return false;
        }
        rawLength = total;
        dataRemaining = sizeKnown ? dataRemaining - total : dataRemaining;
        return true;
    }

    private static WavDecoder Build(Stream stream, ushort tag, int channels, int sampleRate, int align, int bits, long dataStart, long dataLength, bool known)
    {
        if (channels is < 1 or > 64 || sampleRate is < 1 or > 768000) throw new InvalidDataException("The WAVE format is invalid.");
        int bytes = (bits + 7) / 8;
        SampleKind kind;
        switch (tag)
        {
            case 1 when bits == 8: kind = SampleKind.Unsigned8; break;
            case 1 when bits == 16: kind = SampleKind.Int16; break;
            case 1 when bits == 24: kind = SampleKind.Int24; break;
            case 1 when bits == 32: kind = SampleKind.Int32; break;
            case 3 when bits == 32: kind = SampleKind.Float32; break;
            case 3 when bits == 64: kind = SampleKind.Float64; break;
            case 6 when bits == 8: kind = SampleKind.ALaw; break;
            case 7 when bits == 8: kind = SampleKind.MuLaw; break;
            default: throw new NotSupportedException($"WAVE format {tag} with {bits} bits per sample is not supported.");
        }
        if (align != bytes * channels) align = bytes * channels;
        return new WavDecoder(stream, sampleRate, channels, align, kind, dataStart, dataLength, known);
    }

    private void Convert(ReadOnlySpan<byte> source, Span<float> destination)
    {
        int samples = destination.Length;
        switch (kind)
        {
            case SampleKind.Unsigned8:
                for (int i = 0; i < samples; i++) destination[i] = (source[i] - 128) * (1f / 128f);
                break;
            case SampleKind.Int16:
                ConvertInt16(source, destination);
                break;
            case SampleKind.Int24:
                for (int i = 0; i < samples; i++)
                {
                    int v = source[3 * i] | source[3 * i + 1] << 8 | (sbyte)source[3 * i + 2] << 16;
                    destination[i] = v * (1f / 8388608f);
                }
                break;
            case SampleKind.Int32:
                for (int i = 0; i < samples; i++) destination[i] = BinaryPrimitives.ReadInt32LittleEndian(source[(4 * i)..]) * (1f / 2147483648f);
                break;
            case SampleKind.Float32:
                for (int i = 0; i < samples; i++)
                {
                    float v = BinaryPrimitives.ReadSingleLittleEndian(source[(4 * i)..]);
                    destination[i] = float.IsFinite(v) ? v : 0f;
                }
                break;
            case SampleKind.Float64:
                for (int i = 0; i < samples; i++)
                {
                    double v = BinaryPrimitives.ReadDoubleLittleEndian(source[(8 * i)..]);
                    destination[i] = double.IsFinite(v) ? (float)v : 0f;
                }
                break;
            case SampleKind.ALaw:
                for (int i = 0; i < samples; i++) destination[i] = ALawTable[source[i]];
                break;
            case SampleKind.MuLaw:
                for (int i = 0; i < samples; i++) destination[i] = MuLawTable[source[i]];
                break;
        }
    }

    private static void ConvertInt16(ReadOnlySpan<byte> source, Span<float> destination)
    {
        int samples = destination.Length;
        int i = 0;
        if (BitConverter.IsLittleEndian && Vector.IsHardwareAccelerated)
        {
            ReadOnlySpan<short> shorts = MemoryMarshal.Cast<byte, short>(source[..(samples * 2)]);
            Vector<float> scale = new(1f / 32768f);
            for (; i <= samples - Vector<short>.Count; i += Vector<short>.Count)
            {
                Vector.Widen(new Vector<short>(shorts[i..]), out Vector<int> low, out Vector<int> high);
                (Vector.ConvertToSingle(low) * scale).CopyTo(destination[i..]);
                (Vector.ConvertToSingle(high) * scale).CopyTo(destination[(i + Vector<int>.Count)..]);
            }
        }
        for (; i < samples; i++) destination[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(2 * i)..]) * (1f / 32768f);
    }

    // ITU-T G.711 expansion tables, built once from the companding laws.
    private static readonly float[] ALawTable = BuildALaw();
    private static readonly float[] MuLawTable = BuildMuLaw();

    private static float[] BuildALaw()
    {
        float[] table = new float[256];
        for (int code = 0; code < 256; code++)
        {
            int a = code ^ 0x55;
            int exponent = (a >> 4) & 7;
            int mantissa = a & 0xF;
            int magnitude = exponent == 0 ? (mantissa << 4) + 8 : ((mantissa << 4) + 0x108) << (exponent - 1);
            table[code] = ((a & 0x80) != 0 ? magnitude : -magnitude) * (1f / 32768f);
        }
        return table;
    }

    private static float[] BuildMuLaw()
    {
        float[] table = new float[256];
        for (int code = 0; code < 256; code++)
        {
            int u = ~code & 0xFF;
            int magnitude = (((u & 0xF) << 3) + 0x84 << ((u >> 4) & 7)) - 0x84;
            table[code] = ((u & 0x80) != 0 ? -magnitude : magnitude) * (1f / 32768f);
        }
        return table;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        if (!TryReadExactly(stream, buffer)) throw new InvalidDataException("The WAVE file ends inside its header.");
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read <= 0) return false;
            total += read;
        }
        return true;
    }

    private static void Skip(Stream stream, long count)
    {
        if (count <= 0) return;
        if (StreamLength.TryGet(stream, out _))
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }
        Span<byte> trash = stackalloc byte[512];
        while (count > 0)
        {
            int read = stream.Read(trash[..(int)Math.Min(count, trash.Length)]);
            if (read <= 0) throw new InvalidDataException("The WAVE file ends inside its header.");
            count -= read;
        }
    }
}
