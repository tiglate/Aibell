using System.Security.Cryptography;
using Aibell.Audio;

namespace Aibell.Audio.Flac.Tests;

public enum SubframeKind { Auto, Constant, Verbatim, Fixed0, Fixed1, Fixed2, Fixed3, Fixed4, Lpc }

public enum StereoMode { Independent = 0, LeftSide = 8, SideRight = 9, MidSide = 10 }

internal sealed class FlacEncodeOptions
{
    public int SampleRate { get; init; } = 44100;
    public int BitsPerSample { get; init; } = 16;
    public int[] BlockSizes { get; init; } = [1152];
    public bool VariableBlockSize { get; init; }
    public StereoMode Stereo { get; init; } = StereoMode.Independent;
    public SubframeKind Kind { get; init; } = SubframeKind.Auto;
    public int RiceMethod { get; init; }
    public int PartitionOrder { get; init; } = 2;
    public bool ForceEscape { get; init; }
    public int WastedBits { get; init; }
    public int[] LpcCoefficients { get; init; } = [2, -1];
    public int LpcShift { get; init; }
    public int LpcPrecision { get; init; } = 4;
    public bool SeekTable { get; init; }
    public bool Md5 { get; init; } = true;
    public bool KnownLength { get; init; } = true;
    public byte[][] ExtraMetadata { get; init; } = [];
    public bool OmitHeaderRateAndDepth { get; init; } = true;
}

/// <summary>A small FLAC writer built from RFC 9639, used only to produce streams that exercise every coding path of the decoder.</summary>
internal static class FlacTestEncoder
{
    private sealed class BitWriter
    {
        private readonly List<byte> bytes = [];
        private ulong accumulator;
        private int bits;

        public int ByteCount => bytes.Count;

        public void Write(ulong value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                accumulator = accumulator << 1 | (value >> i & 1);
                if (++bits == 8)
                {
                    bytes.Add((byte)accumulator);
                    accumulator = 0;
                    bits = 0;
                }
            }
        }

        public void WriteSigned(long value, int count) => Write((ulong)value & (count == 64 ? ulong.MaxValue : (1UL << count) - 1), count);

        public void Unary(int zeros)
        {
            for (int i = 0; i < zeros; i++) Write(0, 1);
            Write(1, 1);
        }

        public void Align()
        {
            while (bits != 0) Write(0, 1);
        }

        public byte[] ToArray() => [.. bytes];

        public ReadOnlySpan<byte> Span(int from) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[from..];
    }

    /// <summary>Byte offsets of the frames of the last stream written, from the start of the file.</summary>
    public static List<int> LastFrameOffsets { get; } = [];

    public static byte[] Encode(int[][] channels, FlacEncodeOptions options)
    {
        LastFrameOffsets.Clear();
        int channelCount = channels.Length;
        int total = channels[0].Length;
        using MemoryStream output = new();
        byte[] streamInfo = BuildStreamInfo(channels, options, total);
        List<byte[]> metadata = [];
        List<(long Sample, long Offset, int Frames)> seekPoints = [];

        // Frames first, so the seek table and sizes are known.
        using MemoryStream frames = new();
        int position = 0, frameNumber = 0, sizeIndex = 0;
        int minFrame = int.MaxValue, maxFrame = 0, minBlock = int.MaxValue, maxBlock = 0;
        while (position < total)
        {
            int blockSize = Math.Min(options.BlockSizes[sizeIndex++ % options.BlockSizes.Length], total - position);
            int before = (int)frames.Length;
            seekPoints.Add((position, before, blockSize));
            LastFrameOffsets.Add(before);
            byte[] frame = EncodeFrame(channels, position, blockSize, frameNumber, options);
            frames.Write(frame);
            minFrame = Math.Min(minFrame, frame.Length);
            maxFrame = Math.Max(maxFrame, frame.Length);
            minBlock = Math.Min(minBlock, blockSize);
            maxBlock = Math.Max(maxBlock, blockSize);
            position += blockSize;
            frameNumber++;
        }
        if (minBlock == int.MaxValue) minBlock = 0;
        if (minFrame == int.MaxValue) minFrame = 0;
        streamInfo[0] = (byte)(minBlock >> 8);
        streamInfo[1] = (byte)minBlock;
        streamInfo[2] = (byte)(maxBlock >> 8);
        streamInfo[3] = (byte)maxBlock;
        streamInfo[4] = (byte)(minFrame >> 16);
        streamInfo[5] = (byte)(minFrame >> 8);
        streamInfo[6] = (byte)minFrame;
        streamInfo[7] = (byte)(maxFrame >> 16);
        streamInfo[8] = (byte)(maxFrame >> 8);
        streamInfo[9] = (byte)maxFrame;

        metadata.Add(streamInfo);
        List<(int Type, byte[] Body)> blocks = [(0, streamInfo)];
        foreach (byte[] extra in options.ExtraMetadata) blocks.Add((extra[0], extra[1..]));
        if (options.SeekTable)
        {
            byte[] table = new byte[seekPoints.Count * 18 + 18];
            int at = 0;
            foreach ((long sample, long offset, int count) in seekPoints)
            {
                WriteBig(table, at, (ulong)sample, 8);
                WriteBig(table, at + 8, (ulong)offset, 8);
                WriteBig(table, at + 16, (ulong)count, 2);
                at += 18;
            }
            WriteBig(table, at, ulong.MaxValue, 8); // a placeholder point
            blocks.Add((3, table));
        }
        output.Write("fLaC"u8);
        for (int i = 0; i < blocks.Count; i++)
        {
            bool last = i == blocks.Count - 1;
            output.WriteByte((byte)((last ? 0x80 : 0) | blocks[i].Type));
            output.WriteByte((byte)(blocks[i].Body.Length >> 16));
            output.WriteByte((byte)(blocks[i].Body.Length >> 8));
            output.WriteByte((byte)blocks[i].Body.Length);
            output.Write(blocks[i].Body);
        }
        for (int i = 0; i < LastFrameOffsets.Count; i++) LastFrameOffsets[i] += (int)output.Length;
        output.Write(frames.ToArray());
        return output.ToArray();
    }

    private static void WriteBig(byte[] target, int at, ulong value, int length)
    {
        for (int i = 0; i < length; i++) target[at + i] = (byte)(value >> (8 * (length - 1 - i)));
    }

    private static byte[] BuildStreamInfo(int[][] channels, FlacEncodeOptions options, int total)
    {
        BitWriter w = new();
        w.Write(0, 16);
        w.Write(0, 16);
        w.Write(0, 24);
        w.Write(0, 24);
        w.Write((ulong)options.SampleRate, 20);
        w.Write((ulong)(channels.Length - 1), 3);
        w.Write((ulong)(options.BitsPerSample - 1), 5);
        w.Write(options.KnownLength ? (ulong)total : 0, 36);
        byte[] md5 = new byte[16];
        if (options.Md5)
        {
            int bytesPerSample = (options.BitsPerSample + 7) / 8;
            byte[] raw = new byte[total * channels.Length * bytesPerSample];
            int at = 0;
            for (int i = 0; i < total; i++)
            {
                foreach (int[] channel in channels)
                {
                    for (int b = 0; b < bytesPerSample; b++) raw[at++] = (byte)(channel[i] >> (8 * b));
                }
            }
            md5 = MD5.HashData(raw);
        }
        byte[] head = w.ToArray();
        return [.. head, .. md5];
    }

    private static byte[] EncodeFrame(int[][] channels, int start, int blockSize, int frameNumber, FlacEncodeOptions options)
    {
        int channelCount = channels.Length;
        int bps = options.BitsPerSample;
        long[][] samples = new long[channelCount][];
        for (int c = 0; c < channelCount; c++)
        {
            samples[c] = new long[blockSize];
            for (int i = 0; i < blockSize; i++) samples[c][i] = channels[c][start + i];
        }
        int assignment = channelCount - 1;
        int[] depths = new int[channelCount];
        Array.Fill(depths, bps);
        if (options.Stereo != StereoMode.Independent && channelCount == 2)
        {
            long[] left = samples[0], right = samples[1];
            long[] side = new long[blockSize], mid = new long[blockSize];
            for (int i = 0; i < blockSize; i++)
            {
                side[i] = left[i] - right[i];
                mid[i] = (left[i] + right[i]) >> 1;
            }
            switch (options.Stereo)
            {
                case StereoMode.LeftSide: samples = [left, side]; depths = [bps, bps + 1]; break;
                case StereoMode.SideRight: samples = [side, right]; depths = [bps + 1, bps]; break;
                default: samples = [mid, side]; depths = [bps, bps + 1]; break;
            }
            assignment = (int)options.Stereo;
        }

        BitWriter w = new();
        w.Write(0x3FFE, 14);
        w.Write(0, 1);
        w.Write(options.VariableBlockSize ? 1UL : 0, 1);
        int blockCode = blockSize switch { 192 => 1, 576 => 2, 1152 => 3, 2304 => 4, 4608 => 5, 256 => 8, 512 => 9, 1024 => 10, 2048 => 11, 4096 => 12, 8192 => 13, 16384 => 14, 32768 => 15, <= 256 => 6, _ => 7 };
        w.Write((ulong)blockCode, 4);
        int rateCode = options.SampleRate switch { 88200 => 1, 176400 => 2, 192000 => 3, 8000 => 4, 16000 => 5, 22050 => 6, 24000 => 7, 32000 => 8, 44100 => 9, 48000 => 10, 96000 => 11, _ => options.SampleRate % 1000 == 0 && options.SampleRate / 1000 < 256 ? 12 : options.SampleRate < 65536 ? 13 : 14 };
        w.Write(options.OmitHeaderRateAndDepth ? 0UL : (ulong)rateCode, 4);
        w.Write((ulong)assignment, 4);
        int depthCode = bps switch { 8 => 1, 12 => 2, 16 => 4, 20 => 5, 24 => 6, 32 => 7, _ => 0 };
        w.Write(options.OmitHeaderRateAndDepth ? 0UL : (ulong)depthCode, 3);
        w.Write(0, 1);
        long number = options.VariableBlockSize ? start : frameNumber;
        WriteCodedNumber(w, number);
        if (blockCode == 6) w.Write((ulong)(blockSize - 1), 8);
        if (blockCode == 7) w.Write((ulong)(blockSize - 1), 16);
        if (!options.OmitHeaderRateAndDepth)
        {
            if (rateCode == 12) w.Write((ulong)(options.SampleRate / 1000), 8);
            if (rateCode == 13) w.Write((ulong)options.SampleRate, 16);
            if (rateCode == 14) w.Write((ulong)(options.SampleRate / 10), 16);
        }
        w.Write(Crc.Crc8(w.Span(0)), 8);

        for (int c = 0; c < samples.Length; c++) EncodeSubframe(w, samples[c], depths[c], options);
        w.Align();
        w.Write(Crc.Crc16(w.Span(0)), 16);
        return w.ToArray();
    }

    private static void WriteCodedNumber(BitWriter w, long value)
    {
        if (value < 0x80)
        {
            w.Write((ulong)value, 8);
            return;
        }
        int continuation = 1;
        while (value >= 1L << (6 * continuation + (6 - continuation))) continuation++;
        // First byte: (continuation + 1) ones, a zero, then the top bits.
        int ones = continuation + 1;
        ulong first = (0xFFUL << (8 - ones)) & 0xFF | (ulong)(value >> (6 * continuation));
        w.Write(first, 8);
        for (int i = continuation - 1; i >= 0; i--) w.Write(0x80UL | (ulong)((value >> (6 * i)) & 0x3F), 8);
    }

    private static void EncodeSubframe(BitWriter w, long[] data, int depth, FlacEncodeOptions options)
    {
        int wasted = options.WastedBits;
        long[] shifted = data;
        if (wasted > 0)
        {
            shifted = new long[data.Length];
            for (int i = 0; i < data.Length; i++) shifted[i] = data[i] >> wasted;
        }
        int effective = depth - wasted;
        SubframeKind kind = options.Kind;
        if (kind == SubframeKind.Auto) kind = BestKind(shifted, effective);
        if (kind == SubframeKind.Constant && shifted.Any(v => v != shifted[0])) kind = SubframeKind.Verbatim;

        w.Write(0, 1);
        int typeCode = kind switch
        {
            SubframeKind.Constant => 0,
            SubframeKind.Verbatim => 1,
            SubframeKind.Lpc => 31 + options.LpcCoefficients.Length,
            _ => 8 + (kind - SubframeKind.Fixed0)
        };
        w.Write((ulong)typeCode, 6);
        if (wasted > 0)
        {
            w.Write(1, 1);
            w.Unary(wasted - 1);
        }
        else w.Write(0, 1);

        switch (kind)
        {
            case SubframeKind.Constant:
                w.WriteSigned(shifted[0], effective);
                break;
            case SubframeKind.Verbatim:
                foreach (long v in shifted) w.WriteSigned(v, effective);
                break;
            case SubframeKind.Lpc:
                {
                    int order = options.LpcCoefficients.Length;
                    for (int i = 0; i < order; i++) w.WriteSigned(shifted[i], effective);
                    w.Write((ulong)(options.LpcPrecision - 1), 4);
                    w.WriteSigned(options.LpcShift, 5);
                    foreach (int coefficient in options.LpcCoefficients) w.WriteSigned(coefficient, options.LpcPrecision);
                    long[] residual = new long[shifted.Length - order];
                    for (int i = order; i < shifted.Length; i++)
                    {
                        long sum = 0;
                        for (int j = 0; j < order; j++) sum += (long)options.LpcCoefficients[j] * shifted[i - 1 - j];
                        residual[i - order] = shifted[i] - (sum >> options.LpcShift);
                    }
                    WriteResidual(w, residual, shifted.Length, order, options);
                    break;
                }
            default:
                {
                    int order = kind - SubframeKind.Fixed0;
                    for (int i = 0; i < order; i++) w.WriteSigned(shifted[i], effective);
                    WriteResidual(w, FixedResidual(shifted, order), shifted.Length, order, options);
                    break;
                }
        }
    }

    private static SubframeKind BestKind(long[] data, int depth)
    {
        if (data.All(v => v == data[0])) return SubframeKind.Constant;
        SubframeKind best = SubframeKind.Verbatim;
        long bestCost = (long)data.Length * depth;
        for (int order = 0; order <= 4 && order < data.Length; order++)
        {
            long cost = FixedResidual(data, order).Sum(v => Math.Abs(v) * 2 + 3) / 2 + order * depth;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = SubframeKind.Fixed0 + order;
            }
        }
        return best;
    }

    private static long[] FixedResidual(long[] x, int order)
    {
        long[] residual = new long[x.Length - order];
        for (int i = order; i < x.Length; i++)
        {
            long prediction = order switch
            {
                0 => 0,
                1 => x[i - 1],
                2 => 2 * x[i - 1] - x[i - 2],
                3 => 3 * x[i - 1] - 3 * x[i - 2] + x[i - 3],
                _ => 4 * x[i - 1] - 6 * x[i - 2] + 4 * x[i - 3] - x[i - 4]
            };
            residual[i - order] = x[i] - prediction;
        }
        return residual;
    }

    private static void WriteResidual(BitWriter w, long[] residual, int blockSize, int order, FlacEncodeOptions options)
    {
        int partitionOrder = options.PartitionOrder;
        while (partitionOrder > 0 && ((blockSize & ((1 << partitionOrder) - 1)) != 0 || (blockSize >> partitionOrder) < order)) partitionOrder--;
        int parameterBits = options.RiceMethod == 0 ? 4 : 5;
        w.Write((ulong)options.RiceMethod, 2);
        w.Write((ulong)partitionOrder, 4);
        int partitions = 1 << partitionOrder;
        int size = blockSize >> partitionOrder;
        int at = 0;
        for (int p = 0; p < partitions; p++)
        {
            int count = p == 0 ? size - order : size;
            ReadOnlySpan<long> part = residual.AsSpan(at, count);
            at += count;
            long max = 0;
            foreach (long v in part) max = Math.Max(max, Math.Abs(v));
            if (options.ForceEscape || max > (1L << 20))
            {
                int width = max == 0 && part.ToArray().All(v => v == 0) ? 0 : 1;
                while (width > 0 && !part.ToArray().All(v => v >= -(1L << (width - 1)) && v < 1L << (width - 1))) width++;
                w.Write((ulong)((1 << parameterBits) - 1), parameterBits);
                w.Write((ulong)width, 5);
                foreach (long v in part) w.WriteSigned(v, width);
                continue;
            }
            int bestParameter = 0;
            long bestBits = long.MaxValue;
            for (int k = 0; k < (1 << parameterBits) - 1 && k < 30; k++)
            {
                long bits = 0;
                foreach (long v in part)
                {
                    ulong folded = v >= 0 ? (ulong)v << 1 : ((ulong)(-v) << 1) - 1;
                    bits += (long)(folded >> k) + 1 + k;
                }
                if (bits < bestBits)
                {
                    bestBits = bits;
                    bestParameter = k;
                }
            }
            w.Write((ulong)bestParameter, parameterBits);
            foreach (long v in part)
            {
                ulong folded = v >= 0 ? (ulong)v << 1 : ((ulong)(-v) << 1) - 1;
                w.Unary((int)(folded >> bestParameter));
                w.Write(folded & ((1UL << bestParameter) - 1), bestParameter);
            }
        }
    }
}
