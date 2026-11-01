using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Aibell.Audio.Aiff;

/// <summary>
/// AIFF and AIFF-C reader: big-endian and little-endian (sowt) signed integer PCM (8 to 32 bits), unsigned 8-bit PCM (raw),
/// IEEE float (32 and 64 bits), and A-law and mu-law. Works on forward-only streams (an unknown data size runs to the end of the
/// stream) and seeks exactly when the stream can seek. Format reference: Apple Computer's Audio Interchange File Format (AIFF) v1.3,
/// AIFF-C (1991), and ITU-T G.711.
/// </summary>
public sealed class AiffDecoder : IAudioDecoder
{
    private const int RawBufferBytes = 32 * 1024;
    private const uint UnknownSize = 0xFFFFFFFF;
    private const uint TagNone = 0x4E4F4E45;
    private const uint TagTwos = 0x74776F73;
    private const uint TagSowt = 0x736F7774;
    private const uint TagRaw = 0x72617720;
    private const uint TagIn24 = 0x696E3234;
    private const uint TagIn32 = 0x696E3332;
    private const uint TagFl32 = 0x666C3332;
    private const uint TagFl32Upper = 0x464C3332;
    private const uint TagFl64 = 0x666C3634;
    private const uint TagFl64Upper = 0x464C3634;
    private const uint TagALaw = 0x616C6177;
    private const uint TagALawUpper = 0x414C4157;
    private const uint TagMuLaw = 0x756C6177;
    private const uint TagMuLawUpper = 0x554C4157;

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

    private enum SampleKind { Signed8, Unsigned8, Int16Be, Int16Le, Int24Be, Int24Le, Int32Be, Int32Le, Float32Be, Float64Be, ALaw, MuLaw }

    private AiffDecoder(Stream stream, int sampleRate, int channels, int blockAlign, SampleKind kind, long dataStart, long dataLength, bool sizeKnown)
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

    /// <summary>True when the bytes start a FORM/AIFF or FORM/AIFC file.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head) =>
        head.Length >= 12 && head[..4].SequenceEqual("FORM"u8) && (head.Slice(8, 4).SequenceEqual("AIFF"u8) || head.Slice(8, 4).SequenceEqual("AIFC"u8));

    /// <summary>Reads the header and leaves the stream at the first sample. Throws <see cref="InvalidDataException"/> or <see cref="NotSupportedException"/>.</summary>
    public static AiffDecoder Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[12];
        ReadExactly(stream, header);
        if (!Sniff(header)) throw new InvalidDataException("This is not an AIFF file.");
        bool aifc = header[11] == (byte)'C';
        long position = 12;
        bool haveFormat = false;
        uint compression = TagNone;
        uint numSampleFrames = 0;
        int channels = 0, sampleRate = 0, bits = 0;
        long ssndDataStart = -1, ssndPayload = 0;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> body = stackalloc byte[24];
        while (true)
        {
            if (!TryReadExactly(stream, chunk)) throw new InvalidDataException("The AIFF file has no audio data.");
            position += 8;
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
            long size = size32;
            ReadOnlySpan<byte> id = chunk[..4];
            if (id.SequenceEqual("SSND"u8))
            {
                if (size32 != 0 && size < 8) throw new InvalidDataException("The AIFF sound data chunk is too short.");
                ReadExactly(stream, chunk);
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                if (offset > 16 * 1024 * 1024 || (size32 != UnknownSize && size32 != 0 && size - 8 < offset))
                    throw new InvalidDataException("The AIFF sound data offset is invalid.");
                if (haveFormat)
                {
                    Skip(stream, offset);
                    position += 8 + offset;
                    bool known = size32 != UnknownSize && size32 != 0 && !(size == 8 + offset && numSampleFrames is 0 or UnknownSize);
                    long payload = known ? size - 8 - offset : long.MaxValue;
                    return Build(stream, compression, channels, sampleRate, bits, numSampleFrames, position, payload, known);
                }
                if (!StreamLength.TryGet(stream, out _) || size32 is UnknownSize or 0)
                    throw new InvalidDataException("The AIFF sound data comes before its format.");
                ssndDataStart = position + 8 + offset;
                ssndPayload = size - 8 - offset;
                Skip(stream, size - 8);
                position += size;
            }
            else if (id.SequenceEqual("COMM"u8))
            {
                int minComm = aifc ? 22 : 18;
                if (size < minComm || size > 4096) throw new InvalidDataException("The AIFF format chunk is invalid.");
                int toRead = (int)Math.Min(size, body.Length);
                body.Clear();
                ReadExactly(stream, body[..toRead]);
                Skip(stream, size - toRead);
                channels = BinaryPrimitives.ReadInt16BigEndian(body);
                numSampleFrames = BinaryPrimitives.ReadUInt32BigEndian(body[2..]);
                bits = BinaryPrimitives.ReadInt16BigEndian(body[6..]);
                sampleRate = ReadExtended80(body.Slice(8, 10));
                compression = aifc ? BinaryPrimitives.ReadUInt32BigEndian(body[18..]) : TagNone;
                haveFormat = true;
                position += size;
                if (ssndDataStart >= 0)
                {
                    stream.Seek(ssndDataStart, SeekOrigin.Begin);
                    return Build(stream, compression, channels, sampleRate, bits, numSampleFrames, ssndDataStart, ssndPayload, true);
                }
            }
            else
            {
                Skip(stream, size);
                position += size;
            }
            if ((size & 1) != 0)
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

    private static AiffDecoder Build(Stream stream, uint compression, int channels, int sampleRate, int bits, uint numSampleFrames, long dataStart, long ssndPayloadLength, bool known)
    {
        if (channels is < 1 or > 64 || sampleRate is < 1 or > 768000 || bits is < 1 or > 64)
            throw new InvalidDataException("The AIFF format is invalid.");
        int bytes = (bits + 7) / 8;
        SampleKind kind;
        switch (compression)
        {
            case TagNone or TagTwos when bytes == 1: kind = SampleKind.Signed8; break;
            case TagNone or TagTwos when bytes == 2: kind = SampleKind.Int16Be; break;
            case TagNone or TagTwos when bytes == 3: kind = SampleKind.Int24Be; break;
            case TagNone or TagTwos when bytes == 4: kind = SampleKind.Int32Be; break;
            case TagIn24 when bytes == 3: kind = SampleKind.Int24Be; break;
            case TagIn32 when bytes == 4: kind = SampleKind.Int32Be; break;
            case TagSowt when bytes == 1: kind = SampleKind.Signed8; break;
            case TagSowt when bytes == 2: kind = SampleKind.Int16Le; break;
            case TagSowt when bytes == 3: kind = SampleKind.Int24Le; break;
            case TagSowt when bytes == 4: kind = SampleKind.Int32Le; break;
            case TagRaw when bits == 8: kind = SampleKind.Unsigned8; bytes = 1; break;
            case TagFl32 or TagFl32Upper when bits == 32: kind = SampleKind.Float32Be; bytes = 4; break;
            case TagFl64 or TagFl64Upper when bits == 64: kind = SampleKind.Float64Be; bytes = 8; break;
            case TagALaw or TagALawUpper when bits is 8 or 16: kind = SampleKind.ALaw; bytes = 1; break;
            case TagMuLaw or TagMuLawUpper when bits is 8 or 16: kind = SampleKind.MuLaw; bytes = 1; break;
            default: throw new NotSupportedException($"AIFF compression 0x{compression:X8} with {bits} bits per sample is not supported.");
        }
        int align = bytes * channels;
        long dataLength = ssndPayloadLength;
        if (known && numSampleFrames is > 0 and < UnknownSize)
            dataLength = Math.Min(dataLength, (long)numSampleFrames * align);
        if (known && StreamLength.TryGet(stream, out long available))
            dataLength = Math.Clamp(dataLength, 0, available - dataStart);
        return new AiffDecoder(stream, sampleRate, channels, align, kind, dataStart, dataLength, known);
    }

    private static int ReadExtended80(ReadOnlySpan<byte> bytes)
    {
        ushort expAndSign = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..]);
        int exp = expAndSign & 0x7FFF;
        if ((expAndSign & 0x8000) != 0 || exp is 0 or 0x7FFF || mantissa == 0) return -1;
        double value = Math.ScaleB((double)mantissa, exp - 16446);
        return value is >= 1 and <= 768000 ? (int)Math.Round(value) : -1;
    }

    private void Convert(ReadOnlySpan<byte> source, Span<float> destination)
    {
        int samples = destination.Length;
        switch (kind)
        {
            case SampleKind.Signed8:
                for (int i = 0; i < samples; i++) destination[i] = (sbyte)source[i] * (1f / 128f);
                break;
            case SampleKind.Unsigned8:
                for (int i = 0; i < samples; i++) destination[i] = (source[i] - 128) * (1f / 128f);
                break;
            case SampleKind.Int16Be:
                ConvertInt16Be(source, destination);
                break;
            case SampleKind.Int16Le:
                ConvertInt16Le(source, destination);
                break;
            case SampleKind.Int24Be:
                for (int i = 0; i < samples; i++)
                {
                    int v = (sbyte)source[3 * i] << 16 | source[3 * i + 1] << 8 | source[3 * i + 2];
                    destination[i] = v * (1f / 8388608f);
                }
                break;
            case SampleKind.Int24Le:
                for (int i = 0; i < samples; i++)
                {
                    int v = source[3 * i] | source[3 * i + 1] << 8 | (sbyte)source[3 * i + 2] << 16;
                    destination[i] = v * (1f / 8388608f);
                }
                break;
            case SampleKind.Int32Be:
                for (int i = 0; i < samples; i++) destination[i] = BinaryPrimitives.ReadInt32BigEndian(source[(4 * i)..]) * (1f / 2147483648f);
                break;
            case SampleKind.Int32Le:
                for (int i = 0; i < samples; i++) destination[i] = BinaryPrimitives.ReadInt32LittleEndian(source[(4 * i)..]) * (1f / 2147483648f);
                break;
            case SampleKind.Float32Be:
                for (int i = 0; i < samples; i++)
                {
                    float v = BinaryPrimitives.ReadSingleBigEndian(source[(4 * i)..]);
                    destination[i] = float.IsFinite(v) ? v : 0f;
                }
                break;
            case SampleKind.Float64Be:
                for (int i = 0; i < samples; i++)
                {
                    double v = BinaryPrimitives.ReadDoubleBigEndian(source[(8 * i)..]);
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

    private static void ConvertInt16Be(ReadOnlySpan<byte> source, Span<float> destination)
    {
        int samples = destination.Length;
        int i = 0;
        if (BitConverter.IsLittleEndian && Vector.IsHardwareAccelerated)
        {
            ReadOnlySpan<ushort> words = MemoryMarshal.Cast<byte, ushort>(source[..(samples * 2)]);
            Vector<float> scale = new(1f / 32768f);
            for (; i <= samples - Vector<ushort>.Count; i += Vector<ushort>.Count)
            {
                Vector<ushort> raw = new(words[i..]);
                Vector<short> signed = Vector.AsVectorInt16((raw >> 8) | (raw << 8));
                Vector.Widen(signed, out Vector<int> low, out Vector<int> high);
                (Vector.ConvertToSingle(low) * scale).CopyTo(destination[i..]);
                (Vector.ConvertToSingle(high) * scale).CopyTo(destination[(i + Vector<int>.Count)..]);
            }
        }
        for (; i < samples; i++) destination[i] = BinaryPrimitives.ReadInt16BigEndian(source[(2 * i)..]) * (1f / 32768f);
    }

    private static void ConvertInt16Le(ReadOnlySpan<byte> source, Span<float> destination)
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
        if (!TryReadExactly(stream, buffer)) throw new InvalidDataException("The AIFF file ends inside its header.");
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
            if (read <= 0) throw new InvalidDataException("The AIFF file ends inside its header.");
            count -= read;
        }
    }
}
