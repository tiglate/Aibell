namespace Aibell.Audio.Aac;

/// <summary>
/// Decodes AAC-LC (and Main without prediction) audio in MP4 / M4A files, the first AAC track. Output is interleaved float in the WAVE channel
/// order at the stream's own rate. The edit list is honoured: the encoder delay at the start and the padding at the end are not played, and
/// <see cref="TotalFrames"/> is the length that remains. Seeking decodes the block before the target to rebuild the filterbank overlap, so it is
/// exact. A damaged block becomes silence of the same length; sixteen in a row end the stream with an <see cref="InvalidDataException"/>.
/// HE-AAC and the other profiles are refused when the file is opened (<see cref="NotSupportedException"/>), or, for implicit SBR,
/// when the first block is decoded. Decoding allocates nothing once the file is open.
/// </summary>
public sealed class Mp4AacDecoder : IAudioDecoder
{
    private const int MaxConsecutiveFailures = 16;

    // Index of each WAVE-order channel in the decoder's own order (centre first), per channel configuration.
    private static readonly int[][] WaveOrder =
    [
        [],
        [0],
        [0, 1],
        [1, 2, 0],
        [1, 2, 0, 3],
        [1, 2, 0, 3, 4],
        [1, 2, 0, 5, 3, 4],
        [1, 2, 0, 7, 5, 6, 3, 4]
    ];

    private readonly Stream stream;
    private readonly Mp4AacTrack track;
    private readonly AacDecoder decoder = new();
    private readonly int[] order;
    private readonly byte[] packet;
    private readonly float[] block;
    private int blockPosition;
    private int blockLength;
    private int nextPacket;
    private int failures;
    private long position;

    private Mp4AacDecoder(Stream stream, Mp4AacTrack track)
    {
        this.stream = stream;
        this.track = track;
        order = WaveOrder[track.Config.ChannelConfiguration];
        packet = new byte[Math.Max(1, track.Sizes.Length == 0 ? 1 : track.Sizes.Max())];
        block = new float[AacDecoder.FrameLength * order.Length];
    }

    public int SampleRate => track.Config.SampleRate;

    public int Channels => order.Length;

    public long TotalFrames => track.FrameCount;

    /// <summary>
    /// Reads the movie box of the file from <paramref name="stream"/> (seekable, positioned at its start). Throws <see cref="InvalidDataException"/> or
    /// <see cref="NotSupportedException"/> (also for MP4 files without AAC, fragmented files and HE-AAC).
    /// </summary>
    public static Mp4AacDecoder Open(Stream stream)
    {
        if (!stream.CanSeek) throw new NotSupportedException("MP4 decoding needs a seekable stream.");
        Mp4AacTrack track = Mp4AacTrack.Read(stream);
        Mp4AacDecoder decoder = new(stream, track);
        decoder.Start();
        return decoder;
    }

    public int Read(Span<float> destination, int frames)
    {
        int channels = Channels;
        int produced = 0;
        while (produced < frames && position < track.FrameCount)
        {
            if (blockPosition >= blockLength && !LoadNextPacket()) break;
            int take = (int)Math.Min(Math.Min(frames - produced, blockLength - blockPosition), track.FrameCount - position);
            block.AsSpan(blockPosition * channels, take * channels).CopyTo(destination[(produced * channels)..]);
            blockPosition += take;
            produced += take;
            position += take;
        }
        return produced;
    }

    public bool Seek(long frame)
    {
        if (frame < 0 || frame > track.FrameCount) return false;
        blockPosition = blockLength = 0;
        position = frame;
        if (frame == track.FrameCount) return true;
        long timeline = frame + track.StartFrame;
        int index = (int)(timeline / AacDecoder.FrameLength);
        if (index >= track.PacketCount) return false;
        // The filterbank overlaps consecutive blocks: the one before the target is decoded (and dropped) so the target block comes out whole.
        decoder.Reset();
        failures = 0;
        if (index > 0)
        {
            nextPacket = index - 1;
            LoadNextPacket();
        }
        nextPacket = index;
        if (!LoadNextPacket()) return false;
        blockPosition = (int)(timeline - (long)index * AacDecoder.FrameLength);
        return true;
    }

    public void Dispose() => stream.Dispose();

    // Decodes the first block now so implicit HE-AAC (SBR signalled inside the data) is refused at open time; then rewinds to the start of the audio.
    private void Start()
    {
        if (track.PacketCount > 0)
        {
            nextPacket = 0;
            LoadNextPacket();
        }
        if (!Seek(0)) position = 0;
    }

    private bool LoadNextPacket()
    {
        if (nextPacket >= track.PacketCount) return false;
        int index = nextPacket++;
        int size = track.Sizes[index];
        int channels = order.Length;
        try
        {
            if (stream.Position != track.Offsets[index]) stream.Seek(track.Offsets[index], SeekOrigin.Begin);
            stream.ReadExactly(packet.AsSpan(0, size));
            AudioSpecificConfig config = track.Config;
            decoder.Decode(config.ObjectType, config.SamplingIndex, config.ChannelConfiguration, packet.AsSpan(0, size));
            failures = 0;
        }
        catch (AacUnsupportedException exception)
        {
            throw new NotSupportedException("This AAC audio uses a feature that is not supported: " + exception.Message, exception);
        }
        catch (Exception exception) when (exception is AacException or EndOfStreamException)
        {
            if (++failures >= MaxConsecutiveFailures) throw new InvalidDataException("The AAC stream is damaged.", exception);
            decoder.Conceal(channels);
        }
        for (int c = 0; c < channels; c++)
        {
            ReadOnlySpan<float> source = decoder.GetChannel(order[c]);
            for (int n = 0; n < AacDecoder.FrameLength; n++) block[n * channels + c] = source[n];
        }
        blockPosition = 0;
        blockLength = AacDecoder.FrameLength;
        return true;
    }
}
