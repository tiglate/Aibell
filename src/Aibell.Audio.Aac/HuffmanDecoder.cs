namespace Aibell.Audio.Aac;

/// <summary>
/// Table-driven prefix-code decoder built from (length, codeword) lists. A first-level table resolves codewords of up to
/// <see cref="RootBits"/> bits in one lookup; longer codewords continue in small second-level tables. Codeword lengths are validated when the
/// decoder is built, so a damaged table cannot send decoding into a loop.
/// </summary>
internal sealed class HuffmanDecoder
{
    private const int RootBits = 9;
    private readonly int[] root = new int[1 << RootBits];
    private readonly List<int[]> subtables = [];
    private readonly int subBits;

    // Entry encoding: bit 31 set = pointer to subtable (index in the low bits); otherwise (length << 16) | symbol; 0 = invalid.
    public HuffmanDecoder((byte Length, uint Code)[] codes)
    {
        int maxLength = codes.Max(code => code.Length);
        subBits = Math.Max(0, maxLength - RootBits);
        for (int symbol = 0; symbol < codes.Length; symbol++)
        {
            (byte length, uint code) = codes[symbol];
            if (length is 0 or > 24) throw new InvalidOperationException("Bad Huffman codeword length.");
            if (length <= RootBits)
            {
                int first = (int)(code << (RootBits - length));
                for (int i = 0; i < 1 << (RootBits - length); i++) root[first + i] = length << 16 | symbol;
                continue;
            }
            int prefix = (int)(code >> (length - RootBits));
            if (root[prefix] == 0)
            {
                root[prefix] = int.MinValue | subtables.Count;
                subtables.Add(new int[1 << subBits]);
            }
            int[] table = subtables[root[prefix] & 0x7fffffff];
            int remaining = length - RootBits;
            int suffix = (int)(code & ((1u << remaining) - 1));
            int start = suffix << (subBits - remaining);
            for (int i = 0; i < 1 << (subBits - remaining); i++) table[start + i] = length << 16 | symbol;
        }
    }

    public int Decode(ref BitReader reader)
    {
        uint window = reader.Peek(RootBits + subBits);
        int entry = root[(int)(window >> subBits)];
        if (entry < 0) entry = subtables[entry & 0x7fffffff][(int)(window & ((1u << subBits) - 1))];
        if (entry == 0) throw new AacException("Invalid Huffman codeword.");
        reader.Skip(entry >> 16);
        return entry & 0xffff;
    }
}
