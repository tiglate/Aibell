namespace Aibell.Archive.Rar;

/// <summary>Reads bits most significant first from a byte range. Reads past the end return zeros and set <see cref="Overrun"/>.</summary>
internal sealed class RarBitReader
{
    private readonly byte[] data;
    private readonly int end;

    public RarBitReader(byte[] data, int start, int length)
    {
        this.data = data;
        end = start + length;
        BitPosition = (long)start * 8;
        Start = start;
    }

    public int Start { get; }

    /// <summary>Position in bits from the start of the underlying array.</summary>
    public long BitPosition { get; set; }

    public bool Overrun => BitPosition > (long)end * 8;

    public int BitsLeft => (int)Math.Max(0, (long)end * 8 - BitPosition);

    /// <summary>The next <paramref name="count"/> bits (up to 32) without consuming them.</summary>
    public uint Peek(int count)
    {
        int index = (int)(BitPosition >> 3);
        int shift = (int)(BitPosition & 7);
        ulong value = 0;
        for (int i = 0; i < 5; i++) value = value << 8 | (index + i < end && index + i >= 0 ? data[index + i] : 0u);
        return (uint)(value >> (40 - shift - count) & ((1UL << count) - 1));
    }

    public void Skip(int count) => BitPosition += count;

    public uint Read(int count)
    {
        if (count == 0) return 0;
        uint value = Peek(count);
        BitPosition += count;
        return value;
    }

    public void AlignToByte() => BitPosition = BitPosition + 7 & ~7L;

    public int ReadByte() => (int)Read(8);
}
