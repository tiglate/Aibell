using System.Buffers.Binary;
using Aibell.Audio;

namespace Aibell.Audio.Ogg.Tests;

/// <summary>Builds Ogg pages from RFC 3533 for tests.</summary>
internal sealed class OggTestWriter(uint serial)
{
    private readonly List<byte> output = [];
    private uint sequence;

    public byte[] ToArray() => [.. output];

    public long Length => output.Count;

    /// <summary>Writes one page holding the given packets (the last may be left open, to continue on the next page).</summary>
    public void Page(IReadOnlyList<byte[]> packets, long granule, OggPageKind flags = OggPageKind.None, bool lastPacketContinues = false, uint? sequenceOverride = null)
    {
        List<byte> lacing = [];
        List<byte> body = [];
        for (int p = 0; p < packets.Count; p++)
        {
            byte[] data = packets[p];
            body.AddRange(data);
            int remaining = data.Length;
            bool open = lastPacketContinues && p == packets.Count - 1;
            while (remaining >= 255)
            {
                lacing.Add(255);
                remaining -= 255;
            }
            if (!open) lacing.Add((byte)remaining);
            else if (remaining > 0) throw new ArgumentException("An open packet must be a multiple of 255 bytes.");
        }
        WritePage(lacing, [.. body], granule, flags, sequenceOverride ?? sequence++);
    }

    /// <summary>Writes the packets, spreading them over pages of at most <paramref name="packetsPerPage"/> packets; long packets continue across pages.</summary>
    public void Stream(IReadOnlyList<byte[]> packets, int packetsPerPage, Func<int, long> granuleAfterPacket, bool endOfStream = true, bool beginOfStream = true)
    {
        for (int i = 0; i < packets.Count; i += packetsPerPage)
        {
            List<byte[]> group = [.. packets.Skip(i).Take(packetsPerPage)];
            OggPageKind flags = OggPageKind.None;
            if (i == 0 && beginOfStream) flags |= OggPageKind.BeginOfStream;
            if (i + packetsPerPage >= packets.Count && endOfStream) flags |= OggPageKind.EndOfStream;
            Page(group, granuleAfterPacket(i + group.Count - 1), flags);
        }
    }

    public void Raw(byte[] bytes) => output.AddRange(bytes);

    private void WritePage(List<byte> lacing, byte[] body, long granule, OggPageKind flags, uint pageSequence)
    {
        byte[] page = new byte[27 + lacing.Count + body.Length];
        "OggS"u8.CopyTo(page);
        page[5] = (byte)flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), pageSequence);
        page[26] = (byte)lacing.Count;
        lacing.CopyTo(page, 27);
        body.CopyTo(page, 27 + lacing.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc.Crc32Ogg(page));
        output.AddRange(page);
    }
}
