namespace Aibell.Archive.Rar;

/// <summary>
/// The RAR 3.x/4.x decompressor (unpack version 29): LZ77 with canonical Huffman codes, the PPMd variant H text mode, and the
/// standard filters (E8/E8E9 call fixing, delta, RGB, audio). The sliding window, the code tables, the repeat distances and the PPMd
/// model persist between the files of a solid archive. Derived from the format as read in libarchive's BSD-licensed RAR reader.
/// </summary>
internal sealed class Rar3Decoder
{
    private const int MainSize = 299, OffsetSize = 60, LowOffsetSize = 17, LengthSize = 28;
    private const int TableSize = MainSize + OffsetSize + LowOffsetSize + LengthSize;

    private static readonly byte[] LengthBases = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224];
    private static readonly byte[] LengthBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5];

    private static readonly int[] OffsetBases =
    [
        0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096, 6144, 8192, 12288,
        16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144, 327680, 393216, 458752, 524288, 589824, 655360, 720896,
        786432, 851968, 917504, 983040, 1048576, 1310720, 1572864, 1835008, 2097152, 2359296, 2621440, 2883584, 3145728, 3407872,
        3670016, 3932160
    ];

    private static readonly byte[] OffsetBits =
    [
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16,
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18
    ];

    private static readonly byte[] ShortBases = [0, 4, 8, 16, 32, 64, 128, 192];
    private static readonly byte[] ShortBits = [2, 2, 3, 4, 5, 6, 6, 6];

    private readonly byte[] window;
    private readonly int mask;
    private readonly byte[] lengthTable = new byte[TableSize];
    private readonly RarHuffman mainCode = new(), offsetCode = new(), lowOffsetCode = new(), lengthCode = new();
    private readonly int[] oldOffset = new int[4];
    private readonly List<Program> programs = [];
    private readonly List<Filter> filters = [];
    private long position;
    private int lastLength, lastOffset, lastLowOffset, lowOffsetRepeats;
    private bool needTables = true;
    private bool isPpmBlock;
    private int ppmEscape = 2;
    private RarPpmd? ppmd;
    private int lastFilterNumber;

    public Rar3Decoder(int windowSize)
    {
        if (windowSize < 1 << 16 || (windowSize & windowSize - 1) != 0) throw new ArgumentOutOfRangeException(nameof(windowSize));
        window = new byte[windowSize];
        mask = windowSize - 1;
    }

    private sealed class Program
    {
        public ulong Fingerprint;
        public uint OldLength;
        public int UsageCount;
    }

    private sealed class Filter
    {
        public required Program Program;
        public required uint[] Registers;
        public long Start;
        public uint Length;
    }

    /// <summary>Forgets everything a previous file left behind (a file that does not continue a solid archive).</summary>
    private void Reset()
    {
        Array.Clear(window);
        Array.Clear(lengthTable);
        Array.Clear(oldOffset);
        position = 0;
        lastLength = lastOffset = lastLowOffset = lowOffsetRepeats = 0;
        needTables = true;
        isPpmBlock = false;
        ppmEscape = 2;
        ppmd = null;
        programs.Clear();
        lastFilterNumber = 0;
    }

    /// <summary>Decompresses one file of <paramref name="unpackedSize"/> bytes from the packed bytes.</summary>
    public byte[] Unpack(byte[] packed, int offset, int length, long unpackedSize, bool solid)
    {
        if (!solid) Reset();
        filters.Clear();
        var reader = new RarBitReader(packed, offset, length);
        var output = new byte[unpackedSize];
        long fileStart = position;
        int produced = 0;

        // An empty file normally has no data; if it has, it carries the tables (and the end marker) for the files after it.
        if (unpackedSize == 0 && length == 0) return output;
        if (unpackedSize == 0 && needTables)
        {
            ParseTables(reader);
            needTables = false;
        }

        while (true)
        {
            if (produced >= unpackedSize)
            {
                ConsumeEndMarker(reader);
                break;
            }
            if (reader.Overrun) throw new InvalidDataException("The RAR data is truncated.");
            if (needTables)
            {
                ParseTables(reader);
                needTables = false;
            }

            if (isPpmBlock)
            {
                if (!DecodePpm(reader, output, ref produced)) break;
                continue;
            }

            int symbol = mainCode.Decode(reader);
            if (symbol < 256)
            {
                window[position & mask] = (byte)symbol;
                position++;
                output[produced++] = (byte)symbol;
                continue;
            }
            if (symbol == 256)
            {
                if (reader.Read(1) == 0)
                {
                    // The end of the file; whether the next file starts with new tables is decided by one more bit.
                    needTables = reader.Read(1) != 0;
                    break;
                }
                ParseTables(reader);
                needTables = false;
                continue;
            }

            int matchOffset, matchLength;
            if (symbol == 257)
            {
                ReadFilter(reader, () => reader.ReadByte());
                continue;
            }
            if (symbol == 258)
            {
                if (lastLength == 0) continue;
                matchOffset = lastOffset;
                matchLength = lastLength;
            }
            else if (symbol <= 262)
            {
                int index = symbol - 259;
                matchOffset = oldOffset[index];
                int lengthSymbol = lengthCode.Decode(reader);
                if (lengthSymbol >= LengthBases.Length) throw new InvalidDataException("Bad RAR data.");
                matchLength = LengthBases[lengthSymbol] + 2 + (int)reader.Read(LengthBits[lengthSymbol]);
                for (int i = index; i > 0; i--) oldOffset[i] = oldOffset[i - 1];
                oldOffset[0] = matchOffset;
            }
            else if (symbol <= 270)
            {
                matchOffset = ShortBases[symbol - 263] + 1 + (int)reader.Read(ShortBits[symbol - 263]);
                matchLength = 2;
                PushOffset(matchOffset);
            }
            else
            {
                int lengthIndex = symbol - 271;
                if (lengthIndex >= LengthBases.Length) throw new InvalidDataException("Bad RAR data.");
                matchLength = LengthBases[lengthIndex] + 3 + (int)reader.Read(LengthBits[lengthIndex]);
                int offsetSymbol = offsetCode.Decode(reader);
                if (offsetSymbol >= OffsetBases.Length) throw new InvalidDataException("Bad RAR data.");
                matchOffset = OffsetBases[offsetSymbol] + 1;
                int bits = OffsetBits[offsetSymbol];
                if (bits > 0)
                {
                    if (offsetSymbol > 9)
                    {
                        if (bits > 4) matchOffset += (int)reader.Read(bits - 4) << 4;
                        if (lowOffsetRepeats > 0)
                        {
                            lowOffsetRepeats--;
                            matchOffset += lastLowOffset;
                        }
                        else
                        {
                            int low = lowOffsetCode.Decode(reader);
                            if (low == 16)
                            {
                                lowOffsetRepeats = 15;
                                matchOffset += lastLowOffset;
                            }
                            else
                            {
                                matchOffset += low;
                                lastLowOffset = low;
                            }
                        }
                    }
                    else matchOffset += (int)reader.Read(bits);
                }
                if (matchOffset >= 0x40000) matchLength++;
                if (matchOffset >= 0x2000) matchLength++;
                PushOffset(matchOffset);
            }

            lastOffset = matchOffset;
            lastLength = matchLength;
            CopyMatch(matchOffset, matchLength, output, ref produced);
        }

        if (reader.Overrun) throw new InvalidDataException("The RAR data is truncated.");
        if (produced < unpackedSize) throw new InvalidDataException("The RAR data ended before the file did.");
        ApplyFilters(output, fileStart);
        return output;
    }

    private void PushOffset(int value)
    {
        oldOffset[3] = oldOffset[2];
        oldOffset[2] = oldOffset[1];
        oldOffset[1] = oldOffset[0];
        oldOffset[0] = value;
    }

    private void CopyMatch(int distance, int length, byte[] output, ref int produced)
    {
        for (int i = 0; i < length; i++)
        {
            byte value = window[position - distance & mask];
            window[position & mask] = value;
            position++;
            if (produced < output.Length) output[produced++] = value;
        }
    }

    // After the last byte of a file the stream normally holds the end marker; reading it tells whether the next file keeps the tables.
    private void ConsumeEndMarker(RarBitReader reader)
    {
        if (needTables) return;
        long saved = reader.BitPosition;
        try
        {
            if (isPpmBlock)
            {
                int symbol = ppmd!.DecodeSymbol();
                if (symbol == ppmEscape)
                {
                    int code = ppmd.DecodeSymbol();
                    if (code is 0 or 2) needTables = true;
                }
                return;
            }
            if (mainCode.Decode(reader) == 256 && reader.Read(1) == 0) needTables = reader.Read(1) != 0;
            else
            {
                reader.BitPosition = saved;
                needTables = true;
            }
        }
        catch (InvalidDataException)
        {
            reader.BitPosition = saved;
            needTables = true;
        }
    }

    // --- Code tables ----------------------------------------------------------------------------------------------------------

    private void ParseTables(RarBitReader reader)
    {
        reader.AlignToByte();
        isPpmBlock = reader.Read(1) != 0;
        if (isPpmBlock)
        {
            ParsePpmHeader(reader);
            return;
        }

        lastLowOffset = 0;
        lowOffsetRepeats = 0;
        if (reader.Read(1) == 0) Array.Clear(lengthTable);

        var bitLengths = new byte[20];
        for (int i = 0; i < 20;)
        {
            int value = (int)reader.Read(4);
            bitLengths[i++] = (byte)value;
            if (value == 0xF)
            {
                int zeros = (int)reader.Read(4);
                if (zeros != 0)
                {
                    i--;
                    for (int j = 0; j < zeros + 2 && i < 20; j++) bitLengths[i++] = 0;
                }
            }
        }
        var precode = new RarHuffman();
        precode.Build(bitLengths);

        for (int i = 0; i < TableSize;)
        {
            if (reader.Overrun) throw new InvalidDataException("The RAR data is truncated.");
            int value = precode.Decode(reader);
            if (value < 16)
            {
                lengthTable[i] = (byte)(lengthTable[i] + value & 0xF);
                i++;
            }
            else if (value < 18)
            {
                if (i == 0) throw new InvalidDataException("Bad RAR code table.");
                int count = value == 16 ? (int)reader.Read(3) + 3 : (int)reader.Read(7) + 11;
                for (int j = 0; j < count && i < TableSize; j++, i++) lengthTable[i] = lengthTable[i - 1];
            }
            else
            {
                int count = value == 18 ? (int)reader.Read(3) + 3 : (int)reader.Read(7) + 11;
                for (int j = 0; j < count && i < TableSize; j++) lengthTable[i++] = 0;
            }
        }

        mainCode.Build(lengthTable.AsSpan(0, MainSize));
        offsetCode.Build(lengthTable.AsSpan(MainSize, OffsetSize));
        lowOffsetCode.Build(lengthTable.AsSpan(MainSize + OffsetSize, LowOffsetSize));
        lengthCode.Build(lengthTable.AsSpan(MainSize + OffsetSize + LowOffsetSize, LengthSize));
    }

    private void ParsePpmHeader(RarBitReader reader)
    {
        int flags = (int)reader.Read(7);
        int memory = 0;
        if ((flags & 0x20) != 0) memory = ((int)reader.Read(8) + 1) << 20;
        if ((flags & 0x40) != 0) ppmEscape = (int)reader.Read(8);
        if ((flags & 0x20) != 0)
        {
            int maxOrder = (flags & 0x1F) + 1;
            if (maxOrder > 16) maxOrder = 16 + (maxOrder - 16) * 3;
            if (maxOrder == 1 || memory == 0) throw new InvalidDataException("Bad RAR PPMd parameters.");
            ppmd = new RarPpmd(memory, maxOrder, reader);
        }
        else if (ppmd is null) throw new InvalidDataException("Invalid PPMd sequence in the RAR data.");
        else ppmd.StartRange(reader);
    }

    // One step of a PPMd block; false when the block ends the file.
    private bool DecodePpm(RarBitReader reader, byte[] output, ref int produced)
    {
        int symbol = ppmd!.DecodeSymbol();
        if (symbol != ppmEscape)
        {
            EmitLiteral(symbol, output, ref produced);
            return true;
        }
        int code = ppmd.DecodeSymbol();
        switch (code)
        {
            case 0:
                needTables = true;
                return true;
            case 2:
                needTables = true;
                return false;
            case 3:
                ReadFilter(reader, () => ppmd.DecodeSymbol());
                return true;
            case 4:
                {
                    int distance = 0;
                    for (int i = 2; i >= 0; i--) distance |= ppmd.DecodeSymbol() << i * 8;
                    int length = ppmd.DecodeSymbol();
                    CopyMatch(distance + 2, length + 32, output, ref produced);
                    return true;
                }
            case 5:
                {
                    int length = ppmd.DecodeSymbol();
                    CopyMatch(1, length + 4, output, ref produced);
                    return true;
                }
            default:
                EmitLiteral(symbol, output, ref produced);
                return true;
        }
    }

    private void EmitLiteral(int symbol, byte[] output, ref int produced)
    {
        window[position & mask] = (byte)symbol;
        position++;
        if (produced < output.Length) output[produced++] = (byte)symbol;
    }

    // --- Filters --------------------------------------------------------------------------------------------------------------

    private void ReadFilter(RarBitReader reader, Func<int> nextByte)
    {
        int flags = nextByte();
        int length = (flags & 7) + 1;
        if (length == 7) length = nextByte() + 7;
        else if (length == 8) length = nextByte() << 8 | nextByte();
        var code = new byte[length];
        for (int i = 0; i < length; i++) code[i] = (byte)nextByte();
        if (reader.Overrun) throw new InvalidDataException("The RAR data is truncated.");
        ParseFilter(code, flags);
    }

    private sealed class MemoryBits(byte[] bytes, int offset)
    {
        private int offset = offset;
        private ulong bits;
        private int available;

        public bool AtEnd { get; private set; }

        public uint Read(int count)
        {
            while (available < count && offset < bytes.Length)
            {
                bits = bits << 8 | bytes[offset++];
                available += 8;
            }
            if (count > available)
            {
                AtEnd = true;
                return 0;
            }
            available -= count;
            return (uint)(bits >> available & (1UL << count) - 1);
        }

        public uint Number()
        {
            switch (Read(2))
            {
                case 0: return Read(4);
                case 1:
                    uint value = Read(8);
                    if (value >= 16) return value;
                    return 0xFFFFFF00 | value << 4 | Read(4);
                case 2: return Read(16);
                default: return Read(32);
            }
        }
    }

    private void ParseFilter(byte[] bytes, int flags)
    {
        var bits = new MemoryBits(bytes, 0);
        int number;
        if ((flags & 0x80) != 0)
        {
            number = (int)bits.Number();
            if (number == 0)
            {
                filters.Clear();
                programs.Clear();
            }
            else number--;
            if (number > programs.Count) throw new InvalidDataException("Bad RAR filter.");
            lastFilterNumber = number;
        }
        else number = lastFilterNumber;

        Program? program = number < programs.Count ? programs[number] : null;
        if (program is not null) program.UsageCount++;

        long start = bits.Number() + position;
        if ((flags & 0x40) != 0) start += 258;
        uint length = (flags & 0x20) != 0 ? bits.Number() : program?.OldLength ?? 0;
        if (length > window.Length) throw new InvalidDataException("Bad RAR filter.");

        var registers = new uint[8];
        registers[3] = 0x3C000;
        registers[4] = length;
        registers[5] = (uint)(program?.UsageCount ?? 0);
        registers[7] = 0x40000;
        if ((flags & 0x10) != 0)
        {
            uint registerMask = bits.Read(7);
            for (int i = 0; i < 7; i++)
            {
                if ((registerMask & 1u << i) != 0) registers[i] = bits.Number();
            }
        }

        if (program is null)
        {
            uint codeLength = bits.Number();
            if (codeLength == 0 || codeLength > 0x10000) throw new InvalidDataException("Bad RAR filter.");
            var code = new byte[codeLength];
            for (int i = 0; i < code.Length; i++) code[i] = (byte)bits.Read(8);
            byte xor = 0;
            for (int i = 1; i < code.Length; i++) xor ^= code[i];
            if (xor != code[0]) throw new InvalidDataException("Bad RAR filter checksum.");
            program = new Program { Fingerprint = RarCrc32.Compute(code) | (ulong)code.Length << 32, UsageCount = 0 };
            programs.Add(program);
        }
        program.OldLength = length;
        if (bits.AtEnd) throw new InvalidDataException("Bad RAR filter.");
        filters.Add(new Filter { Program = program, Registers = registers, Start = start, Length = length });
    }

    private void ApplyFilters(byte[] output, long fileStart)
    {
        foreach (Filter filter in filters)
        {
            long relative = filter.Start - fileStart;
            if (relative < 0 || relative + filter.Length > output.Length) throw new InvalidDataException("A RAR filter lies outside the file.");
            Span<byte> data = output.AsSpan((int)relative, (int)filter.Length);
            switch (filter.Program.Fingerprint)
            {
                case 0x1D0E06077D: Delta(data, filter.Registers[0]); break;
                case 0x35AD576887: E8(data, relative, extended: false); break;
                case 0x393CD7E57E: E8(data, relative, extended: true); break;
                case 0x951C2C5DC8: Rgb(data, filter.Registers[0], filter.Registers[1]); break;
                case 0xD8BC85E701: Audio(data, filter.Registers[0]); break;
                default: throw new NotSupportedException("This RAR archive uses a custom filter program, which is not supported.");
            }
        }
    }

    private static void Delta(Span<byte> data, uint channels)
    {
        if (channels is 0 or > 128) throw new InvalidDataException("Bad RAR delta filter.");
        byte[] source = data.ToArray();
        int next = 0;
        for (int channel = 0; channel < channels; channel++)
        {
            byte last = 0;
            for (int index = channel; index < data.Length; index += (int)channels)
            {
                last = (byte)(last - source[next++]);
                data[index] = last;
            }
        }
    }

    private static void E8(Span<byte> data, long position, bool extended)
    {
        const uint FileSize = 0x1000000;
        if (data.Length <= 4) return;
        for (int i = 0; i <= data.Length - 5; i++)
        {
            if (data[i] != 0xE8 && !(extended && data[i] == 0xE9)) continue;
            uint current = (uint)position + (uint)i + 1;
            int address = BitConverter.ToInt32(data.Slice(i + 1, 4));
            if (address < 0 && current >= (uint)-(long)address) BitConverter.TryWriteBytes(data.Slice(i + 1, 4), (uint)address + FileSize);
            else if (address >= 0 && (uint)address < FileSize) BitConverter.TryWriteBytes(data.Slice(i + 1, 4), (uint)address - current);
            i += 4;
        }
    }

    private static void Rgb(Span<byte> data, uint stride, uint byteOffset)
    {
        int length = data.Length;
        if (stride > length || length < 3 || byteOffset > 2) throw new InvalidDataException("Bad RAR color filter.");
        byte[] source = data.ToArray();
        var destination = new byte[length];
        int next = 0;
        for (int i = 0; i < 3; i++)
        {
            byte value = 0;
            int previous = i - (int)stride;
            for (int j = i; j < length; j += 3)
            {
                if (previous >= 0)
                {
                    int delta1 = Math.Abs(destination[previous + 3] - destination[previous]);
                    int delta2 = Math.Abs(value - destination[previous]);
                    int delta3 = Math.Abs(destination[previous + 3] - destination[previous] + value - destination[previous]);
                    if (delta1 > delta2 || delta1 > delta3) value = delta2 <= delta3 ? destination[previous + 3] : destination[previous];
                }
                value = (byte)(value - source[next++]);
                destination[j] = value;
                previous += 3;
            }
        }
        for (int i = (int)byteOffset; i < length - 2; i += 3)
        {
            destination[i] += destination[i + 1];
            destination[i + 2] += destination[i + 1];
        }
        destination.CopyTo(data);
    }

    private static void Audio(Span<byte> data, uint channels)
    {
        if (channels is 0 or > 128) throw new InvalidDataException("Bad RAR audio filter.");
        byte[] source = data.ToArray();
        int next = 0;
        for (int channel = 0; channel < channels; channel++)
        {
            var weight = new int[3];
            var delta = new int[3];
            var error = new int[7];
            int lastDelta = 0, count = 0;
            byte lastByte = 0;
            for (int j = channel; j < data.Length; j += (int)channels)
            {
                int input = (sbyte)source[next++];
                delta[2] = delta[1];
                delta[1] = lastDelta - delta[0];
                delta[0] = lastDelta;
                int predicted = (8 * lastByte + weight[0] * delta[0] + weight[1] * delta[1] + weight[2] * delta[2] >> 3) & 0xFF;
                byte value = (byte)(predicted - input);
                int predictionError = input * 8;
                error[0] += Math.Abs(predictionError);
                error[1] += Math.Abs(predictionError - delta[0]);
                error[2] += Math.Abs(predictionError + delta[0]);
                error[3] += Math.Abs(predictionError - delta[1]);
                error[4] += Math.Abs(predictionError + delta[1]);
                error[5] += Math.Abs(predictionError - delta[2]);
                error[6] += Math.Abs(predictionError + delta[2]);
                lastDelta = (sbyte)(value - lastByte);
                data[j] = lastByte = value;
                if ((count++ & 0x1F) != 0) continue;
                int best = 0;
                for (int k = 1; k < 7; k++)
                {
                    if (error[k] < error[best]) best = k;
                }
                Array.Clear(error);
                switch (best)
                {
                    case 1: if (weight[0] >= -16) weight[0]--; break;
                    case 2: if (weight[0] < 16) weight[0]++; break;
                    case 3: if (weight[1] >= -16) weight[1]--; break;
                    case 4: if (weight[1] < 16) weight[1]++; break;
                    case 5: if (weight[2] >= -16) weight[2]--; break;
                    case 6: if (weight[2] < 16) weight[2]++; break;
                }
            }
        }
    }
}
