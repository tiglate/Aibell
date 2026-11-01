using Aibell.Audio.Ogg;

namespace Aibell.Audio.Vorbis;

/// <summary>
/// Ogg Vorbis I decoder written from the Vorbis I specification and RFC 3533: header parsing, floor 1, residue 0/1/2, channel coupling, the inverse MDCT with
/// overlap-add, granule-position based trimming at the start and end, chained streams (of the same format), and seeking by bisecting the pages by granule position.
/// Floor type 0 (used by no encoder since 2001) is not supported. Decoding allocates nothing once the stream is open.
/// </summary>
public sealed class VorbisDecoder : IAudioDecoder
{
    private readonly Stream stream;
    private readonly OggPacketReader packets;
    private VorbisIdentification identification;
    private VorbisSynthesizer synthesizer;
    private readonly long audioStart;
    private readonly long streamEnd;
    private int pcmStart;
    private int pcmEnd;
    private long decodedEnd;
    private bool granuleSynced;
    private long skipRemaining;
    private bool anchored = true;
    private long seekTarget;
    private bool ended;
    private bool disposed;

    private VorbisDecoder(Stream stream, OggPacketReader packets, VorbisIdentification identification, VorbisSynthesizer synthesizer, long audioStart, long totalFrames)
    {
        this.stream = stream;
        this.packets = packets;
        this.identification = identification;
        this.synthesizer = synthesizer;
        this.audioStart = audioStart;
        TotalFrames = totalFrames;
        streamEnd = packets.CanSeek ? packets.Pages.Length : -1;
    }

    public int SampleRate => identification.SampleRate;

    public int Channels => identification.Channels;

    public long TotalFrames { get; }

    /// <summary>True when the bytes start an Ogg stream carrying Vorbis.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head)
    {
        // "OggS", then the first page's segment table and its first packet: 27 header bytes + one lace + the identification header's first 7 bytes.
        if (head.Length < 35 || !head[..4].SequenceEqual("OggS"u8)) return false;
        int segments = head[26];
        return head.Length >= 27 + segments + 7 && VorbisIdentification.IsIdentification(head[(27 + segments)..]);
    }

    public static VorbisDecoder Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        OggPacketReader packets = new(stream, VorbisIdentification.IsIdentification);
        (VorbisIdentification identification, VorbisSetup setup) = ReadHeaders(packets);
        long audioStart = packets.CanSeek ? packets.Pages.Position : -1;
        long total = -1;
        if (packets.CanSeek)
        {
            long length = packets.Pages.Length;
            if (OggSeeker.FindLastPage(packets.Pages, packets.SerialNumber, audioStart, length, out OggPageHeader last, out bool foreignAfter) && !foreignAfter) total = last.Granule;
            packets.SeekToPage(audioStart);
        }
        VorbisSynthesizer synthesizer = new(identification, setup);
        VorbisDecoder decoder = new(stream, packets, identification, synthesizer, audioStart, total);
        decoder.Prime();
        return decoder;
    }

    public int Read(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int channels = identification.Channels;
        int produced = 0;
        while (produced < frames)
        {
            if (pcmStart >= pcmEnd && !DecodeNext()) break;
            int count = Math.Min(pcmEnd - pcmStart, frames - produced);
            synthesizer.Output.AsSpan(pcmStart * channels, count * channels).CopyTo(destination[(produced * channels)..]);
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
        synthesizer.Reset();
        if (frame > 0 && OggSeeker.FindLastPageAtOrBefore(packets.Pages, packets.SerialNumber, audioStart, streamEnd, frame, out long offset, out long granule) && granule > 0)
        {
            packets.SeekToPage(offset);
            anchored = false;
            seekTarget = frame;
            skipRemaining = 0;
            granuleSynced = true;
            decodedEnd = 0;
            return true;
        }
        packets.SeekToPage(audioStart);
        anchored = true;
        granuleSynced = false;
        decodedEnd = 0;
        skipRemaining = frame;
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
    }

    private static (VorbisIdentification, VorbisSetup) ReadHeaders(OggPacketReader packets)
    {
        if (!packets.ReadPacket(out OggPacket first) || !VorbisIdentification.IsIdentification(first.Data)) throw new InvalidDataException("This is not an Ogg Vorbis stream.");
        VorbisIdentification identification = VorbisIdentification.Parse(first.Data);
        if (!packets.ReadPacket(out OggPacket comment) || comment.Data.Length < 7 || comment.Data[0] != 3 || !comment.Data.Slice(1, 6).SequenceEqual("vorbis"u8))
            throw new InvalidDataException("The Vorbis comment header is missing.");
        if (!packets.ReadPacket(out OggPacket setupPacket)) throw new InvalidDataException("The Vorbis setup header is missing.");
        VorbisSetup setup = VorbisSetup.Parse(setupPacket.Data, identification.Channels);
        return (identification, setup);
    }

    // Decodes the first audio packets now so an unplayable stream is refused up front.
    private void Prime()
    {
        if (!DecodeNext()) throw new InvalidDataException("The Vorbis stream has no audio.");
        // A stream that cannot seek keeps what was just decoded; otherwise start over so that playback begins from the first packet.
        if (!packets.CanSeek) return;
        pcmStart = 0;
        granuleSynced = false;
        decodedEnd = 0;
        packets.SeekToPage(audioStart);
        synthesizer.Reset();
        pcmEnd = 0;
        anchored = true;
        ended = false;
    }

    /// <summary>Decodes packets until some audio is ready. False at the end of the stream.</summary>
    private bool DecodeNext()
    {
        if (ended) return false;
        while (packets.ReadPacket(out OggPacket packet))
        {
            if (packet.BeginOfStream)
            {
                // A chained stream: only continued when its format is the same, since the output format cannot change mid-stream.
                try
                {
                    if (!VorbisIdentification.IsIdentification(packet.Data)) continue;
                    VorbisIdentification next = VorbisIdentification.Parse(packet.Data);
                    if (next.Channels != identification.Channels || next.SampleRate != identification.SampleRate) break;
                    if (!packets.ReadPacket(out _) || !packets.ReadPacket(out OggPacket setupPacket)) break;
                    VorbisSetup setup = VorbisSetup.Parse(setupPacket.Data, next.Channels);
                    identification = next;
                    synthesizer = new VorbisSynthesizer(next, setup);
                    decodedEnd = 0;
                    granuleSynced = false;
                    continue;
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
                {
                    break;
                }
            }
            if (packet.LostBefore) synthesizer.Reset();
            int frames = synthesizer.Decode(packet.Data);
            if (frames < 0) continue;
            long positionAfter = decodedEnd + frames;
            pcmStart = 0;
            pcmEnd = frames;
            long granule = packet.PageGranule;
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
                    granuleSynced = true;
                }
                else if (!granuleSynced)
                {
                    granuleSynced = true;
                    // A first page whose granule is below the number of samples decoded asks to discard the excess at the start of the stream (the
                    // stream's last page does the same at its end, which is handled below).
                    if (!packet.EndOfStream && granule < positionAfter) pcmStart = (int)Math.Min(frames, positionAfter - granule);
                }
                if (packet.EndOfStream && positionAfter > granule) pcmEnd -= (int)Math.Min(pcmEnd - pcmStart, positionAfter - granule);
            }
            decodedEnd = positionAfter;
            if (!anchored) pcmStart = pcmEnd;
            if (skipRemaining > 0)
            {
                int drop = (int)Math.Min(skipRemaining, pcmEnd - pcmStart);
                pcmStart += drop;
                skipRemaining -= drop;
            }
            if (pcmStart < pcmEnd) return true;
        }
        ended = true;
        return false;
    }
}
