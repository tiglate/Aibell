namespace Aibell.Audio;

/// <summary>Table-driven CRCs used by the audio containers (polynomials from RFC 9639 for FLAC and RFC 3533 for Ogg).</summary>
public static class Crc
{
    private static readonly byte[] Table8 = BuildTable8(0x07);
    private static readonly ushort[] Table16 = BuildTable16(0x8005);
    private static readonly uint[] Table32 = BuildTable32(0x04C11DB7);

    /// <summary>CRC-8, polynomial x^8+x^2+x+1, initial value 0, not reflected (FLAC frame header).</summary>
    public static byte Crc8(ReadOnlySpan<byte> data, byte crc = 0)
    {
        foreach (byte b in data) crc = Table8[crc ^ b];
        return crc;
    }

    /// <summary>CRC-16, polynomial x^16+x^15+x^2+1, initial value 0, not reflected (FLAC frame).</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data, ushort crc = 0)
    {
        foreach (byte b in data) crc = (ushort)((crc << 8) ^ Table16[(crc >> 8) ^ b]);
        return crc;
    }

    /// <summary>CRC-32, polynomial 0x04C11DB7, initial value 0, not reflected, no final xor (Ogg page).</summary>
    public static uint Crc32Ogg(ReadOnlySpan<byte> data, uint crc = 0)
    {
        foreach (byte b in data) crc = (crc << 8) ^ Table32[(crc >> 24) ^ b];
        return crc;
    }

    private static byte[] BuildTable8(byte polynomial)
    {
        byte[] table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 0x80) != 0 ? (value << 1) ^ polynomial : value << 1;
            table[i] = (byte)value;
        }
        return table;
    }

    private static ushort[] BuildTable16(ushort polynomial)
    {
        ushort[] table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int value = i << 8;
            for (int bit = 0; bit < 8; bit++) value = (value & 0x8000) != 0 ? (value << 1) ^ polynomial : value << 1;
            table[i] = (ushort)value;
        }
        return table;
    }

    private static uint[] BuildTable32(uint polynomial)
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i << 24;
            for (int bit = 0; bit < 8; bit++) value = (value & 0x80000000) != 0 ? (value << 1) ^ polynomial : value << 1;
            table[i] = value;
        }
        return table;
    }
}
