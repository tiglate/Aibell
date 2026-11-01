namespace Aibell.Audio.Aac;

/// <summary>MSB-first bit reader over a byte span. Reading past the end throws <see cref="AacException"/>; it never reads outside the span.</summary>
internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int position;

    public BitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        position = 0;
    }

    public readonly int BitPosition => position;

    public readonly int BitsLeft => data.Length * 8 - position;

    public uint ReadBits(int count)
    {
        if (count == 0) return 0;
        uint value = Peek(count);
        Skip(count);
        return value;
    }

    public int ReadBit() => (int)ReadBits(1);

    public bool ReadFlag() => ReadBits(1) != 0;

    /// <summary>Returns the next <paramref name="count"/> (1..25) bits without consuming them; bits past the end read as zero.</summary>
    public readonly uint Peek(int count)
    {
        int byteIndex = position >> 3;
        uint window = 0;
        for (int i = 0; i < 4; i++)
        {
            int index = byteIndex + i;
            window = window << 8 | (index < data.Length ? data[index] : 0u);
        }
        // 32 bits loaded, of which (position & 7) are already consumed; up to 25 bits remain guaranteed.
        if (count > 25) return PeekWide(count);
        return window << (position & 7) >> (32 - count);
    }

    private readonly uint PeekWide(int count)
    {
        BitReader copy = this;
        uint high = copy.ReadBits(count - 16);
        return high << 16 | copy.ReadBits(16);
    }

    public void Skip(int count)
    {
        if (count > BitsLeft) throw new AacException("The AAC data ended inside a frame.");
        position += count;
    }

    public void ByteAlign() => position = position + 7 & ~7;
}
