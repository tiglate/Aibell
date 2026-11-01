namespace Aibell.Audio.Mp3;

/// <summary>Reads bits (most significant first) from a byte buffer that is padded with at least four zero bytes.</summary>
internal struct Mp3BitReader(byte[] data)
{
    private readonly byte[] data = data;

    /// <summary>The position in bits from the start of the buffer.</summary>
    public int Position;

    /// <summary>The next <paramref name="count"/> (at most 25) bits without consuming them.</summary>
    public readonly uint Peek(int count)
    {
        int index = Position >> 3;
        uint word = (uint)(data[index] << 24 | data[index + 1] << 16 | data[index + 2] << 8 | data[index + 3]);
        return word << (Position & 7) >> (32 - count);
    }

    public uint Read(int count)
    {
        uint value = count == 0 ? 0 : Peek(count);
        Position += count;
        return value;
    }
}

/// <summary>One of the Huffman code tables of Layer III, decoded through a lookup table for the short codes and a search for the long ones.</summary>
internal sealed class Mp3HuffmanTable
{
    private const int FastBits = 9;

    private readonly int[] fast = new int[1 << FastBits];
    private readonly (uint Code, int Length, int Symbol)[] slow;
    private readonly int maxLength;

    public Mp3HuffmanTable(int dimension, int linBits, uint[] codes, byte[] lengths)
    {
        Dimension = dimension;
        LinBits = linBits;
        List<(uint, int, int)> longCodes = [];
        for (int symbol = 0; symbol < codes.Length; symbol++)
        {
            int length = lengths[symbol];
            maxLength = Math.Max(maxLength, length);
            if (length == 0) continue;
            if (length <= FastBits)
            {
                int first = (int)(codes[symbol] << (FastBits - length));
                for (int i = 0; i < 1 << (FastBits - length); i++) fast[first + i] = length << 16 | symbol + 1;
            }
            else longCodes.Add((codes[symbol], length, symbol));
        }
        slow = [.. longCodes];
    }

    public int Dimension { get; }

    public int LinBits { get; }

    /// <summary>Decodes one symbol and returns its index (x * dimension + y), or -1 for a bit pattern that is no code.</summary>
    public int Decode(ref Mp3BitReader reader)
    {
        if (maxLength == 0) return 0;
        uint bits = reader.Peek(FastBits);
        int entry = fast[bits];
        if (entry != 0)
        {
            reader.Position += entry >> 16;
            return (entry & 0xFFFF) - 1;
        }
        foreach ((uint code, int length, int symbol) in slow)
        {
            if (reader.Peek(length) == code)
            {
                reader.Position += length;
                return symbol;
            }
        }
        return -1;
    }
}
