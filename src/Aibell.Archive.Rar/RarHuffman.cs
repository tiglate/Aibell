namespace Aibell.Archive.Rar;

/// <summary>A canonical Huffman code (codes of one length are assigned in symbol order, shorter codes first) for up to 15-bit codes.</summary>
internal sealed class RarHuffman
{
    private readonly int[] firstCode = new int[17];
    private readonly int[] count = new int[17];
    private readonly int[] offset = new int[17];
    private int[] symbols = [];
    private int total;

    public bool IsEmpty => total == 0;

    /// <summary>Builds the code from <paramref name="lengths"/> (0 = unused symbol). Throws when the lengths over-subscribe the code space.</summary>
    public void Build(ReadOnlySpan<byte> lengths)
    {
        Array.Clear(count);
        foreach (byte length in lengths) count[length & 15]++;
        count[0] = 0;
        int code = 0, index = 0;
        total = 0;
        for (int len = 1; len <= 15; len++)
        {
            firstCode[len] = code;
            offset[len] = index;
            index += count[len];
            code = code + count[len] << 1;
            if (firstCode[len] + count[len] > 1 << len) throw new InvalidDataException("The RAR Huffman table is over-subscribed.");
        }
        total = index;
        if (symbols.Length < total) symbols = new int[Math.Max(total, 16)];
        var next = new int[17];
        Array.Copy(offset, next, 16);
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int len = lengths[symbol] & 15;
            if (len != 0) symbols[next[len]++] = symbol;
        }
    }

    public int Decode(RarBitReader reader)
    {
        uint bits = reader.Peek(15);
        for (int len = 1; len <= 15; len++)
        {
            int diff = (int)(bits >> 15 - len) - firstCode[len];
            if (diff >= 0 && diff < count[len])
            {
                reader.Skip(len);
                return symbols[offset[len] + diff];
            }
        }
        throw new InvalidDataException("Invalid prefix code in the RAR bit stream.");
    }
}
