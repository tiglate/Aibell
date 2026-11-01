using Aibell.Audio.Ogg;

namespace Aibell.Audio.Opus;

/// <summary>
/// Ogg Opus decoder (RFC 7845 on top of the Opus codec of RFC 6716): mono and stereo streams (channel mapping family 0) and the multistream layouts of families 1 (surround, up to 8 channels, output in WAVE order) and 255 (discrete channels), pre-skip and output gain from the
/// identification header, end-of-stream trimming by granule position, concealment of lost pages, chained streams with the same channel count, and seeking by bisecting the pages with an
/// 80 ms pre-roll. Output is 48 kHz. Decoding allocates nothing once the stream is open.
/// </summary>
public sealed class OggOpusDecoder : IAudioDecoder
{
    private const int PreRollFrames = 3840;
    private const int ConcealChunk = 960;
    private const long ConcealLimit = 10 * OpusDecoder.SampleRate;

    private readonly Stream stream;
    private readonly OggPacketReader packets;
    private OpusDecoder[] decoders = [];
    private float[][] streamPcm = [];
    private bool direct;
    private int[] waveOrder = [];
    private readonly float[] pcm;
    private readonly long audioStart;
    private readonly long streamEnd;
    private byte[] packetBuffer = new byte[4096];
    private OpusHead head;
    private float gain;
    private int pcmStart;
    private int pcmEnd;
    private long decodedEnd;
    private long skipRemaining;
    private bool anchored = true;
    private long seekTarget;
    private bool ended;
    private bool disposed;
    private bool havePending;
    private int pendingLength;
    private long pendingGranule;
    private bool pendingEnd;
    private long concealRemaining;

    private OggOpusDecoder(Stream stream, OggPacketReader packets, OpusHead head, long audioStart, long totalFrames)
    {
        this.stream = stream;
        this.packets = packets;
        this.head = head;
        this.audioStart = audioStart;
        TotalFrames = totalFrames;
        streamEnd = packets.CanSeek ? packets.Pages.Length : -1;
        Configure(head);
        pcm = new float[OpusDecoder.MaxPacketFrames * head.Channels];
        gain = head.Gain;
        skipRemaining = head.PreSkip;
    }

    public int SampleRate => OpusDecoder.SampleRate;

    public int Channels => head.Channels;

    public long TotalFrames { get; }

    /// <summary>True when the bytes start an Ogg stream carrying Opus.</summary>
    public static bool Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 35 || !bytes[..4].SequenceEqual("OggS"u8)) return false;
        int segments = bytes[26];
        return bytes.Length >= 27 + segments + 8 && OpusHead.IsHead(bytes[(27 + segments)..]);
    }

    public static OggOpusDecoder Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        OggPacketReader packets = new(stream, OpusHead.IsHead);
        if (!packets.ReadPacket(out OggPacket first) || !OpusHead.IsHead(first.Data)) throw new InvalidDataException("This is not an Ogg Opus stream.");
        OpusHead head = OpusHead.Parse(first.Data);
        if (!packets.ReadPacket(out OggPacket tags) || tags.Data.Length < 8 || !tags.Data[..8].SequenceEqual("OpusTags"u8))
            throw new InvalidDataException("The Opus comment header is missing.");
        long audioStart = packets.CanSeek ? packets.Pages.Position : -1;
        long total = -1;
        if (packets.CanSeek)
        {
            long length = packets.Pages.Length;
            if (OggSeeker.FindLastPage(packets.Pages, packets.SerialNumber, audioStart, length, out OggPageHeader last, out bool foreignAfter) && !foreignAfter)
                total = Math.Max(0, last.Granule - head.PreSkip);
            packets.SeekToPage(audioStart);
        }
        return new OggOpusDecoder(stream, packets, head, audioStart, total);
    }

    public int Read(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int channels = head.Channels;
        int produced = 0;
        while (produced < frames)
        {
            if (pcmStart >= pcmEnd && !DecodeNext()) break;
            int count = Math.Min(pcmEnd - pcmStart, frames - produced);
            Span<float> target = destination.Slice(produced * channels, count * channels);
            pcm.AsSpan(pcmStart * channels, count * channels).CopyTo(target);
            if (gain != 1f)
            {
                for (int i = 0; i < target.Length; i++) target[i] *= gain;
            }
            pcmStart += count;
            produced += count;
        }
        return produced;
    }

    public bool Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!packets.CanSeek || frame < 0 || audioStart < 0) return false;
        pcmStart = pcmEnd = 0;
        ended = false;
        if (TotalFrames >= 0 && frame >= TotalFrames)
        {
            ended = true;
            return true;
        }
        foreach (OpusDecoder d in decoders) d.Reset();
        long target = frame + head.PreSkip;
        if (OggSeeker.FindLastPageAtOrBefore(packets.Pages, packets.SerialNumber, audioStart, streamEnd, target - PreRollFrames, out long offset, out long granule) && granule > 0)
        {
            packets.SeekToPage(offset);
            anchored = false;
            seekTarget = target;
            skipRemaining = 0;
            decodedEnd = 0;
            return true;
        }
        packets.SeekToPage(audioStart);
        anchored = true;
        decodedEnd = 0;
        skipRemaining = target;
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
    }

    /// <summary>Decodes packets until some audio is ready. False at the end of the stream.</summary>
    private bool DecodeNext()
    {
        if (ended) return false;
        while (true)
        {
            if (concealRemaining > 0)
            {
                if (Conceal()) return true;
                continue;
            }
            int length;
            long granule;
            bool endOfStream;
            if (havePending)
            {
                havePending = false;
                length = pendingLength;
                granule = pendingGranule;
                endOfStream = pendingEnd;
            }
            else
            {
                if (!packets.ReadPacket(out OggPacket packet)) break;
                if (packet.BeginOfStream)
                {
                    // A chained stream: only continued when the channel count is the same, since the output format cannot change mid-stream.
                    try
                    {
                        if (!OpusHead.IsHead(packet.Data)) continue;
                        OpusHead next = OpusHead.Parse(packet.Data);
                        if (next.Channels != head.Channels) break;
                        if (!packets.ReadPacket(out _)) break;
                        head = next;
                        gain = next.Gain;
                        Configure(next);
                        decodedEnd = 0;
                        skipRemaining = next.PreSkip;
                        anchored = true;
                        continue;
                    }
                    catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
                    {
                        break;
                    }
                }
                if (packet.Data.Length == 0) continue;
                if (packet.Data.Length > packetBuffer.Length) Array.Resize(ref packetBuffer, packet.Data.Length);
                packet.Data.CopyTo(packetBuffer);
                length = packet.Data.Length;
                granule = packet.PageGranule;
                endOfStream = packet.EndOfStream;
                if (packet.LostBefore && anchored && decodedEnd > 0)
                {
                    // Pages went missing: the granule position of this page says how much audio is gone, and it is concealed before this packet plays.
                    long missing = MissingSamples(length);
                    if (missing > 0)
                    {
                        concealRemaining = missing;
                        havePending = true;
                        pendingLength = length;
                        pendingGranule = granule;
                        pendingEnd = endOfStream;
                        continue;
                    }
                }
            }
            int frames = DecodePacket(length);
            if (frames <= 0) continue;
            long positionAfter = decodedEnd + frames;
            pcmStart = 0;
            pcmEnd = frames;
            if (granule >= 0)
            {
                if (!anchored)
                {
                    // After a seek the position is unknown until the end of the first page; everything before it precedes the target.
                    anchored = true;
                    positionAfter = granule;
                    long startOfPacket = granule - frames;
                    pcmStart = (int)Math.Clamp(seekTarget - startOfPacket, 0, frames);
                    skipRemaining = Math.Max(0, seekTarget - granule);
                }
                if (endOfStream && positionAfter > granule) pcmEnd -= (int)Math.Min(pcmEnd - pcmStart, positionAfter - granule);
            }
            decodedEnd = positionAfter;
            if (!anchored) pcmStart = pcmEnd;
            if (SkipAudio()) return true;
        }
        ended = true;
        return false;
    }

    // Sets up one decoder per coded stream: the coupled (stereo) streams come first, then the mono ones.
    private void Configure(OpusHead layout)
    {
        decoders = new OpusDecoder[layout.Streams];
        for (int i = 0; i < decoders.Length; i++) decoders[i] = new OpusDecoder(i < layout.Coupled ? 2 : 1);
        direct = layout.Streams == 1 && layout.Mapping.AsSpan().SequenceEqual(layout.Channels == 2 ? (byte[])[0, 1] : [0]);
        waveOrder = layout.Surround ? WaveOrder(layout.Channels) : [.. Enumerable.Range(0, layout.Channels)];
        streamPcm = [];
        if (!direct)
        {
            streamPcm = new float[layout.Streams][];
            for (int i = 0; i < streamPcm.Length; i++) streamPcm[i] = new float[OpusDecoder.MaxPacketFrames * (i < layout.Coupled ? 2 : 1)];
        }
    }

    // Decodes the Opus packet in packetBuffer into pcm. The packet of a multistream layout holds one self-delimited packet per stream, the last one plain.
    private int DecodePacket(int length)
    {
        if (direct) return decoders[0].Decode(packetBuffer, 0, length, pcm, 0, OpusDecoder.MaxPacketFrames);
        int offset = 0;
        int frames = -1;
        for (int s = 0; s < decoders.Length; s++)
        {
            if (offset >= length) return -1;
            int decoded;
            if (s < decoders.Length - 1)
            {
                decoded = decoders[s].DecodeSelfDelimited(packetBuffer, offset, length - offset, out int used, streamPcm[s], 0, OpusDecoder.MaxPacketFrames);
                offset += used;
            }
            else decoded = decoders[s].Decode(packetBuffer, offset, length - offset, streamPcm[s], 0, OpusDecoder.MaxPacketFrames);
            if (decoded <= 0 || (frames >= 0 && decoded != frames)) return -1;
            frames = decoded;
        }
        Assemble(frames);
        return frames;
    }

    // Conceals a lost stretch in every stream.
    private int Conceal(int chunk)
    {
        if (direct) return decoders[0].Decode(null, 0, 0, pcm, 0, chunk);
        int frames = chunk;
        for (int s = 0; s < decoders.Length; s++)
        {
            frames = decoders[s].Decode(null, 0, 0, streamPcm[s], 0, chunk);
            if (frames <= 0) return frames;
        }
        Assemble(frames);
        return frames;
    }

    // Interleaves the decoded streams into the output channels: channel c takes the stream channel the mapping table names (255 is silence), and
    // surround layouts are put in WAVE order.
    private void Assemble(int frames)
    {
        int channels = head.Channels;
        int coupled = head.Coupled;
        for (int c = 0; c < channels; c++)
        {
            int target = waveOrder[c];
            int m = head.Mapping[c];
            if (m == 255)
            {
                for (int i = 0; i < frames; i++) pcm[i * channels + target] = 0f;
                continue;
            }
            int stream = m < 2 * coupled ? m / 2 : coupled + (m - 2 * coupled);
            int width = stream < coupled ? 2 : 1;
            int offset = stream < coupled ? m & 1 : 0;
            float[] source = streamPcm[stream];
            for (int i = 0; i < frames; i++) pcm[i * channels + target] = source[i * width + offset];
        }
    }

    // Vorbis channel order (which Ogg Opus uses for mapping family 1) to WAVE order: where channel c goes.
    private static int[] WaveOrder(int channels) => channels switch
    {
        3 => [0, 2, 1],
        5 => [0, 2, 1, 3, 4],
        6 => [0, 2, 1, 4, 5, 3],
        7 => [0, 2, 1, 5, 6, 4, 3],
        8 => [0, 2, 1, 6, 7, 4, 5, 3],
        _ => [.. Enumerable.Range(0, channels)]
    };

    // Drops the audio that precedes the wanted position (the pre-skip, or the run-up to a seek). True when some audio is left.
    private bool SkipAudio()
    {
        if (skipRemaining > 0)
        {
            int drop = (int)Math.Min(skipRemaining, pcmEnd - pcmStart);
            pcmStart += drop;
            skipRemaining -= drop;
        }
        return pcmStart < pcmEnd;
    }

    // How many samples the lost pages held: the granule position at the end of the current page, less what has been played and what this page will play.
    private long MissingSamples(int length)
    {
        long pageGranule = packets.Pages.Header.Granule;
        if (pageGranule < 0) return 0;
        long own = Math.Max(0, OpusDecoder.PacketSamples(packetBuffer.AsSpan(0, length))) + packets.MeasureRestOfPage(OpusDecoder.PacketSamples);
        long missing = pageGranule - (decodedEnd + own);
        if (missing <= 0 || missing > ConcealLimit) return 0;
        return missing / 120 * 120;
    }

    // Produces up to 20 ms of concealment. False when nothing was produced.
    private bool Conceal()
    {
        int chunk = (int)Math.Min(concealRemaining, ConcealChunk);
        int frames = Conceal(chunk);
        if (frames <= 0)
        {
            concealRemaining = 0;
            return false;
        }
        concealRemaining -= frames;
        decodedEnd += frames;
        pcmStart = 0;
        pcmEnd = frames;
        return SkipAudio();
    }

    /// <summary>The identification header (RFC 7845 section 5.1) with the channel mapping.</summary>
    private readonly record struct OpusHead(int Channels, int PreSkip, float Gain, int Streams, int Coupled, byte[] Mapping, bool Surround)
    {
        public static bool IsHead(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual("OpusHead"u8);

        public static OpusHead Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length < 19) throw new InvalidDataException("The Opus identification header is truncated.");
            if (data[8] >> 4 != 0) throw new NotSupportedException("Unsupported Opus header version.");
            int channels = data[9];
            int preSkip = data[10] | data[11] << 8;
            short gainQ8 = (short)(data[16] | data[17] << 8);
            float gain = (float)Math.Pow(10.0, gainQ8 / (20.0 * 256.0));
            int family = data[18];
            if (family == 0)
            {
                if (channels is not (1 or 2)) throw new InvalidDataException("Opus mapping family 0 carries one or two channels.");
                return new OpusHead(channels, preSkip, gain, 1, channels - 1, channels == 2 ? [0, 1] : [0], false);
            }
            if (family is not (1 or 255)) throw new NotSupportedException("Opus channel mapping families other than 0, 1 and 255 are not supported.");
            if (channels < 1 || (family == 1 && channels > 8)) throw new InvalidDataException("The Opus channel count does not fit the mapping family.");
            if (data.Length < 21 + channels) throw new InvalidDataException("The Opus channel mapping table is truncated.");
            int streams = data[19];
            int coupled = data[20];
            if (streams < 1 || coupled > streams || streams + coupled > 255) throw new InvalidDataException("The Opus stream counts are invalid.");
            byte[] mapping = data.Slice(21, channels).ToArray();
            foreach (byte entry in mapping)
            {
                if (entry != 255 && entry >= streams + coupled) throw new InvalidDataException("The Opus channel mapping names a missing stream.");
            }
            return new OpusHead(channels, preSkip, gain, streams, coupled, mapping, family == 1);
        }
    }
}
