using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aibell.Audio.Flac;

/// <summary>
/// Native FLAC decoder written from RFC 9639: STREAMINFO and SEEKTABLE metadata, every subframe type (constant, verbatim, fixed, LPC), partitioned
/// Rice residuals, wasted bits, the three stereo decorrelation modes, CRC-8 and CRC-16 checks, and seeking by seek table or by bisecting the
/// frame headers. A damaged frame is replaced by silence and decoding resumes at the next valid frame header. The decoder keeps one fixed input
/// buffer and one set of sample buffers, so decoding allocates nothing. Samples of 32 bits are supported except in the stereo decorrelated modes,
/// which would need 33-bit arithmetic.
/// </summary>
public sealed class FlacDecoder : IAudioDecoder
{
    private const int MinimumBufferBytes = 64 * 1024;
    private const int MaximumFrameBytes = 16 * 1024 * 1024;
    private const int MaximumHeaderBytes = 24;

    private readonly Stream stream;
    private readonly FlacStreamInfo info;
    private readonly long[] seekSamples;
    private readonly long[] seekOffsets;
    private readonly long firstFrameOffset;
    private readonly bool canSeek;
    private readonly int[] coefficients = new int[32];
    private byte[] buffer;
    private int bufferStart;
    private int bufferEnd;
    private long bufferOrigin;
    private bool endOfInput;
    private int lookahead;
    private int[][] channelData;
    private int blockCapacity;
    private int frameLength;
    private int frameRead;
    private int frameBitsPerSample;
    private bool inFailure;
    private bool disposed;

    private FlacDecoder(Stream stream, FlacStreamInfo info, long[] seekSamples, long[] seekOffsets, long firstFrameOffset)
    {
        this.stream = stream;
        this.info = info;
        this.seekSamples = seekSamples;
        this.seekOffsets = seekOffsets;
        this.firstFrameOffset = firstFrameOffset;
        canSeek = firstFrameOffset >= 0 && StreamLength.TryGet(stream, out _);
        bufferOrigin = firstFrameOffset;
        lookahead = Math.Clamp(info.MaxFrameSize, 4096, MaximumFrameBytes);
        buffer = new byte[Math.Max(MinimumBufferBytes, lookahead * 2)];
        blockCapacity = Math.Max(info.MaxBlockSize, 4608);
        channelData = new int[info.Channels][];
        for (int c = 0; c < channelData.Length; c++) channelData[c] = new int[blockCapacity];
        frameBitsPerSample = info.BitsPerSample;
    }

    public int SampleRate => info.SampleRate;

    public int Channels => info.Channels;

    public long TotalFrames => info.TotalSamples > 0 ? info.TotalSamples : -1;

    /// <summary>The MD5 signature of the unencoded audio stored in STREAMINFO (all zero when the encoder did not compute it).</summary>
    public ReadOnlySpan<byte> AudioMd5 => info.Md5;

    public int BitsPerSample => info.BitsPerSample;

    /// <summary>True when the bytes start a native FLAC stream.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head) => head.Length >= 4 && head[..4].SequenceEqual("fLaC"u8);

    /// <summary>Reads the metadata blocks and leaves the stream at the first frame. Throws <see cref="InvalidDataException"/> or <see cref="NotSupportedException"/>.</summary>
    public static FlacDecoder Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[4];
        ReadExactly(stream, header);
        if (!Sniff(header)) throw new InvalidDataException("This is not a FLAC stream.");
        FlacStreamInfo? info = null;
        long[] seekSamples = [];
        long[] seekOffsets = [];
        bool last;
        do
        {
            ReadExactly(stream, header);
            last = (header[0] & 0x80) != 0;
            int type = header[0] & 0x7F;
            int length = header[1] << 16 | header[2] << 8 | header[3];
            if (info is null && type != 0) throw new InvalidDataException("The FLAC STREAMINFO block is missing.");
            switch (type)
            {
                case 0 when info is null:
                    {
                        if (length < FlacStreamInfo.Length) throw new InvalidDataException("The FLAC STREAMINFO block is too short.");
                        byte[] block = new byte[length];
                        ReadExactly(stream, block);
                        info = FlacStreamInfo.Parse(block);
                        break;
                    }
                case 3:
                    {
                        byte[] block = new byte[length];
                        ReadExactly(stream, block);
                        int points = length / 18;
                        List<long> samples = new(points);
                        List<long> offsets = new(points);
                        for (int i = 0; i < points; i++)
                        {
                            ulong sample = BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(i * 18));
                            if (sample == ulong.MaxValue) break;
                            samples.Add((long)sample);
                            offsets.Add((long)BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(i * 18 + 8)));
                        }
                        seekSamples = [.. samples];
                        seekOffsets = [.. offsets];
                        break;
                    }
                case 127:
                    throw new InvalidDataException("The FLAC metadata block type is invalid.");
                default:
                    Skip(stream, length);
                    break;
            }
        }
        while (!last);
        FlacStreamInfo parsed = info ?? throw new InvalidDataException("The FLAC STREAMINFO block is missing.");
        if (parsed.Channels > 8 || parsed.BitsPerSample > 32) throw new NotSupportedException("This FLAC stream's format is not supported.");
        long firstFrame = StreamLength.TryGet(stream, out _) ? stream.Position : -1;
        FlacDecoder decoder = new(stream, parsed, seekSamples, seekOffsets, firstFrame);
        decoder.Prime();
        return decoder;
    }

    public int Read(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int produced = 0;
        int channels = info.Channels;
        while (produced < frames)
        {
            if (frameRead >= frameLength && !DecodeNextFrame()) break;
            int count = Math.Min(frameLength - frameRead, frames - produced);
            Interleave(destination.Slice(produced * channels, count * channels), frameRead, count);
            frameRead += count;
            produced += count;
        }
        return produced;
    }

    public bool Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!canSeek || frame < 0) return false;
        long total = info.TotalSamples;
        if (total > 0 && frame >= total)
        {
            stream.Seek(0, SeekOrigin.End);
            bufferOrigin = stream.Position;
            bufferStart = bufferEnd = 0;
            endOfInput = true;
            frameLength = frameRead = 0;
            return true;
        }
        (long offset, long sample) = LocateStart(frame);
        Reposition(offset);
        frameLength = frameRead = 0;
        inFailure = false;
        long running = sample;
        while (true)
        {
            if (!DecodeNextFrame()) return true;
            if (running + frameLength > frame)
            {
                frameRead = (int)Math.Max(0, frame - running);
                return true;
            }
            running += frameLength;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
    }

    // Decodes the first frame now so that a file with valid metadata but no audio is refused up front.
    private void Prime()
    {
        if (!DecodeNextFrame()) throw new InvalidDataException("The FLAC stream has no audio frames.");
        frameRead = 0;
    }

    // ---- input buffering -----------------------------------------------------------------------------------------------------------------

    private void EnsureBuffered(int minimum)
    {
        int have = bufferEnd - bufferStart;
        if (have >= minimum || endOfInput) return;
        if (buffer.Length < minimum + MaximumHeaderBytes)
        {
            byte[] bigger = new byte[Math.Max(buffer.Length * 2, minimum + MaximumHeaderBytes)];
            Buffer.BlockCopy(buffer, bufferStart, bigger, 0, have);
            buffer = bigger;
            bufferOrigin += bufferStart;
            bufferStart = 0;
            bufferEnd = have;
        }
        else if (bufferStart > 0 && buffer.Length - bufferStart < minimum + MaximumHeaderBytes)
        {
            Buffer.BlockCopy(buffer, bufferStart, buffer, 0, have);
            bufferOrigin += bufferStart;
            bufferStart = 0;
            bufferEnd = have;
        }
        while (bufferEnd - bufferStart < minimum)
        {
            int read = stream.Read(buffer, bufferEnd, buffer.Length - bufferEnd);
            if (read <= 0)
            {
                endOfInput = true;
                break;
            }
            bufferEnd += read;
        }
    }

    private void Reposition(long absoluteOffset)
    {
        stream.Seek(absoluteOffset, SeekOrigin.Begin);
        bufferOrigin = absoluteOffset;
        bufferStart = bufferEnd = 0;
        endOfInput = false;
    }

    // ---- frame decoding ------------------------------------------------------------------------------------------------------------------

    /// <summary>Decodes the next good frame into the sample buffers. False at the end of the stream.</summary>
    private bool DecodeNextFrame()
    {
        frameLength = frameRead = 0;
        while (true)
        {
            EnsureBuffered(lookahead);
            int available = bufferEnd - bufferStart;
            if (available < 6) return false;
            ReadOnlySpan<byte> window = buffer.AsSpan(bufferStart, available);
            if (window[0] != 0xFF || (window[1] & 0xFE) != 0xF8)
            {
                int next = FindSync(window[1..]);
                if (next < 0)
                {
                    bufferStart = bufferEnd - 1;
                    if (endOfInput) return false;
                    continue;
                }
                bufferStart += next + 1;
                continue;
            }
            try
            {
                int consumed = DecodeFrame(window, out int blockSize);
                bufferStart += consumed;
                frameLength = blockSize;
                inFailure = false;
                return true;
            }
            catch (FlacUnderflowException)
            {
                if (!endOfInput)
                {
                    if (lookahead >= MaximumFrameBytes) throw new InvalidDataException("A FLAC frame is too large.");
                    lookahead = Math.Min(lookahead * 2, MaximumFrameBytes);
                    continue;
                }
                // The file ends inside a frame.
                bufferStart = bufferEnd;
                return false;
            }
            catch (InvalidDataException)
            {
                // Damaged frame: the first failure of a run is replaced by silence of the frame's length, then the search for the next frame header goes on.
                if (!inFailure && TryPeekBlockSize(window, out int silent))
                {
                    inFailure = true;
                    bufferStart++;
                    PrepareSilence(silent);
                    return true;
                }
                inFailure = true;
                bufferStart++;
            }
        }
    }

    private static int FindSync(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int at = data[offset..].IndexOf((byte)0xFF);
            if (at < 0) return -1;
            offset += at;
            if (offset + 1 >= data.Length) return -1;
            if ((data[offset + 1] & 0xFE) == 0xF8) return offset;
            offset++;
        }
        return -1;
    }

    private bool TryPeekBlockSize(ReadOnlySpan<byte> window, out int blockSize)
    {
        blockSize = 0;
        try
        {
            FlacBitReader reader = new(window);
            ParseHeader(ref reader, window, out FrameHeader header);
            blockSize = header.BlockSize;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or FlacUnderflowException)
        {
            return false;
        }
    }

    private void PrepareSilence(int blockSize)
    {
        EnsureCapacity(blockSize);
        foreach (int[] channel in channelData) Array.Clear(channel, 0, blockSize);
        frameLength = blockSize;
        frameRead = 0;
    }

    private readonly record struct FrameHeader(int BlockSize, int Channels, int BitsPerSample, int Assignment, bool Variable, long Number);

    private int DecodeFrame(ReadOnlySpan<byte> data, out int blockSize)
    {
        FlacBitReader reader = new(data);
        ParseHeader(ref reader, data, out FrameHeader header);
        blockSize = header.BlockSize;
        EnsureCapacity(blockSize);
        for (int c = 0; c < header.Channels; c++)
        {
            int depth = header.BitsPerSample;
            if (header.Assignment switch { 8 => c == 1, 9 => c == 0, 10 => c == 1, _ => false }) depth++;
            DecodeSubframe(ref reader, channelData[c], blockSize, depth);
        }
        reader.AlignToByte();
        int end = reader.BytePosition;
        uint stored = reader.ReadBits(16);
        if (Crc.Crc16(data[..end]) != stored) throw new InvalidDataException("A FLAC frame failed its CRC.");
        Decorrelate(header.Assignment, blockSize);
        frameBitsPerSample = header.BitsPerSample;
        return reader.BytePosition;
    }

    private void ParseHeader(ref FlacBitReader reader, ReadOnlySpan<byte> data, out FrameHeader header)
    {
        if (reader.ReadBits(14) != 0x3FFE || reader.ReadBits(1) != 0) throw new InvalidDataException("This is not a FLAC frame header.");
        bool variable = reader.ReadBits(1) == 1;
        int blockCode = (int)reader.ReadBits(4);
        int rateCode = (int)reader.ReadBits(4);
        int channelCode = (int)reader.ReadBits(4);
        int depthCode = (int)reader.ReadBits(3);
        if (reader.ReadBits(1) != 0) throw new InvalidDataException("The FLAC frame header has a reserved bit set.");

        uint first = reader.ReadBits(8);
        long number;
        if (first < 0x80) number = first;
        else
        {
            int ones = BitOperations.LeadingZeroCount(~first << 24);
            if (ones is < 2 or > 7) throw new InvalidDataException("The FLAC frame number is invalid.");
            number = first & (0x7Fu >> ones);
            for (int i = 1; i < ones; i++)
            {
                uint next = reader.ReadBits(8);
                if ((next & 0xC0) != 0x80) throw new InvalidDataException("The FLAC frame number is invalid.");
                number = number << 6 | (next & 0x3F);
            }
        }

        int blockSize = blockCode switch
        {
            0 => throw new InvalidDataException("The FLAC block size is reserved."),
            1 => 192,
            >= 2 and <= 5 => 576 << (blockCode - 2),
            6 => (int)reader.ReadBits(8) + 1,
            7 => (int)reader.ReadBits(16) + 1,
            _ => 256 << (blockCode - 8)
        };
        if (rateCode == 12) reader.ReadBits(8);
        else if (rateCode is 13 or 14) reader.ReadBits(16);
        else if (rateCode == 15) throw new InvalidDataException("The FLAC sample rate code is invalid.");

        int channels, assignment = 0;
        if (channelCode <= 7) channels = channelCode + 1;
        else if (channelCode <= 10)
        {
            channels = 2;
            assignment = channelCode;
        }
        else throw new InvalidDataException("The FLAC channel assignment is reserved.");
        if (channels != info.Channels) throw new InvalidDataException("The FLAC channel count changed.");

        int depth = depthCode switch
        {
            0 => info.BitsPerSample,
            1 => 8,
            2 => 12,
            4 => 16,
            5 => 20,
            6 => 24,
            7 => 32,
            _ => throw new InvalidDataException("The FLAC sample size is reserved.")
        };
        if (assignment != 0 && depth >= 32) throw new NotSupportedException("32-bit stereo FLAC with decorrelation is not supported.");

        int headerEnd = reader.BytePosition;
        uint crc = reader.ReadBits(8);
        if (Crc.Crc8(data[..headerEnd]) != crc) throw new InvalidDataException("A FLAC frame header failed its CRC.");
        header = new FrameHeader(blockSize, channels, depth, assignment, variable, number);
    }

    private void EnsureCapacity(int blockSize)
    {
        if (blockSize <= blockCapacity) return;
        blockCapacity = blockSize;
        for (int c = 0; c < channelData.Length; c++) channelData[c] = new int[blockCapacity];
    }

    private void DecodeSubframe(ref FlacBitReader reader, int[] output, int blockSize, int depth)
    {
        if (reader.ReadBits(1) != 0) throw new InvalidDataException("The FLAC subframe header is invalid.");
        int type = (int)reader.ReadBits(6);
        int wasted = 0;
        if (reader.ReadBits(1) == 1) wasted = reader.ReadUnary() + 1;
        depth -= wasted;
        if (depth is < 1 or > 32) throw new InvalidDataException("The FLAC subframe has no usable bits.");
        Span<int> data = output.AsSpan(0, blockSize);

        if (type == 0) data.Fill(reader.ReadSigned(depth));
        else if (type == 1)
        {
            for (int i = 0; i < data.Length; i++) data[i] = reader.ReadSigned(depth);
        }
        else if (type is >= 8 and <= 12)
        {
            int order = type - 8;
            if (order > blockSize) throw new InvalidDataException("The FLAC predictor order exceeds the block size.");
            for (int i = 0; i < order; i++) data[i] = reader.ReadSigned(depth);
            DecodeResidual(ref reader, data, order);
            RestoreFixed(data, order);
        }
        else if (type >= 32)
        {
            int order = type - 31;
            if (order > blockSize) throw new InvalidDataException("The FLAC predictor order exceeds the block size.");
            for (int i = 0; i < order; i++) data[i] = reader.ReadSigned(depth);
            int precision = (int)reader.ReadBits(4) + 1;
            if (precision == 16) throw new InvalidDataException("The FLAC coefficient precision is invalid.");
            int shift = reader.ReadSigned(5);
            // The standard forbids a negative shift; old encoders produced it as a left shift, which is how it is read here.
            for (int i = 0; i < order; i++) coefficients[order - 1 - i] = reader.ReadSigned(precision);
            DecodeResidual(ref reader, data, order);
            RestoreLpc(data, order, shift);
        }
        else throw new InvalidDataException("The FLAC subframe type is reserved.");

        if (wasted > 0)
        {
            for (int i = 0; i < data.Length; i++) data[i] <<= wasted;
        }
    }

    private static void DecodeResidual(ref FlacBitReader reader, Span<int> data, int predictorOrder)
    {
        uint method = reader.ReadBits(2);
        if (method > 1) throw new InvalidDataException("The FLAC residual coding method is reserved.");
        int parameterBits = method == 0 ? 4 : 5;
        int escape = (1 << parameterBits) - 1;
        int partitionOrder = (int)reader.ReadBits(4);
        int partitions = 1 << partitionOrder;
        int blockSize = data.Length;
        if ((blockSize & (partitions - 1)) != 0) throw new InvalidDataException("The FLAC block size does not divide into the partitions.");
        int partitionSize = blockSize >> partitionOrder;
        if (partitionSize < predictorOrder) throw new InvalidDataException("A FLAC residual partition is smaller than the predictor order.");
        int at = predictorOrder;
        for (int p = 0; p < partitions; p++)
        {
            int count = p == 0 ? partitionSize - predictorOrder : partitionSize;
            int parameter = (int)reader.ReadBits(parameterBits);
            Span<int> part = data.Slice(at, count);
            at += count;
            if (parameter == escape)
            {
                int width = (int)reader.ReadBits(5);
                if (width == 0) part.Clear();
                else
                {
                    for (int i = 0; i < part.Length; i++) part[i] = reader.ReadSigned(width);
                }
                continue;
            }
            for (int i = 0; i < part.Length; i++)
            {
                uint folded = ((uint)reader.ReadUnary() << parameter) | reader.ReadBits(parameter);
                part[i] = (int)(folded >> 1) ^ -(int)(folded & 1);
            }
        }
    }

    private static void RestoreFixed(Span<int> data, int order)
    {
        switch (order)
        {
            case 1:
                for (int i = 1; i < data.Length; i++) data[i] += data[i - 1];
                break;
            case 2:
                for (int i = 2; i < data.Length; i++) data[i] += 2 * data[i - 1] - data[i - 2];
                break;
            case 3:
                for (int i = 3; i < data.Length; i++) data[i] += 3 * (data[i - 1] - data[i - 2]) + data[i - 3];
                break;
            case 4:
                for (int i = 4; i < data.Length; i++) data[i] += 4 * (data[i - 1] + data[i - 3]) - 6 * data[i - 2] - data[i - 4];
                break;
        }
    }

    // The coefficients are stored in reverse (oldest sample first) so the dot product walks both arrays forward.
    private void RestoreLpc(Span<int> data, int order, int shift)
    {
        ref int d = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(data);
        ref int c = ref coefficients[0];
        if (shift >= 0)
        {
            for (int i = order; i < data.Length; i++)
            {
                long sum = 0;
                ref int window = ref Unsafe.Add(ref d, i - order);
                int j = 0;
                for (; j + 4 <= order; j += 4)
                {
                    sum += (long)Unsafe.Add(ref c, j) * Unsafe.Add(ref window, j) + (long)Unsafe.Add(ref c, j + 1) * Unsafe.Add(ref window, j + 1)
                        + (long)Unsafe.Add(ref c, j + 2) * Unsafe.Add(ref window, j + 2) + (long)Unsafe.Add(ref c, j + 3) * Unsafe.Add(ref window, j + 3);
                }
                for (; j < order; j++) sum += (long)Unsafe.Add(ref c, j) * Unsafe.Add(ref window, j);
                Unsafe.Add(ref d, i) += (int)(sum >> shift);
            }
            return;
        }
        for (int i = order; i < data.Length; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++) sum += (long)coefficients[j] * data[i - order + j];
            data[i] += (int)(sum << -shift);
        }
    }

    private void Decorrelate(int assignment, int blockSize)
    {
        if (assignment == 0) return;
        Span<int> a = channelData[0].AsSpan(0, blockSize);
        Span<int> b = channelData[1].AsSpan(0, blockSize);
        switch (assignment)
        {
            case 8: // left + side
                for (int i = 0; i < blockSize; i++) b[i] = a[i] - b[i];
                break;
            case 9: // side + right
                for (int i = 0; i < blockSize; i++) a[i] += b[i];
                break;
            default: // mid + side
                for (int i = 0; i < blockSize; i++)
                {
                    int side = b[i];
                    int mid = (a[i] << 1) | (side & 1);
                    a[i] = (mid + side) >> 1;
                    b[i] = (mid - side) >> 1;
                }
                break;
        }
    }

    // ---- output --------------------------------------------------------------------------------------------------------------------------

    private void Interleave(Span<float> destination, int start, int count)
    {
        float scale = 1f / (1L << (frameBitsPerSample - 1));
        int channels = info.Channels;
        if (channels == 2)
        {
            ReadOnlySpan<int> left = channelData[0].AsSpan(start, count);
            ReadOnlySpan<int> right = channelData[1].AsSpan(start, count);
            for (int i = 0; i < count; i++)
            {
                destination[2 * i] = left[i] * scale;
                destination[2 * i + 1] = right[i] * scale;
            }
            return;
        }
        for (int c = 0; c < channels; c++)
        {
            ReadOnlySpan<int> source = channelData[c].AsSpan(start, count);
            for (int i = 0; i < count; i++) destination[i * channels + c] = source[i] * scale;
        }
    }

    // ---- seeking -------------------------------------------------------------------------------------------------------------------------

    // Finds a stream offset at or before the frame holding `target`, with the sample number that frame starts at.
    private (long Offset, long Sample) LocateStart(long target)
    {
        long loOffset = firstFrameOffset;
        long loSample = 0;
        for (int i = 0; i < seekSamples.Length; i++)
        {
            if (seekSamples[i] > target) break;
            loOffset = firstFrameOffset + seekOffsets[i];
            loSample = seekSamples[i];
        }
        if (seekSamples.Length > 0 || info.TotalSamples <= 0) return (loOffset, loSample);

        long hiOffset = stream.Length;
        long hiSample = info.TotalSamples;
        for (int round = 0; round < 24 && hiOffset - loOffset > 128 * 1024 && hiSample > loSample; round++)
        {
            double fraction = (double)(target - loSample) / (hiSample - loSample);
            long guess = loOffset + (long)(fraction * (hiOffset - loOffset)) - 32 * 1024;
            guess = Math.Clamp(guess, loOffset + 1, hiOffset - 1);
            if (!ScanForHeader(guess, out long headerOffset, out long sample, out int blockSize) || headerOffset >= hiOffset)
            {
                hiOffset = guess;
                continue;
            }
            if (sample > target)
            {
                hiOffset = Math.Min(headerOffset, guess + 1);
                hiSample = sample;
            }
            else if (sample + blockSize <= target)
            {
                loOffset = headerOffset;
                loSample = sample;
            }
            else return (headerOffset, sample);
        }
        return (loOffset, loSample);
    }

    // Looks for a frame header with a valid CRC-8 at or after `offset`; reports where it is and the sample it starts at.
    private bool ScanForHeader(long offset, out long headerOffset, out long sample, out int blockSize)
    {
        headerOffset = sample = 0;
        blockSize = 0;
        Reposition(offset);
        while (true)
        {
            EnsureBuffered(MaximumHeaderBytes);
            int available = bufferEnd - bufferStart;
            if (available < 6) return false;
            ReadOnlySpan<byte> window = buffer.AsSpan(bufferStart, available);
            int at = window[0] == 0xFF && (window[1] & 0xFE) == 0xF8 ? 0 : FindSync(window[1..]) + 1;
            if (at <= 0 && !(window[0] == 0xFF && (window[1] & 0xFE) == 0xF8))
            {
                bufferStart = bufferEnd - 1;
                if (endOfInput) return false;
                continue;
            }
            bufferStart += at;
            window = buffer.AsSpan(bufferStart, bufferEnd - bufferStart);
            try
            {
                FlacBitReader reader = new(window);
                ParseHeader(ref reader, window, out FrameHeader header);
                headerOffset = bufferOrigin + bufferStart;
                blockSize = header.BlockSize;
                sample = header.Variable ? header.Number : header.Number * (info.MinBlockSize == info.MaxBlockSize && info.MinBlockSize > 0 ? info.MinBlockSize : header.BlockSize);
                return true;
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or FlacUnderflowException)
            {
                bufferStart++;
            }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------------------

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read <= 0) throw new InvalidDataException("The FLAC stream ends inside its metadata.");
            total += read;
        }
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
            if (read <= 0) throw new InvalidDataException("The FLAC stream ends inside its metadata.");
            count -= read;
        }
    }
}
