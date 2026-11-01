namespace Aibell.Archive.Rar;

internal static class RarCrc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ value >> 1 : value >> 1;
            table[i] = value;
        }
        return table;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ crc >> 8;
        return ~crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);
}
