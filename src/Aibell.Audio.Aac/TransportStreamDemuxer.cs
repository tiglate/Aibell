namespace Aibell.Audio.Aac;

/// <summary>The audio format a transport stream's program map declares for the chosen audio stream.</summary>
public enum TransportAudioKind
{
    /// <summary>No program map seen yet, or it lists no audio.</summary>
    None,
    AacAdts,
    AacLatm,
    MpegAudio,
    Ac3,
    Other
}

/// <summary>
/// Extracts the elementary stream of the first audio program of an MPEG transport stream (ISO/IEC 13818-1), as HLS packed segments carry it.
/// Input may arrive in any chunking; bytes that do not line up with 188-byte packets are skipped (resynchronizing on 0x47 confirmed by the
/// next packet); lost packets are noticed through the continuity counters. Memory is bounded and nothing is allocated per packet.
/// Program tables must fit in one packet, which is true for any real audio stream.
/// </summary>
public sealed class TransportStreamDemuxer
{
    private const int PacketSize = 188;
    private const int PatPid = 0;
    private readonly byte[] buffer = new byte[PacketSize * 4];
    private readonly Action<ReadOnlySpan<byte>> sink;
    private readonly Action? onDiscontinuity;
    private readonly byte[] pesHeader = new byte[9 + 255];
    private int bufferLength;
    private int programMapPid = -1;
    private int audioPid = -1;
    private int lastContinuity = -1;
    private int pesHeaderLength;
    private int pesHeaderNeeded;
    private bool inPes;
    private bool synced;

    /// <param name="sink">Receives elementary stream bytes in order. The span is only valid during the call.</param>
    /// <param name="onDiscontinuity">Called before data that follows lost or skipped input, so a frame parser can drop its partial frame.</param>
    public TransportStreamDemuxer(Action<ReadOnlySpan<byte>> sink, Action? onDiscontinuity = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        this.sink = sink;
        this.onDiscontinuity = onDiscontinuity;
    }

    public TransportAudioKind Kind { get; private set; }

    /// <summary>The PMT stream_type of the chosen audio stream, or -1.</summary>
    public int StreamType { get; private set; } = -1;

    public long SkippedBytes { get; private set; }

    public long Packets { get; private set; }

    public void Push(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int copy = Math.Min(buffer.Length - bufferLength, data.Length);
            data[..copy].CopyTo(buffer.AsSpan(bufferLength));
            bufferLength += copy;
            data = data[copy..];
            Process(final: false);
        }
    }

    /// <summary>The input is complete (the end of a segment): the last packet no longer needs a following sync byte to be accepted.</summary>
    public void Complete() => Process(final: true);

    /// <summary>Starts a new, unrelated stretch of input (the next HLS segment): continuity counters and any partial PES packet are forgotten.</summary>
    public void StartSegment()
    {
        Process(final: true);
        bufferLength = 0;
        lastContinuity = -1;
        inPes = false;
        synced = false;
        pesHeaderLength = 0;
        pesHeaderNeeded = 0;
    }

    private void Process(bool final)
    {
        int position = 0;
        while (bufferLength - position >= PacketSize + (final ? 0 : 1))
        {
            // In sync, a packet needs no confirmation; while searching, the next packet must start with 0x47 too (unless the input ends).
            bool aligned = buffer[position] == 0x47 && (synced || bufferLength - position < PacketSize + 1 || buffer[position + PacketSize] == 0x47);
            if (!aligned)
            {
                position++;
                SkippedBytes++;
                if (synced)
                {
                    synced = false;
                    Break();
                }
                continue;
            }
            synced = true;
            HandlePacket(buffer.AsSpan(position, PacketSize));
            position += PacketSize;
        }
        if (final)
        {
            // Whatever is left is shorter than a packet.
            SkippedBytes += bufferLength - position;
            position = bufferLength;
        }
        if (position > 0)
        {
            Buffer.BlockCopy(buffer, position, buffer, 0, bufferLength - position);
            bufferLength -= position;
        }
    }

    private void Break()
    {
        inPes = false;
        pesHeaderLength = 0;
        pesHeaderNeeded = 0;
        lastContinuity = -1;
        onDiscontinuity?.Invoke();
    }

    private void HandlePacket(ReadOnlySpan<byte> packet)
    {
        Packets++;
        int pid = (packet[1] & 0x1f) << 8 | packet[2];
        bool start = (packet[1] & 0x40) != 0;
        int control = packet[3] >> 4 & 3;
        if ((packet[1] & 0x80) != 0)
        {
            if (pid == audioPid) Break();
            return;
        }
        if ((control & 1) == 0) return; // no payload
        int offset = 4;
        if ((control & 2) != 0)
        {
            offset = 5 + packet[4];
            if (offset > PacketSize) return;
        }
        ReadOnlySpan<byte> payload = packet[offset..];

        if (pid == PatPid)
        {
            if (start) ReadProgramAssociation(payload);
        }
        else if (pid == programMapPid)
        {
            if (start) ReadProgramMap(payload);
        }
        else if (pid == audioPid)
        {
            int continuity = packet[3] & 15;
            if (lastContinuity >= 0)
            {
                if (continuity == lastContinuity) return; // a duplicate packet, allowed once by the standard
                if (continuity != (lastContinuity + 1 & 15)) Break();
            }
            lastContinuity = continuity;
            ReadPes(payload, start);
        }
    }

    private static ReadOnlySpan<byte> Section(ReadOnlySpan<byte> payload, byte tableId)
    {
        if (payload.Length < 1) return default;
        int pointer = payload[0];
        if (1 + pointer + 3 > payload.Length) return default;
        ReadOnlySpan<byte> section = payload[(1 + pointer)..];
        if (section[0] != tableId) return default;
        int length = (section[1] & 0x0f) << 8 | section[2];
        return 3 + length <= section.Length ? section[..(3 + length)] : default;
    }

    private void ReadProgramAssociation(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> section = Section(payload, 0x00);
        // Header (8 bytes after the table id and length), then 4-byte entries, then a 4-byte CRC.
        for (int at = 8; at + 4 <= section.Length - 4; at += 4)
        {
            int program = section[at] << 8 | section[at + 1];
            if (program == 0) continue; // the network PID
            int pid = (section[at + 2] & 0x1f) << 8 | section[at + 3];
            if (pid != programMapPid)
            {
                programMapPid = pid;
                audioPid = -1;
                Kind = TransportAudioKind.None;
                StreamType = -1;
            }
            return;
        }
    }

    private void ReadProgramMap(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> section = Section(payload, 0x02);
        if (section.Length < 16) return;
        int at = 12 + ((section[10] & 0x0f) << 8 | section[11]);
        int end = section.Length - 4;
        int chosenPid = -1, chosenType = -1;
        TransportAudioKind chosen = TransportAudioKind.None;
        while (at + 5 <= end)
        {
            int type = section[at];
            int pid = (section[at + 1] & 0x1f) << 8 | section[at + 2];
            TransportAudioKind kind = type switch
            {
                0x0f => TransportAudioKind.AacAdts,
                0x11 => TransportAudioKind.AacLatm,
                0x03 or 0x04 => TransportAudioKind.MpegAudio,
                0x81 or 0x06 or 0x87 => TransportAudioKind.Ac3,
                _ => TransportAudioKind.None
            };
            // Prefer a stream this library can read; remember the first other audio stream so callers can name it.
            if (kind != TransportAudioKind.None && (chosen == TransportAudioKind.None || IsReadable(kind) && !IsReadable(chosen)))
            {
                chosen = kind;
                chosenPid = pid;
                chosenType = type;
            }
            at += 5 + ((section[at + 3] & 0x0f) << 8 | section[at + 4]);
        }
        if (chosenPid != audioPid)
        {
            audioPid = chosenPid;
            lastContinuity = -1;
            inPes = false;
        }
        Kind = chosen;
        StreamType = chosenType;
    }

    private static bool IsReadable(TransportAudioKind kind) => kind is TransportAudioKind.AacAdts or TransportAudioKind.MpegAudio;

    private void ReadPes(ReadOnlySpan<byte> payload, bool start)
    {
        if (start)
        {
            inPes = false;
            pesHeaderLength = 0;
            pesHeaderNeeded = 9;
        }
        else if (!inPes && pesHeaderNeeded == 0) return; // the middle of a PES packet whose start was lost
        if (!inPes)
        {
            // The PES header (9 bytes plus optional fields) may be split over packets, though it almost never is.
            int take = Math.Min(payload.Length, pesHeader.Length - pesHeaderLength);
            payload[..take].CopyTo(pesHeader.AsSpan(pesHeaderLength));
            pesHeaderLength += take;
            if (pesHeaderLength < 9) return;
            if (pesHeader[0] != 0 || pesHeader[1] != 0 || pesHeader[2] != 1)
            {
                pesHeaderNeeded = 0;
                pesHeaderLength = 0;
                return;
            }
            int total = 9 + pesHeader[8];
            if (pesHeaderLength < total) return;
            inPes = true;
            pesHeaderNeeded = 0;
            int consumed = total - (pesHeaderLength - take);
            payload = payload[Math.Min(consumed, payload.Length)..];
            pesHeaderLength = 0;
        }
        if (!payload.IsEmpty) sink(payload);
    }
}
