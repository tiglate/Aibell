using System.Buffers.Binary;
using System.Text;

namespace Aibell.Playback.Miniaudio.Tests;

/// <summary>Builds Ogg Vorbis test files in memory from the committed fixture, so no extra binary fixture is needed.</summary>
internal static class OggTestFiles
{
    private static readonly uint[] CrcTable = BuildTable();

    /// <summary>
    /// Rewrites <paramref name="source"/> with a comment header of about <paramref name="commentBytes"/> bytes (a long AcoustID-style
    /// fingerprint) split over Ogg pages of at most 16 lacing values, the way several taggers write real files. A decoder that reads the
    /// stream in 4 KB chunks then sees the comment packet continue on a page that has not arrived yet. The audio pages are kept.
    /// </summary>
    public static byte[] WithLongCommentHeader(byte[] source, int commentBytes = 14_000)
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
        if (packets.Count != 3 || current.Count != 0) throw new InvalidDataException("Expected identification, comment and setup packets.");

        // About forty tags, like a MusicBrainz-tagged file: several of them are still unread when the first 4 KB chunks run out.
        byte[] vendor = Encoding.ASCII.GetBytes("Aibell test");
        List<byte[]> tags = [Encoding.ASCII.GetBytes("TITLE=Long Comment Header")];
        for (int index = 0; tags.Sum(tag => tag.Length + 4) < commentBytes; index++)
            tags.Add(Encoding.ASCII.GetBytes($"TAG{index:D2}=" + new string((char)('A' + index % 26), 340)));
        List<byte> comment = [3, .. "vorbis"u8.ToArray(), .. BitConverter.GetBytes(vendor.Length), .. vendor, .. BitConverter.GetBytes(tags.Count)];
        foreach (byte[] tag in tags) comment.AddRange([.. BitConverter.GetBytes(tag.Length), .. tag]);
        comment.Add(1);

        List<byte> output = [];
        int sequence = 0;
        output.AddRange(Page(serial, sequence++, 2, 0, [(byte)packets[0].Length], packets[0]));
        List<byte> lacing = [];
        foreach (byte[] packet in new[] { [.. comment], packets[2] })
        {
            lacing.AddRange(Enumerable.Repeat((byte)255, packet.Length / 255));
            lacing.Add((byte)(packet.Length % 255));
        }
        byte[] headerBytes = [.. comment, .. packets[2]];
        int position = 0;
        bool carried = false;
        for (int start = 0; start < lacing.Count; start += 16)
        {
            byte[] chunk = [.. lacing.Skip(start).Take(16)];
            int size = chunk.Sum(value => value);
            bool completes = chunk.Any(value => value < 255);
            output.AddRange(Page(serial, sequence++, (byte)(carried ? 1 : 0), completes ? 0 : -1, chunk, headerBytes[position..(position + size)]));
            position += size;
            carried = chunk[^1] == 255;
        }
        for (int index = used; index < pages.Count; index++)
            output.AddRange(Page(serial, sequence++, pages[index].Flags, pages[index].Granule, pages[index].Lacing, pages[index].Body));
        return [.. output];
    }

    /// <summary>Length of the header pages (everything before the first audio page).</summary>
    public static int HeaderLength(byte[] file)
    {
        List<(byte Flags, long Granule, uint Serial, byte[] Lacing, byte[] Body)> pages = ReadPages(file);
        int position = 0, packets = 0;
        foreach ((byte _, long _, uint _, byte[] lacing, byte[] body) in pages)
        {
            position += 27 + lacing.Length + body.Length;
            packets += lacing.Count(value => value < 255);
            if (packets >= 3) return position;
        }
        throw new InvalidDataException("No audio page found.");
    }

    private static List<(byte Flags, long Granule, uint Serial, byte[] Lacing, byte[] Body)> ReadPages(byte[] data)
    {
        List<(byte, long, uint, byte[], byte[])> pages = [];
        int position = 0;
        while (position < data.Length)
        {
            if (!data.AsSpan(position, 4).SequenceEqual("OggS"u8)) throw new InvalidDataException("Not an Ogg stream.");
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
        foreach (byte value in page) crc = (crc << 8) ^ CrcTable[(byte)((crc >> 24) ^ value)];
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
