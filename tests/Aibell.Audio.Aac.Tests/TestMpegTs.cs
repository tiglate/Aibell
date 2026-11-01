namespace Aibell.Audio.Aac.Tests;

/// <summary>A minimal MPEG-TS muxer for tests: PAT, PMT and one audio PID carrying PES packets of whole ADTS frames.</summary>
public static class TestMpegTs
{
    public const int ProgramMapPid = 0x100;
    public const int AudioPid = 0x101;

    /// <summary>Muxes <paramref name="frames"/> (each one complete ADTS frame) into TS packets, <paramref name="framesPerPes"/> frames per PES packet.</summary>
    public static byte[] Mux(IReadOnlyList<byte[]> frames, int framesPerPes = 4, int stream = 0x0f, int firstContinuity = 0, bool tables = true)
    {
        using MemoryStream output = new();
        int[] continuity = [0, 0, firstContinuity];
        if (tables)
        {
            output.Write(TablePacket(0, Pat(), ref continuity[0]));
            output.Write(TablePacket(ProgramMapPid, Pmt(stream), ref continuity[1]));
        }
        for (int i = 0; i < frames.Count; i += framesPerPes)
        {
            List<byte> es = [];
            for (int j = i; j < Math.Min(frames.Count, i + framesPerPes); j++) es.AddRange(frames[j]);
            long pts = 90000L * i * 1024 / 44100;
            List<byte> pes = [0, 0, 1, 0xc0, (byte)((es.Count + 8) >> 8), (byte)(es.Count + 8), 0x84, 0x80, 5,
                (byte)(0x21 | (int)(pts >> 29 & 0x0e)), (byte)(pts >> 22), (byte)(pts >> 14 | 1), (byte)(pts >> 7), (byte)(pts << 1 | 1)];
            pes.AddRange(es);
            WritePes(output, pes, ref continuity[2]);
        }
        return output.ToArray();
    }

    private static byte[] Pat() => [0x00, 0xb0, 13, 0, 1, 0xc1, 0, 0, 0, 1, 0xe0 | ProgramMapPid >> 8, ProgramMapPid & 0xff, 0, 0, 0, 0];

    private static byte[] Pmt(int stream) =>
        [0x02, 0xb0, 18, 0, 1, 0xc1, 0, 0, 0xe0 | AudioPid >> 8, AudioPid & 0xff, 0xf0, 0, (byte)stream, 0xe0 | AudioPid >> 8, AudioPid & 0xff, 0xf0, 0, 0, 0, 0, 0];

    private static byte[] TablePacket(int pid, byte[] section, ref int continuity)
    {
        byte[] packet = new byte[188];
        Array.Fill(packet, (byte)0xff);
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | pid >> 8);
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x10 | continuity++ & 15);
        packet[4] = 0;
        section.CopyTo(packet, 5);
        return packet;
    }

    private static void WritePes(Stream output, List<byte> pes, ref int continuity)
    {
        for (int offset = 0; offset < pes.Count; offset += 184)
        {
            int count = Math.Min(184, pes.Count - offset);
            byte[] packet = new byte[188];
            packet[0] = 0x47;
            packet[1] = (byte)((offset == 0 ? 0x40 : 0) | AudioPid >> 8);
            packet[2] = (byte)(AudioPid & 0xff);
            int at = 4;
            if (count < 184)
            {
                int stuffing = 184 - count;
                packet[3] = (byte)(0x30 | continuity++ & 15);
                packet[4] = (byte)(stuffing - 1);
                if (stuffing > 1)
                {
                    packet[5] = 0;
                    for (int i = 6; i < 4 + stuffing; i++) packet[i] = 0xff;
                }
                at = 4 + stuffing;
            }
            else packet[3] = (byte)(0x10 | continuity++ & 15);
            pes.CopyTo(offset, packet, at, count);
            output.Write(packet);
        }
    }
}
