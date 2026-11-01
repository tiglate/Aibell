using System.Runtime.CompilerServices;

namespace Aibell.Audio.Vorbis;

/// <summary>A Vorbis codebook (section 3): a Huffman code over the entries and, optionally, a table of output vectors.</summary>
internal sealed class VorbisCodebook
{
    private const int FastBits = 10;
    private const int MaximumEntries = 1 << 20;
    private const int MaximumVectorValues = 1 << 22;
    private const int NoCode = int.MinValue;

    private readonly int[] fast;
    private readonly int[] trie;
    private readonly float[]? vectors;

    private VorbisCodebook(int dimensions, int entries, int[] fast, int[] trie, float[]? vectors)
    {
        Dimensions = dimensions;
        Entries = entries;
        this.fast = fast;
        this.trie = trie;
        this.vectors = vectors;
    }

    public int Dimensions { get; }

    public int Entries { get; }

    public bool HasVectors => vectors is not null;

    public static VorbisCodebook Read(ref VorbisBitReader reader)
    {
        if (reader.Read(24) != 0x564342) throw new InvalidDataException("A Vorbis codebook has no sync pattern.");
        int dimensions = (int)reader.Read(16);
        int entries = (int)reader.Read(24);
        if (dimensions == 0 || entries == 0 || entries > MaximumEntries) throw new InvalidDataException("A Vorbis codebook has an unsupported size.");
        int[] lengths = new int[entries];
        bool ordered = reader.Read(1) == 1;
        if (!ordered)
        {
            bool sparse = reader.Read(1) == 1;
            for (int i = 0; i < entries; i++)
            {
                if (sparse && reader.Read(1) == 0) lengths[i] = 0;
                else lengths[i] = (int)reader.Read(5) + 1;
            }
        }
        else
        {
            int current = 0;
            int length = (int)reader.Read(5) + 1;
            while (current < entries)
            {
                int number = (int)reader.Read(Ilog(entries - current));
                if (current + number > entries || length > 32) throw new InvalidDataException("A Vorbis codebook's ordered lengths are invalid.");
                for (int i = 0; i < number; i++) lengths[current + i] = length;
                current += number;
                length++;
                if (reader.EndOfPacket) throw new InvalidDataException("A Vorbis codebook ends early.");
            }
        }
        if (reader.EndOfPacket) throw new InvalidDataException("A Vorbis codebook ends early.");

        int lookupType = (int)reader.Read(4);
        float[]? vectors = null;
        if (lookupType is 1 or 2)
        {
            float minimum = Float32Unpack(reader.Read(32));
            float delta = Float32Unpack(reader.Read(32));
            int valueBits = (int)reader.Read(4) + 1;
            bool sequence = reader.Read(1) == 1;
            long lookupValues = lookupType == 1 ? Lookup1Values(entries, dimensions) : (long)entries * dimensions;
            if (lookupValues > MaximumVectorValues || (long)entries * dimensions > MaximumVectorValues) throw new InvalidDataException("A Vorbis codebook's vector table is too large.");
            int[] multiplicands = new int[lookupValues];
            for (int i = 0; i < multiplicands.Length; i++) multiplicands[i] = (int)reader.Read(valueBits);
            if (reader.EndOfPacket) throw new InvalidDataException("A Vorbis codebook ends early.");
            vectors = new float[(long)entries * dimensions];
            for (int entry = 0; entry < entries; entry++)
            {
                float last = 0;
                long divisor = 1;
                for (int d = 0; d < dimensions; d++)
                {
                    long offset = lookupType == 1 ? entry / divisor % lookupValues : (long)entry * dimensions + d;
                    float value = multiplicands[offset] * delta + minimum + last;
                    vectors[(long)entry * dimensions + d] = value;
                    if (sequence) last = value;
                    if (lookupType == 1) divisor *= lookupValues;
                }
            }
        }
        else if (lookupType != 0) throw new InvalidDataException("A Vorbis codebook has a reserved lookup type.");

        (int[] fast, int[] trie) = BuildDecoder(lengths);
        return new VorbisCodebook(dimensions, entries, fast, trie, vectors);
    }

    // Codewords are assigned to the entries in order, each the lowest unused value of its length. Free space is tracked as one free subtree per depth; the
    // deepest free subtree no deeper than the wanted length is the leftmost one, and its all-zero descendant is the codeword.
    private static (int[] Fast, int[] Trie) BuildDecoder(int[] lengths)
    {
        int used = 0, only = -1, longest = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] == 0) continue;
            used++;
            only = i;
            longest = Math.Max(longest, lengths[i]);
        }
        int[] fast = new int[1 << FastBits];
        Array.Fill(fast, NoCode);
        if (used == 0) return (fast, []);
        if (used == 1)
        {
            if (lengths[only] != 1) throw new InvalidDataException("A single-entry Vorbis codebook must use a one-bit code.");
            Array.Fill(fast, only | 1 << 20);
            return (fast, []);
        }

        uint[] freeNode = new uint[33];
        bool[] hasFree = new bool[33];
        hasFree[0] = true;
        List<int> trie = [];
        List<(uint Code, int Length, int Entry)> assigned = new(used);
        for (int entry = 0; entry < lengths.Length; entry++)
        {
            int length = lengths[entry];
            if (length == 0) continue;
            int depth = length;
            while (depth >= 0 && !hasFree[depth]) depth--;
            if (depth < 0) throw new InvalidDataException("A Vorbis codebook's Huffman tree is overspecified.");
            uint code = freeNode[depth] << (length - depth);
            hasFree[depth] = false;
            for (int j = depth + 1; j <= length; j++)
            {
                freeNode[j] = freeNode[depth] << (j - depth) | 1;
                hasFree[j] = true;
            }
            assigned.Add((code, length, entry));
        }
        for (int d = 0; d <= 32; d++)
        {
            if (hasFree[d]) throw new InvalidDataException("A Vorbis codebook's Huffman tree is underspecified.");
        }

        // The slow path: a binary trie over the codewords (children at 2*node, 2*node+1; negative values are ~entry, zero is absent).
        trie.Add(0);
        trie.Add(0);
        foreach ((uint code, int length, int entry) in assigned)
        {
            int node = 0;
            for (int bit = length - 1; bit >= 0; bit--)
            {
                int branch = (int)(code >> bit & 1);
                if (bit == 0)
                {
                    trie[2 * node + branch] = ~entry;
                    break;
                }
                int next = trie[2 * node + branch];
                if (next <= 0)
                {
                    next = trie.Count / 2;
                    trie.Add(0);
                    trie.Add(0);
                    trie[2 * node + branch] = next;
                }
                node = next;
            }
        }
        int[] trieArray = [.. trie];

        // The fast table is indexed by the next FastBits stream bits, the first bit read being the lowest.
        for (int pattern = 0; pattern < fast.Length; pattern++)
        {
            int node = 0;
            for (int i = 0; i < FastBits; i++)
            {
                int bit = pattern >> i & 1;
                int next = trieArray[2 * node + bit];
                if (next == 0)
                {
                    node = -1;
                    break;
                }
                if (next < 0)
                {
                    int entry = ~next;
                    fast[pattern] = entry | (i + 1) << 20;
                    node = -1;
                    break;
                }
                node = next;
            }
            if (node > 0) fast[pattern] = -node;
        }
        return (fast, trieArray);
    }

    /// <summary>Reads one entry number. Returns -1 at the end of the packet or when the bits match no codeword.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeScalar(ref VorbisBitReader reader)
    {
        int slot = fast[reader.Peek(FastBits)];
        if (slot >= 0)
        {
            int length = slot >> 20;
            if (length > reader.Available)
            {
                reader.MarkEndOfPacket();
                return -1;
            }
            reader.Skip(length);
            return slot & 0xFFFFF;
        }
        return DecodeSlow(ref reader, slot);
    }

    private int DecodeSlow(ref VorbisBitReader reader, int slot)
    {
        if (slot == NoCode || reader.Available < FastBits)
        {
            reader.MarkEndOfPacket();
            return -1;
        }
        reader.Skip(FastBits);
        int node = -slot;
        while (true)
        {
            int bit = (int)reader.Read(1);
            if (reader.EndOfPacket) return -1;
            int next = trie[2 * node + bit];
            if (next < 0) return ~next;
            if (next == 0)
            {
                reader.MarkEndOfPacket();
                return -1;
            }
            node = next;
        }
    }

    /// <summary>Reads an entry and returns its vector, or an empty span at the end of the packet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<float> DecodeVector(ref VorbisBitReader reader)
    {
        int entry = DecodeScalar(ref reader);
        if (entry < 0 || vectors is null) return default;
        return vectors.AsSpan(entry * Dimensions, Dimensions);
    }

    public static int Ilog(int value)
    {
        int result = 0;
        while (value > 0)
        {
            result++;
            value >>= 1;
        }
        return result;
    }

    private static float Float32Unpack(uint x)
    {
        double mantissa = x & 0x1FFFFF;
        if ((x & 0x80000000) != 0) mantissa = -mantissa;
        int exponent = (int)((x & 0x7FE00000) >> 21);
        return (float)(mantissa * Math.Pow(2, exponent - 788));
    }

    private static long Lookup1Values(int entries, int dimensions)
    {
        long value = (long)Math.Floor(Math.Pow(entries, 1.0 / dimensions));
        // Correct the floating-point estimate: the answer is the largest value whose dimensions-th power does not exceed the entries.
        while (Power(value + 1, dimensions) <= entries) value++;
        while (value > 1 && Power(value, dimensions) > entries) value--;
        return Math.Max(1, value);
    }

    private static long Power(long baseValue, int exponent)
    {
        long result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result *= baseValue;
            if (result > int.MaxValue) return long.MaxValue;
        }
        return result;
    }
}
