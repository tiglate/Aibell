using System.Buffers.Binary;

namespace Aibell.Audio.Mp3;

/// <summary>
/// MP3 decoder (MPEG-1, MPEG-2 and MPEG-2.5 Layer III, Layer II and Layer I, free-format streams) written from ISO/IEC 11172-3, ISO/IEC 13818-3 and the Fraunhofer "Bitstream Syntax and Decoding" description:
/// frame synchronization with resynchronization after damage, the bit reservoir, joint stereo, mixed and short blocks, the Xing/Info/LAME tag (frame count and gapless
/// trimming), and exact seeking through an index of the frames. A frame that cannot be decoded is replaced by silence. Decoding allocates nothing.
/// </summary>
public sealed class Mp3Decoder : IAudioDecoder
{
    private const int PrerollFrames = 10;
    private const int PrerollFramesLayer1 = 2;
    private const int FreeFormatWindow = 8300;
    private const int ResyncLimit = 64 * 1024;
    private const int MaximumIndexFrames = 16 * 1024 * 1024;

    private readonly Stream stream;
    private readonly Mp3ByteSource source;
    private readonly Mp3Layer3 layer = new();
    private readonly Mp3Layer1 layer1 = new();
    private readonly Mp3Layer2 layer2 = new();
    private readonly byte[] frameBytes = new byte[4096];
    private readonly Mp3Header reference;
    private readonly int channels;
    private readonly long[]? offsets;
    private readonly int audioFrames;
    private readonly long startSkip;
    private readonly long endSample;
    private readonly float[] pcm;
    private int pcmStart;
    private int pcmEnd;
    private long framePosition;
    private long outputFrom;
    private bool ended;
    private bool disposed;

    private Mp3Decoder(Stream stream, Mp3ByteSource source, Mp3Header reference, long[]? offsets, int audioFrames, long startSkip, long endSample, long totalFrames)
    {
        this.stream = stream;
        this.source = source;
        this.reference = reference;
        channels = reference.Channels;
        this.offsets = offsets;
        this.audioFrames = audioFrames;
        this.startSkip = startSkip;
        this.endSample = endSample;
        TotalFrames = totalFrames;
        pcm = new float[reference.SamplesPerFrame * channels];
        outputFrom = startSkip;
    }

    public int SampleRate => reference.SampleRate;

    public int Channels => channels;

    public long TotalFrames { get; }

    /// <summary>True when the bytes look like the start of an MP3 stream (an ID3v2 tag, or two valid frame headers in a row).</summary>
    public static bool Sniff(ReadOnlySpan<byte> head)
    {
        if (AudioSniffer.Id3v2Length(head) > 0) return true;
        for (int i = 0; i + 4 <= head.Length && i < head.Length - 4; i++)
        {
            if (head[i] != 0xFF) continue;
            if (!Mp3Header.TryParse(BinaryPrimitives.ReadUInt32BigEndian(head[i..]), out Mp3Header header))
            {
                if (Mp3Header.MeasureFreeFormat(head[i..], dataEnds: true) > 0) return true;
                continue;
            }
            int next = i + header.FrameLength;
            if (next + 4 > head.Length) return true;
            if (Mp3Header.TryParse(BinaryPrimitives.ReadUInt32BigEndian(head[next..]), out Mp3Header second) && header.IsCompatibleWith(second)) return true;
        }
        return false;
    }

    /// <summary>True when the bytes start with a valid Layer III frame header (the stream is positioned after any ID3v2 tag).</summary>
    public static bool StartsWithFrame(ReadOnlySpan<byte> head)
        => head.Length >= 4 && head[0] == 0xFF
            && (Mp3Header.TryParse(BinaryPrimitives.ReadUInt32BigEndian(head), out _) || Mp3Header.MeasureFreeFormat(head, dataEnds: true) > 0);

    public static Mp3Decoder Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Mp3ByteSource source = new(stream);
        SkipId3v2(source);
        if (!FindFirstFrame(source, out Mp3Header first, out long firstOffset)) throw new InvalidDataException("This is not an MP3 stream.");

        // The first frame may be a Xing/Info frame without audio.
        byte[] probe = new byte[4096];
        source.Ensure(first.FrameLength);
        source.Window[..first.FrameLength].CopyTo(probe);
        Mp3InfoTag tag = default;
        bool hasInfo = first.Layer == 3 && Mp3InfoTag.TryParse(probe.AsSpan(0, first.FrameLength), first, out tag);
        long audioStart = firstOffset + (hasInfo ? first.FrameLength : 0);
        if (hasInfo) source.Advance(first.FrameLength);

        long[]? offsets = null;
        int frameCount = tag.Frames;
        if (StreamLength.TryGet(stream, out long length))
        {
            offsets = BuildIndex(source, first, audioStart, length);
            frameCount = offsets.Length;
            source.Seek(audioStart);
        }
        long total = -1;
        long startSkip = 0;
        long endSample = long.MaxValue;
        if (frameCount > 0)
        {
            long samples = (long)frameCount * first.SamplesPerFrame;
            if (hasInfo && tag.HasGaplessInfo)
            {
                startSkip = tag.EncoderDelay + Mp3InfoTag.DecoderDelay;
                endSample = tag.EncoderPadding >= Mp3InfoTag.DecoderDelay ? samples - tag.EncoderPadding + Mp3InfoTag.DecoderDelay : samples;
            }
            else endSample = samples;
            total = Math.Max(0, endSample - startSkip);
        }
        return new Mp3Decoder(stream, source, first, offsets, offsets?.Length ?? 0, startSkip, endSample, total);
    }

    public int Read(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int produced = 0;
        while (produced < frames)
        {
            if (pcmStart >= pcmEnd && !DecodeNext()) break;
            int count = Math.Min(pcmEnd - pcmStart, frames - produced);
            pcm.AsSpan(pcmStart * channels, count * channels).CopyTo(destination[(produced * channels)..]);
            pcmStart += count;
            produced += count;
        }
        return produced;
    }

    public bool Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (offsets is null || frame < 0) return false;
        if (offsets.Length == 0)
        {
            ended = true;
            return true;
        }
        pcmStart = pcmEnd = 0;
        ended = false;
        long target = frame + startSkip;
        if (target >= endSample)
        {
            ended = true;
            return true;
        }
        int samplesPerFrame = reference.SamplesPerFrame;
        long wanted = target / samplesPerFrame;
        long startFrame = Math.Max(0, wanted - (reference.Layer != 3 ? PrerollFramesLayer1 : PrerollFrames));
        source.Seek(offsets[startFrame]);
        layer.Reset();
        layer1.Reset();
        layer2.Reset();
        framePosition = startFrame;
        outputFrom = target;
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
    }

    // ---- stream structure -----------------------------------------------------------------------------------------------------------------

    private static void SkipId3v2(Mp3ByteSource source)
    {
        if (!source.Ensure(10)) return;
        int length = AudioSniffer.Id3v2Length(source.Window[..10]);
        if (length > 0) source.Skip(length);
    }

    private static bool FindFirstFrame(Mp3ByteSource source, out Mp3Header header, out long offset)
    {
        header = default;
        offset = -1;
        for (int scanned = 0; scanned < ResyncLimit; scanned++)
        {
            if (!source.Ensure(4)) return false;
            if (source.Window[0] == 0xFF && TryParseCandidate(source, out Mp3Header candidate) && IsFollowedByFrame(source, candidate))
            {
                header = candidate;
                offset = source.Position;
                return true;
            }
            source.Advance(1);
        }
        return false;
    }

    // A free-format header is valid once the frame length of the stream has been measured from the data that follows.
    private static bool TryParseCandidate(Mp3ByteSource source, out Mp3Header header)
    {
        uint word = BinaryPrimitives.ReadUInt32BigEndian(source.Window);
        if (Mp3Header.TryParse(word, out header)) return true;
        if (((word >> 12) & 15) != 0) return false;
        source.Ensure(FreeFormatWindow);
        int bytes = Mp3Header.MeasureFreeFormat(source.Window, dataEnds: source.Window.Length < FreeFormatWindow);
        return bytes > 0 && bytes < 4096 - 4 && Mp3Header.TryParse(word, out header, bytes);
    }

    // A header is believed when the next frame starts where this one ends (or the data ends there).
    private static bool IsFollowedByFrame(Mp3ByteSource source, in Mp3Header header)
    {
        int next = header.FrameLength;
        if (next + 4 > 8000) return true;
        if (!source.Ensure(next + 4)) return source.Window.Length >= next;
        return Mp3Header.TryParse(BinaryPrimitives.ReadUInt32BigEndian(source.Window[next..]), out Mp3Header second, header.FreeFormatBytes) && header.IsCompatibleWith(second);
    }

    private static long[] BuildIndex(Mp3ByteSource source, in Mp3Header reference, long start, long length)
    {
        List<long> positions = [];
        source.Seek(start);
        while (positions.Count < MaximumIndexFrames)
        {
            long position = source.Position;
            if (!source.Ensure(4)) break;
            if (!Mp3Header.TryParse(BinaryPrimitives.ReadUInt32BigEndian(source.Window), out Mp3Header header, reference.FreeFormatBytes) || !reference.IsCompatibleWith(header))
            {
                // Garbage between frames: look for the next frame before giving up.
                if (!ResyncForward(source, reference)) break;
                continue;
            }
            if (position + header.FrameLength > length) break;
            positions.Add(position);
            source.Skip(header.FrameLength);
        }
        return [.. positions];
    }

    private static bool ResyncForward(Mp3ByteSource source, in Mp3Header reference)
    {
        for (int scanned = 0; scanned < ResyncLimit; scanned++)
        {
            source.Advance(1);
            if (!source.Ensure(4)) return false;
            if (source.Window[0] == 0xFF && Mp3Header.TryParse(BinaryPrimitives.ReadUInt32BigEndian(source.Window), out Mp3Header candidate, reference.FreeFormatBytes)
                && reference.IsCompatibleWith(candidate) && IsFollowedByFrame(source, candidate))
            {
                return true;
            }
        }
        return false;
    }

    // ---- decoding -------------------------------------------------------------------------------------------------------------------------

    /// <summary>Decodes frames until some of the wanted output is ready. False at the end of the stream.</summary>
    private bool DecodeNext()
    {
        if (ended) return false;
        int samplesPerFrame = reference.SamplesPerFrame;
        while (true)
        {
            long frameStart = framePosition * samplesPerFrame;
            if (frameStart >= endSample || !ReadFrame(out Mp3Header header, out int length))
            {
                ended = true;
                return false;
            }
            framePosition++;
            if (header.Layer == 1) layer1.Decode(frameBytes, length, header);
            else if (header.Layer == 2) layer2.Decode(frameBytes, length, header);
            else layer.Decode(frameBytes, length, header);
            Interleave(header);
            long from = Math.Max(outputFrom, frameStart);
            long to = Math.Min(endSample, frameStart + samplesPerFrame);
            if (from >= to) continue;
            pcmStart = (int)(from - frameStart);
            pcmEnd = (int)(to - frameStart);
            return true;
        }
    }

    private bool ReadFrame(out Mp3Header header, out int length)
    {
        header = default;
        length = 0;
        for (int scanned = 0; scanned < ResyncLimit; scanned++)
        {
            if (!source.Ensure(4)) return false;
            if (source.Window[0] == 0xFF && Mp3Header.TryParse(BinaryPrimitives.ReadUInt32BigEndian(source.Window), out header, reference.FreeFormatBytes) && reference.IsCompatibleWith(header))
            {
                length = header.FrameLength;
                if (length > frameBytes.Length) return false;
                if (!source.Ensure(length)) return false;
                source.Window[..length].CopyTo(frameBytes);
                source.Advance(length);
                return true;
            }
            source.Advance(1);
        }
        return false;
    }

    private void Interleave(in Mp3Header header)
    {
        int samples = header.SamplesPerFrame;
        float[][] output = header.Layer == 1 ? layer1.Output : header.Layer == 2 ? layer2.Output : layer.Output;
        int sourceChannels = header.Channels;
        if (channels == 1)
        {
            if (sourceChannels == 1) output[0].AsSpan(0, samples).CopyTo(pcm);
            else
            {
                for (int i = 0; i < samples; i++) pcm[i] = (output[0][i] + output[1][i]) * 0.5f;
            }
        }
        else if (sourceChannels == 2)
        {
            for (int i = 0; i < samples; i++)
            {
                pcm[2 * i] = output[0][i];
                pcm[2 * i + 1] = output[1][i];
            }
        }
        else
        {
            for (int i = 0; i < samples; i++) pcm[2 * i] = pcm[2 * i + 1] = output[0][i];
        }
    }
}
