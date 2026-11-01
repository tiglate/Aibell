using System.Buffers.Binary;
using System.Text;

namespace Aibell.Infrastructure.Tests.Metadata;

/// <summary>Builds Ogg Vorbis variants in memory from the committed fixture, so no extra binary files are needed.</summary>
internal static class OggFixtures
{
    private static readonly uint[] Crc = BuildTable();

    /// <summary>The fixture with a different encoder (vendor) string in its comment header, for example libVorbis 1.3.5's snowman string.</summary>
    public static byte[] WithVendor(byte[] source, string vendor)
    {
        List<(byte Flags, long Granule, uint Serial, byte[] Lacing, byte[] Body)> pages = ReadPages(source);
        uint serial = pages[0].Serial;
        List<byte[]> packets = [];
        List<byte> current = [];
        int used = 0;
        for (int index = 0; index < pages.Count && packets.Count < 3; index++)
        {
            int offset = 0;
            foreach (byte value in pages[index].Lacing)
            {
                current.AddRange(pages[index].Body.AsSpan(offset, value).ToArray());
                offset += value;
                if (value < 255) { packets.Add([.. current]); current.Clear(); }
            }
            used = index + 1;
        }
        byte[] comment = packets[1];
        int oldLength = BinaryPrimitives.ReadInt32LittleEndian(comment.AsSpan(7));
        byte[] vendorBytes = Encoding.UTF8.GetBytes(vendor);
        byte[] rewritten = [.. comment[..7], .. BitConverter.GetBytes(vendorBytes.Length), .. vendorBytes, .. comment[(11 + oldLength)..]];

        List<byte> output = [.. Page(serial, 0, 2, 0, [(byte)packets[0].Length], packets[0])];
        List<byte> lacing = [];
        foreach (byte[] packet in new[] { rewritten, packets[2] })
        {
            lacing.AddRange(Enumerable.Repeat((byte)255, packet.Length / 255));
            lacing.Add((byte)(packet.Length % 255));
        }
        byte[] headers = [.. rewritten, .. packets[2]];
        int position = 0, sequence = 1;
        bool carried = false;
        for (int start = 0; start < lacing.Count; start += 16)
        {
            byte[] chunk = [.. lacing.Skip(start).Take(16)];
            int size = chunk.Sum(value => value);
            output.AddRange(Page(serial, sequence++, (byte)(carried ? 1 : 0), chunk.Any(value => value < 255) ? 0 : -1, chunk, headers[position..(position + size)]));
            position += size;
            carried = chunk[^1] == 255;
        }
        for (int index = used; index < pages.Count; index++)
            output.AddRange(Page(serial, sequence++, pages[index].Flags, pages[index].Granule, pages[index].Lacing, pages[index].Body));
        return [.. output];
    }

    private static List<(byte Flags, long Granule, uint Serial, byte[] Lacing, byte[] Body)> ReadPages(byte[] data)
    {
        List<(byte, long, uint, byte[], byte[])> pages = [];
        int position = 0;
        while (position < data.Length)
        {
            int segments = data[position + 26];
            byte[] lacing = data[(position + 27)..(position + 27 + segments)];
            int size = lacing.Sum(value => value), start = position + 27 + segments;
            pages.Add((data[position + 5], BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position + 6)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 14)), lacing, data[start..(start + size)]));
            position = start + size;
        }
        return pages;
    }

    private static byte[] Page(uint serial, int sequence, byte flags, long granule, byte[] lacing, byte[] body)
    {
        byte[] page = new byte[27 + lacing.Length + body.Length];
        "OggS"u8.CopyTo(page);
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), (uint)sequence);
        page[26] = (byte)lacing.Length;
        lacing.CopyTo(page, 27);
        body.CopyTo(page, 27 + lacing.Length);
        uint crc = 0;
        foreach (byte value in page) crc = (crc << 8) ^ Crc[(byte)((crc >> 24) ^ value)];
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), crc);
        return page;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int bit = 0; bit < 8; bit++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            table[i] = r;
        }
        return table;
    }
}
