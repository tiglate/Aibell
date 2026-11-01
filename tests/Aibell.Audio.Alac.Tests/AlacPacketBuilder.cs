namespace Aibell.Audio.Alac.Tests;

/// <summary>Writes ALAC packets by hand (uncompressed "escape" elements, skipped elements, end marker) for the decoder tests.</summary>
internal sealed class AlacPacketBuilder
{
    private readonly List<byte> bytes = [];
    private int bitCount;

    public AlacPacketBuilder Bits(uint value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            if (bitCount % 8 == 0) bytes.Add(0);
            if (((value >> i) & 1) != 0) bytes[^1] |= (byte)(0x80 >> (bitCount % 8));
            bitCount++;
        }
        return this;
    }

    public AlacPacketBuilder Raw(int sample, int bits)
    {
        if (bits <= 16) return Bits((uint)sample & (uint.MaxValue >> (32 - bits)), bits);
        return Bits((uint)sample >> (bits - 16) & 0xFFFF, 16).Bits((uint)sample & (uint.MaxValue >> (48 - bits)), bits - 16);
    }

    public AlacPacketBuilder Header(int tag, int instance, int flags = 1, uint unused = 0) => Bits((uint)tag, 3).Bits((uint)instance, 4).Bits(unused, 12).Bits((uint)flags, 4);

    /// <summary>An uncompressed mono (tag 0) or LFE (tag 3) element.</summary>
    public AlacPacketBuilder EscapeSingle(int[] samples, int bits, int tag = 0, int? partial = null)
    {
        Header(tag, 0, 1 | (partial is null ? 0 : 8));
        if (partial is { } count) Bits((uint)count >> 16, 16).Bits((uint)count & 0xFFFF, 16);
        foreach (int sample in samples) Raw(sample, bits);
        return this;
    }

    /// <summary>An uncompressed channel pair element (left and right interleaved).</summary>
    public AlacPacketBuilder EscapePair(int[] left, int[] right, int bits, int? partial = null)
    {
        Header(1, 0, 1 | (partial is null ? 0 : 8));
        if (partial is { } count) Bits((uint)count >> 16, 16).Bits((uint)count & 0xFFFF, 16);
        for (int i = 0; i < left.Length; i++)
        {
            Raw(left[i], bits);
            Raw(right[i], bits);
        }
        return this;
    }

    public AlacPacketBuilder End() => Bits(7, 3);

    public byte[] ToArray() => [.. bytes];

    public static byte[] Cookie(int frameLength, int bits, int channels, int rate = 44100, int version = 0, bool atomHeaders = false)
    {
        List<byte> cookie = [];
        if (atomHeaders)
        {
            cookie.AddRange([0, 0, 0, 12, (byte)'f', (byte)'r', (byte)'m', (byte)'a', (byte)'a', (byte)'l', (byte)'a', (byte)'c']);
            cookie.AddRange([0, 0, 0, 36, (byte)'a', (byte)'l', (byte)'a', (byte)'c', 0, 0, 0, 0]);
        }
        cookie.AddRange([(byte)(frameLength >> 24), (byte)(frameLength >> 16), (byte)(frameLength >> 8), (byte)frameLength]);
        cookie.AddRange([(byte)version, (byte)bits, 40, 10, 14, (byte)channels, 0, 255]);
        cookie.AddRange([0, 0, 0x10, 0, 0, 0, 0xAC, 0x44]);
        cookie.AddRange([(byte)(rate >> 24), (byte)(rate >> 16), (byte)(rate >> 8), (byte)rate]);
        return [.. cookie];
    }
}
