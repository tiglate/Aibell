using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aibell.Audio.Flac;

/// <summary>Raised when a frame needs bytes beyond what is buffered; the decoder loads more and decodes the frame again.</summary>
internal sealed class FlacUnderflowException : Exception
{
    public static readonly FlacUnderflowException Instance = new();

    private FlacUnderflowException() : base("The frame continues beyond the buffered data.")
    {
    }
}

/// <summary>
/// Most-significant-bit-first reader over a byte span, with a 64-bit cache refilled eight bytes at a time. The cache holds <c>count</c> valid bits at
/// the top; the bits below them are either zero or the stream's next bits, so overlapping reloads stay consistent.
/// </summary>
internal ref struct FlacBitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int position;
    private ulong cache;
    private int count;

    public FlacBitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        position = 0;
        cache = 0;
        count = 0;
    }

    /// <summary>Whole bytes consumed so far; meaningful when the reader is byte aligned.</summary>
    public readonly int BytePosition => (position * 8 - count) >> 3;

    public readonly bool IsAligned => (count & 7) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Refill()
    {
        if (position + 8 <= data.Length)
        {
            ulong word = BinaryPrimitives.ReadUInt64BigEndian(data[position..]);
            cache |= word >> count;
            int advance = (63 - count) >> 3;
            position += advance;
            count += advance << 3;
        }
        else RefillSlow();
    }

    private void RefillSlow()
    {
        while (count <= 56 && position < data.Length)
        {
            cache |= (ulong)data[position++] << (56 - count);
            count += 8;
        }
    }

    /// <summary>Reads 0 to 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int bits)
    {
        if (bits == 0) return 0;
        if (count < bits)
        {
            Refill();
            if (count < bits) throw FlacUnderflowException.Instance;
        }
        uint value = (uint)(cache >> (64 - bits));
        cache <<= bits;
        count -= bits;
        return value;
    }

    /// <summary>Reads a two's-complement value of 1 to 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSigned(int bits) => (int)(ReadBits(bits) << (32 - bits)) >> (32 - bits);

    /// <summary>Counts zero bits up to and including the next one bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadUnary()
    {
        int total = 0;
        while (true)
        {
            if (count == 0)
            {
                Refill();
                if (count == 0) throw FlacUnderflowException.Instance;
            }
            int zeros = BitOperations.LeadingZeroCount(cache);
            if (zeros < count)
            {
                cache <<= zeros + 1;
                count -= zeros + 1;
                return total + zeros;
            }
            total += count;
            cache = 0;
            count = 0;
        }
    }

    public void AlignToByte()
    {
        int drop = count & 7;
        cache <<= drop;
        count -= drop;
    }
}
