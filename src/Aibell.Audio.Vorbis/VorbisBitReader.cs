using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Aibell.Audio.Vorbis;

/// <summary>
/// Least-significant-bit-first reader over one packet (Vorbis I section 2). Reading past the end of the packet is not an error in Vorbis: it sets
/// <see cref="EndOfPacket"/> and yields zeros, and the decoding stage decides what that means.
/// </summary>
internal ref struct VorbisBitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int position;
    private ulong cache;
    private int count;
    private bool endOfPacket;

    public VorbisBitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        position = 0;
        cache = 0;
        count = 0;
        endOfPacket = false;
    }

    public readonly bool EndOfPacket => endOfPacket;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Refill()
    {
        if (position + 8 <= data.Length)
        {
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(data[position..]);
            cache |= word << count;
            int advance = (63 - count) >> 3;
            position += advance;
            count += advance << 3;
        }
        else
        {
            while (count <= 56 && position < data.Length)
            {
                cache |= (ulong)data[position++] << count;
                count += 8;
            }
        }
    }

    /// <summary>Reads 0 to 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read(int bits)
    {
        if (bits == 0) return 0;
        if (count < bits)
        {
            Refill();
            if (count < bits)
            {
                endOfPacket = true;
                cache = 0;
                count = 0;
                return 0;
            }
        }
        uint value = (uint)(cache & ((1UL << bits) - 1));
        cache >>= bits;
        count -= bits;
        return value;
    }

    /// <summary>Looks at the next <paramref name="bits"/> bits (at most 32), padded with zeros past the end, without consuming them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Peek(int bits)
    {
        if (count < bits) Refill();
        return (uint)(cache & ((1UL << bits) - 1));
    }

    /// <summary>Bits left in the packet (after a refill).</summary>
    public int Available
    {
        get
        {
            if (count < 32) Refill();
            return count;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int bits)
    {
        cache >>= bits;
        count -= bits;
    }

    public void MarkEndOfPacket()
    {
        endOfPacket = true;
        cache = 0;
        count = 0;
    }
}
