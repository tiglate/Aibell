namespace Aibell.Audio.Aac.Tests;

/// <summary>Hand-built ADTS frames and bit packing shared by the AAC and playback tests.</summary>
internal static class AacTestData
{
    /// <summary>One ADTS frame: MPEG-4, no CRC, the given sampling index / channels, payload filled with <paramref name="fill"/>.</summary>
    internal static byte[] Frame(int payload, int samplingIndex = 4, int channels = 2, byte fill = 0x21, bool crc = false)
    {
        int headerLength = crc ? 9 : 7;
        int length = headerLength + payload;
        byte[] frame = new byte[length];
        frame[0] = 0xff;
        frame[1] = (byte)(0xf1 & (crc ? 0xfe : 0xff));
        frame[2] = (byte)(1 << 6 | samplingIndex << 2 | channels >> 2);
        frame[3] = (byte)((channels & 3) << 6 | length >> 11);
        frame[4] = (byte)(length >> 3 & 0xff);
        frame[5] = (byte)((length & 7) << 5 | 0x1f);
        frame[6] = 0xfc;
        Array.Fill(frame, fill, headerLength, payload);
        return frame;
    }

    /// <summary>An ADTS frame whose raw data block holds only a fill element carrying the given extension type.</summary>
    internal static byte[] FillFrame(int extensionType, int samplingIndex = 7, int channels = 2)
    {
        // FIL (id 6), count 2 bytes (the extension type nibble plus a pad byte, nibble-aligned), then END (id 7).
        byte[] payload = Pack([(6, 3), (2, 4), (extensionType, 4), (0, 12), (7, 3)]);
        byte[] frame = Frame(payload.Length, samplingIndex, channels);
        payload.CopyTo(frame, 7);
        return frame;
    }

    internal static byte[] Pack((int Value, int Bits)[] fields)
    {
        List<byte> bytes = [];
        int current = 0, filled = 0;
        foreach ((int value, int count) in fields)
        {
            for (int bit = count - 1; bit >= 0; bit--)
            {
                current = current << 1 | value >> bit & 1;
                if (++filled == 8)
                {
                    bytes.Add((byte)current);
                    current = filled = 0;
                }
            }
        }
        if (filled > 0) bytes.Add((byte)(current << (8 - filled)));
        return [.. bytes];
    }

    /// <summary>
    /// Regroups single-block ADTS frames into frames of <paramref name="perFrame"/> raw data blocks each (the last frame may hold fewer),
    /// with CRC protection when asked (block positions, header CRC and a CRC after every block, computed with the library's CRC code).
    /// </summary>
    internal static List<byte[]> Regroup(List<byte[]> frames, int perFrame, bool crc)
    {
        AacDecoder decoder = new();
        List<byte[]> result = [];
        for (int first = 0; first < frames.Count; first += perFrame)
        {
            List<byte[]> blocks = [.. frames.Skip(first).Take(perFrame).Select(frame => frame.AsSpan(7).ToArray())];
            byte[] head = frames[first].AsSpan(0, 7).ToArray();
            using MemoryStream body = new();
            List<int> positions = [];
            foreach (byte[] block in blocks)
            {
                positions.Add((int)body.Length);
                body.Write(block);
                if (!crc || blocks.Count == 1) continue;
                decoder.Decode(2, (head[2] >> 2) & 15, (head[2] & 1) << 2 | head[3] >> 6, block);
                ushort blockCrc = AdtsCrc.ComputeBlock(block, decoder.ProtectedRanges);
                body.WriteByte((byte)(blockCrc >> 8));
                body.WriteByte((byte)blockCrc);
            }
            int headerLength = crc ? 7 + 2 * (blocks.Count - 1) + 2 : 7;
            byte[] frame = new byte[headerLength + (int)body.Length];
            head.CopyTo(frame, 0);
            if (crc) frame[1] &= 0xfe;
            frame[3] = (byte)(frame[3] & 0xfc | frame.Length >> 11);
            frame[4] = (byte)(frame.Length >> 3);
            frame[5] = (byte)(frame[5] & 0x1f | (frame.Length & 7) << 5);
            frame[6] = (byte)(frame[6] & 0xfc | blocks.Count - 1);
            if (crc && blocks.Count == 1)
            {
                // A single block has one CRC over the header and the block together.
                body.ToArray().CopyTo(frame, headerLength);
                decoder.Decode(2, (head[2] >> 2) & 15, (head[2] & 1) << 2 | head[3] >> 6, blocks[0]);
                ushort single = AdtsCrc.Compute(frame, headerLength, decoder.ProtectedRanges);
                frame[7] = (byte)(single >> 8);
                frame[8] = (byte)single;
            }
            else if (crc)
            {
                for (int i = 1; i < blocks.Count; i++)
                {
                    frame[7 + 2 * (i - 1)] = (byte)(positions[i] >> 8);
                    frame[8 + 2 * (i - 1)] = (byte)positions[i];
                }
                ushort headerCrc = AdtsCrc.Update(0xffff, frame, 0, (headerLength - 2) * 8);
                frame[headerLength - 2] = (byte)(headerCrc >> 8);
                frame[headerLength - 1] = (byte)headerCrc;
            }
            body.ToArray().CopyTo(frame, headerLength);
            result.Add(frame);
        }
        return result;
    }
}
