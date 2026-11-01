namespace Aibell.Archive.Rar;

/// <summary>
/// The RAR 5.0 decompressor: blocks of LZ77 symbols under canonical Huffman codes (tables carried in the block), four recent
/// distances, and the delta, x86 (E8/E8E9) and ARM filters. The window, the tables and the recent distances persist between the
/// files of a solid archive. Derived from the format as read in libarchive's BSD-licensed RAR5 reader.
/// </summary>
internal sealed class Rar5Decoder
{
    private const int BitLengthSize = 20, MainSize = 306, DistanceSize = 64, LowDistanceSize = 16, RepeatSize = 44;
    private const int TableSize = MainSize + DistanceSize + LowDistanceSize + RepeatSize;

    private readonly byte[] window;
    private readonly int mask;
    private readonly RarHuffman bitLengthCode = new(), mainCode = new(), distanceCode = new(), lowDistanceCode = new(), repeatCode = new();
    private readonly int[] recentDistances = new int[4];
    private readonly List<Filter> filters = [];
    private long position;
    private int lastLength;
    private bool haveTables;

    public Rar5Decoder(int windowSize)
    {
        if (windowSize < 1 << 16 || (windowSize & windowSize - 1) != 0) throw new ArgumentOutOfRangeException(nameof(windowSize));
        window = new byte[windowSize];
        mask = windowSize - 1;
    }

    private sealed class Filter
    {
        public int Type;
        public long Start;
        public int Length;
        public int Channels;
    }

    private void Reset()
    {
        Array.Clear(window);
        Array.Clear(recentDistances);
        position = 0;
        lastLength = 0;
        haveTables = false;
    }

    public byte[] Unpack(byte[] packed, int offset, int length, long unpackedSize, bool solid)
    {
        if (!solid) Reset();
        filters.Clear();
        var output = new byte[unpackedSize];
        int produced = 0;
        int blockPosition = offset;
        int end = offset + length;

        // Blocks are read up to the one flagged as last, even when the file is empty: an empty file can carry the tables of the files after it.
        while (blockPosition < end || produced < unpackedSize)
        {
            if (blockPosition + 3 > end) throw new InvalidDataException("The RAR data is truncated.");
            int flags = packed[blockPosition];
            int checksum = packed[blockPosition + 1];
            int sizeBytes = (flags >> 3 & 7) + 1;
            if (sizeBytes > 3) throw new InvalidDataException("Unsupported RAR block header.");
            if (blockPosition + 2 + sizeBytes > end) throw new InvalidDataException("The RAR data is truncated.");
            int blockSize = 0;
            for (int i = 0; i < sizeBytes; i++) blockSize |= packed[blockPosition + 2 + i] << 8 * i;
            int expected = 0x5A ^ flags ^ blockSize & 0xFF ^ blockSize >> 8 & 0xFF ^ blockSize >> 16 & 0xFF;
            if (expected != checksum) throw new InvalidDataException("A RAR block header checksum is wrong.");
            int dataStart = blockPosition + 2 + sizeBytes;
            if (blockSize < 1 || dataStart + blockSize > end) throw new InvalidDataException("The RAR data is truncated.");

            var reader = new RarBitReader(packed, dataStart, blockSize);
            long blockEndBit = (long)dataStart * 8 + (long)(blockSize - 1) * 8 + (flags & 7) + 1;
            if ((flags & 0x80) != 0) ReadTables(reader);
            else if (!haveTables) throw new InvalidDataException("A RAR block has no code tables.");

            DecodeBlock(reader, blockEndBit, output, ref produced, unpackedSize);

            blockPosition = dataStart + blockSize;
            if ((flags & 0x40) != 0) break;
        }

        if (produced < unpackedSize) throw new InvalidDataException("The RAR data ended before the file did.");
        ApplyFilters(output);
        return output;
    }

    private void ReadTables(RarBitReader reader)
    {
        var lengths = new byte[BitLengthSize];
        for (int i = 0; i < BitLengthSize;)
        {
            int value = (int)reader.Read(4);
            if (value == 15)
            {
                int count = (int)reader.Read(4);
                if (count == 0) lengths[i++] = 15;
                else
                {
                    for (int k = 0; k < count + 2 && i < BitLengthSize; k++) lengths[i++] = 0;
                }
            }
            else lengths[i++] = (byte)value;
            if (reader.Overrun) throw new InvalidDataException("The RAR data is truncated.");
        }
        bitLengthCode.Build(lengths);

        var table = new byte[TableSize];
        for (int i = 0; i < TableSize;)
        {
            if (reader.Overrun) throw new InvalidDataException("The RAR data is truncated.");
            int symbol = bitLengthCode.Decode(reader);
            if (symbol < 16) table[i++] = (byte)symbol;
            else if (symbol < 18)
            {
                int count = symbol == 16 ? (int)reader.Read(3) + 3 : (int)reader.Read(7) + 11;
                if (i == 0) throw new InvalidDataException("Bad RAR code table.");
                for (; count > 0 && i < TableSize; count--, i++) table[i] = table[i - 1];
            }
            else
            {
                int count = symbol == 18 ? (int)reader.Read(3) + 3 : (int)reader.Read(7) + 11;
                for (; count > 0 && i < TableSize; count--) table[i++] = 0;
            }
        }

        mainCode.Build(table.AsSpan(0, MainSize));
        distanceCode.Build(table.AsSpan(MainSize, DistanceSize));
        lowDistanceCode.Build(table.AsSpan(MainSize + DistanceSize, LowDistanceSize));
        repeatCode.Build(table.AsSpan(MainSize + DistanceSize + LowDistanceSize, RepeatSize));
        haveTables = true;
    }

    private void DecodeBlock(RarBitReader reader, long endBit, byte[] output, ref int produced, long unpackedSize)
    {
        while (reader.BitPosition < endBit)
        {
            if (reader.Overrun) throw new InvalidDataException("The RAR data is truncated.");
            int symbol = mainCode.Decode(reader);
            if (symbol < 256)
            {
                if (produced >= unpackedSize) throw new InvalidDataException("The RAR data is longer than the file.");
                window[position & mask] = (byte)symbol;
                position++;
                output[produced++] = (byte)symbol;
                continue;
            }
            if (symbol == 256)
            {
                ReadFilter(reader, produced);
                continue;
            }
            if (symbol == 257)
            {
                if (lastLength != 0) Copy(lastLength, recentDistances[0], output, ref produced, unpackedSize);
                continue;
            }

            int length, distance;
            if (symbol < 262)
            {
                int index = symbol - 258;
                distance = recentDistances[index];
                for (int i = index; i > 0; i--) recentDistances[i] = recentDistances[i - 1];
                recentDistances[0] = distance;
                length = ReadLength(reader, repeatCode.Decode(reader));
            }
            else
            {
                length = ReadLength(reader, symbol - 262);
                distance = ReadDistance(reader);
                if (distance > 0x100)
                {
                    length++;
                    if (distance > 0x2000)
                    {
                        length++;
                        if (distance > 0x40000) length++;
                    }
                }
                recentDistances[3] = recentDistances[2];
                recentDistances[2] = recentDistances[1];
                recentDistances[1] = recentDistances[0];
                recentDistances[0] = distance;
            }
            lastLength = length;
            Copy(length, distance, output, ref produced, unpackedSize);
        }
    }

    private static int ReadLength(RarBitReader reader, int code)
    {
        if (code < 8) return 2 + code;
        int bits = code / 4 - 1;
        int length = 2 + ((4 | code & 3) << bits);
        return length + (int)reader.Read(bits);
    }

    private int ReadDistance(RarBitReader reader)
    {
        int slot = distanceCode.Decode(reader);
        int distance = 1;
        int bits;
        if (slot < 4)
        {
            bits = 0;
            distance += slot;
        }
        else
        {
            bits = slot / 2 - 1;
            distance += (2 | slot & 1) << bits;
        }
        if (bits > 0)
        {
            if (bits >= 4)
            {
                if (bits > 4) distance += (int)reader.Read(bits - 4) << 4;
                distance += lowDistanceCode.Decode(reader);
            }
            else distance += (int)reader.Read(bits);
        }
        return distance;
    }

    private void Copy(int length, int distance, byte[] output, ref int produced, long unpackedSize)
    {
        if (produced + length > unpackedSize) throw new InvalidDataException("The RAR data is longer than the file.");
        for (int i = 0; i < length; i++)
        {
            byte value = window[position - distance & mask];
            window[position & mask] = value;
            position++;
            output[produced++] = value;
        }
    }

    // --- Filters --------------------------------------------------------------------------------------------------------------

    private static uint ReadFilterNumber(RarBitReader reader)
    {
        int bytes = (int)reader.Read(2) + 1;
        uint value = 0;
        for (int i = 0; i < bytes; i++) value |= reader.Read(8) << 8 * i;
        return value;
    }

    private void ReadFilter(RarBitReader reader, int produced)
    {
        uint start = ReadFilterNumber(reader);
        uint length = ReadFilterNumber(reader);
        int type = (int)reader.Read(3);
        if (length is < 4 or > 0x400000) throw new InvalidDataException("Invalid RAR filter.");
        var filter = new Filter { Type = type, Start = produced + (long)start, Length = (int)length };
        if (type == 0) filter.Channels = (int)reader.Read(5) + 1;
        filters.Add(filter);
    }

    private void ApplyFilters(byte[] output)
    {
        foreach (Filter filter in filters)
        {
            if (filter.Start + filter.Length > output.Length) throw new InvalidDataException("A RAR filter lies outside the file.");
            Span<byte> data = output.AsSpan((int)filter.Start, filter.Length);
            switch (filter.Type)
            {
                case 0: Delta(data, filter.Channels); break;
                case 1: E8(data, filter.Start, extended: false); break;
                case 2: E8(data, filter.Start, extended: true); break;
                case 3: Arm(data, filter.Start); break;
                default: throw new NotSupportedException("This RAR filter is not supported.");
            }
        }
    }

    private static void Delta(Span<byte> data, int channels)
    {
        byte[] source = data.ToArray();
        int next = 0;
        for (int channel = 0; channel < channels; channel++)
        {
            byte previous = 0;
            for (int index = channel; index < data.Length; index += channels)
            {
                previous = (byte)(previous - source[next++]);
                data[index] = previous;
            }
        }
    }

    private static void E8(Span<byte> data, long start, bool extended)
    {
        const uint FileSize = 0x1000000;
        byte[] source = data.ToArray();
        for (int i = 0; i < data.Length - 4;)
        {
            byte b = source[i++];
            if (b != 0xE8 && !(extended && b == 0xE9)) continue;
            uint offset = (uint)((i + start) % FileSize);
            uint address = BitConverter.ToUInt32(source, i);
            if ((address & 0x80000000) != 0)
            {
                if (((address + offset) & 0x80000000) == 0) BitConverter.TryWriteBytes(data.Slice(i, 4), address + FileSize);
            }
            else if (((address - FileSize) & 0x80000000) != 0) BitConverter.TryWriteBytes(data.Slice(i, 4), address - offset);
            i += 4;
        }
    }

    private static void Arm(Span<byte> data, long start)
    {
        for (int i = 0; i < data.Length - 3; i += 4)
        {
            if (data[i + 3] != 0xEB) continue;
            uint offset = BitConverter.ToUInt32(data.Slice(i, 4)) & 0x00FFFFFF;
            offset -= (uint)((i + start) / 4);
            offset = offset & 0x00FFFFFF | 0xEB000000;
            BitConverter.TryWriteBytes(data.Slice(i, 4), offset);
        }
    }
}
