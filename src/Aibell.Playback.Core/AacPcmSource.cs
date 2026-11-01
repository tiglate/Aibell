using Aibell.Audio.Aac;

namespace Aibell.Playback.Core;

/// <summary>
/// Plays AAC-LC from a byte stream with the managed decoder: raw ADTS (Icecast/Shoutcast, packed-audio HLS) or MPEG-TS (HLS), converted
/// to stereo at the device rate. Damaged frames become silence, never an exception; streams the decoder cannot play (HE-AAC, other
/// codecs) are refused up front with the usual "choose VLC" message. Reads block, so it is only used from the decoding thread.
/// </summary>
internal sealed class AacPcmSource : IPcmSource
{
    private const int TransportChunk = 188 * 21;
    private const int ConsecutiveFailuresTolerated = 400;
    private const int PrimeFrames = 3;

    private readonly Stream stream;
    private readonly bool transport;
    private readonly Func<long>? segmentCounter;
    private readonly int outputRate;
    private readonly AdtsFrameReader adts = new();
    private readonly AacDecoder decoder = new();
    private readonly TransportStreamDemuxer? demuxer;
    private readonly byte[] chunk = new byte[TransportChunk];
    private readonly byte[] elementary = new byte[TransportChunk + 188];
    private readonly float[] frame = new float[2 * AacDecoder.FrameLength];
    private readonly float[] converted = new float[2 * AacDecoder.FrameLength * 8];
    private int elementaryOffset;
    private int elementaryLength;
    private long lastSegment;
    private StereoResampler? resampler;
    private int consecutiveFailures;
    private List<long>? frameOffsets;
    private int indexRate;
    private int discardFrames;
    private long framesSeen;
    private long frameBytesSeen;
    private long firstFrameOffset = -1;
    private bool inputEnded;
    private bool crcTrusted;
    private bool primed;
    private bool disposed;

    private AacPcmSource(Stream stream, bool transport, Func<long>? segmentCounter, uint outputRate)
    {
        this.stream = stream;
        this.transport = transport;
        this.segmentCounter = segmentCounter;
        this.outputRate = (int)outputRate;
        lastSegment = segmentCounter?.Invoke() ?? 0;
        if (transport) demuxer = new TransportStreamDemuxer(AppendElementary, adts.MarkDiscontinuity);
    }

    /// <summary>
    /// Opens a source on <paramref name="stream"/> (which it then owns) after decoding the first frames, so HE-AAC and other refusals
    /// surface as <see cref="NotSupportedException"/> before playback starts. Returns null (leaving the stream open) when the data is not AAC.
    /// </summary>
    public static AacPcmSource? TryOpen(Stream stream, uint outputRate, Func<long>? segmentCounter = null, AacContainer? knownContainer = null)
    {
        AacContainer container;
        if (knownContainer is { } known) container = known;
        else
        {
            if (!stream.CanSeek) return null;
            byte[] head = new byte[32 * 1024];
            int filled = 0;
            while (filled < head.Length)
            {
                int read = stream.Read(head, filled, head.Length - filled);
                if (read <= 0) break;
                filled += read;
            }
            stream.Seek(0, SeekOrigin.Begin);
            container = AacStreamInspector.Sniff(head.AsSpan(0, filled));
        }
        if (container is AacContainer.Mp4)
            throw new NotSupportedException(AudioSourceFactory.UnsupportedMessage("The built-in player cannot play this MP4 audio."));
        if (container is not (AacContainer.Adts or AacContainer.MpegTs)) return null;

        AacPcmSource source = new(stream, container == AacContainer.MpegTs, segmentCounter, outputRate);
        try
        {
            source.Prime();
            return source;
        }
        catch
        {
            source.disposed = true;
            throw;
        }
    }

    private void Prime()
    {
        // Decode the first frames now: refusals (SBR, unsupported profile, a TS that carries MP3 or AC-3) must be reported before playback.
        try
        {
            int decoded = 0;
            while (decoded < PrimeFrames && DecodeNextFrame()) decoded++;
            if (decoded == 0) throw new NotSupportedException(AudioSourceFactory.UnsupportedMessage("The built-in player found no AAC audio in this stream."));
        }
        catch (AacUnsupportedException exception)
        {
            throw new NotSupportedException(AudioSourceFactory.UnsupportedMessage("The built-in player cannot play this AAC stream: " + exception.Message), exception);
        }
        if (demuxer is { Kind: not (TransportAudioKind.AacAdts or TransportAudioKind.None) })
            throw new NotSupportedException(AudioSourceFactory.UnsupportedMessage("The built-in player cannot play this HLS stream: its audio is not AAC."));
        primed = true;
    }

    public int Read(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int produced = 0;
        while (produced < frames)
        {
            int got = resampler is null ? 0 : resampler.Pull(destination[(produced * 2)..], frames - produced);
            produced += got;
            if (got == 0 && !DecodeNextFrame()) break;
        }
        return produced;
    }

    /// <summary>Decodes one more frame into the resampler. False when the input has ended.</summary>
    private bool DecodeNextFrame()
    {
        while (true)
        {
            if (adts.TryRead(out AdtsHeader header, out _))
            {
                DecodeFrame(header);
                return true;
            }
            if (inputEnded) return false;
            FeedMore();
        }
    }

    // Decodes every raw data block of the ADTS frame just read and delivers each one's audio; a block that fails becomes silence.
    private void DecodeFrame(in AdtsHeader header)
    {
        ReadOnlySpan<byte> frame = adts.LastFrame;
        int offset = header.HeaderLength;
        bool headerDamaged = AdtsCrc.VerifyHeader(frame, header) == false && crcTrusted;
        framesSeen += header.RawBlocks;
        frameBytesSeen += header.FrameLength;
        if (firstFrameOffset < 0) firstFrameOffset = adts.LastFrameOffset;
        for (int block = 0; block < header.RawBlocks; block++)
        {
            try
            {
                if (headerDamaged) throw new AacException("The frame header failed its CRC.");
                decoder.DecodeBlock(frame, header, block, ref offset, out bool? crc);
                CheckCrc(crc);
                consecutiveFailures = 0;
            }
            catch (AacUnsupportedException)
            {
                if (!primed) throw;
                throw new NotSupportedException(AudioSourceFactory.UnsupportedMessage("The built-in player cannot play this AAC stream: it changed to a format that is not supported."));
            }
            catch (AacException)
            {
                if (++consecutiveFailures > ConsecutiveFailuresTolerated) throw new InvalidDataException("The stream is not valid AAC audio.");
                decoder.Conceal(Math.Max(1, decoder.Channels));
                // Without the end of this block the next one cannot be found: the rest of the frame is silent too.
                for (int rest = block; rest < header.RawBlocks; rest++)
                {
                    if (rest > block) decoder.Conceal(Math.Max(1, decoder.Channels));
                    Output(header);
                }
                return;
            }
            Output(header);
        }
    }

    // After a seek the first block only rebuilds the overlap of the filterbank and is not played.
    private void Output(in AdtsHeader header)
    {
        if (discardFrames > 0) discardFrames--;
        else Deliver(decoder.SampleRate == 0 ? header.SampleRate : decoder.SampleRate);
    }

    // The CRC rules were implemented from ISO/IEC 13818-7 without a reference stream to confirm them, so a failing CRC only discards a frame once
    // some frame of the same stream has passed: a stream whose CRCs never match is taken to be read wrongly by us, not damaged.
    private void CheckCrc(bool? verdict)
    {
        if (verdict is null) return;
        if (verdict == true) crcTrusted = true;
        else if (crcTrusted) throw new AacException("The frame failed its CRC.");
    }

    private void Deliver(int inputRate)
    {
        if (resampler is null || resampler.InputRate != inputRate) resampler = new StereoResampler(inputRate, outputRate, (PrimeFrames + 1) * 4 * AacDecoder.FrameLength);
        decoder.MixToStereo(frame);
        resampler.Push(frame, AacDecoder.FrameLength);
    }

    private void FeedMore()
    {
        if (!transport)
        {
            Span<byte> room = adts.GetWriteBuffer();
            int read = stream.Read(room);
            if (read <= 0)
            {
                inputEnded = true;
                adts.Complete();
                return;
            }
            adts.Advance(read);
            return;
        }

        if (elementaryOffset < elementaryLength)
        {
            Transfer();
            return;
        }
        int bytes = stream.Read(chunk);
        if (bytes <= 0)
        {
            elementaryOffset = elementaryLength = 0;
            demuxer!.Complete();
            Transfer();
            if (elementaryOffset >= elementaryLength)
            {
                inputEnded = true;
                adts.Complete();
            }
            return;
        }
        elementaryOffset = elementaryLength = 0;
        long segment = segmentCounter?.Invoke() ?? 0;
        if (segment != lastSegment)
        {
            // The bytes just read belong to a new segment; the end of the previous one is flushed into the elementary buffer first.
            lastSegment = segment;
            demuxer!.StartSegment();
        }
        demuxer!.Push(chunk.AsSpan(0, bytes));
        Transfer();
    }

    private void Transfer()
    {
        Span<byte> room = adts.GetWriteBuffer();
        int copy = Math.Min(room.Length, elementaryLength - elementaryOffset);
        elementary.AsSpan(elementaryOffset, copy).CopyTo(room);
        adts.Advance(copy);
        elementaryOffset += copy;
    }

    private void AppendElementary(ReadOnlySpan<byte> data)
    {
        if (elementaryLength + data.Length > elementary.Length) data = data[..(elementary.Length - elementaryLength)];
        data.CopyTo(elementary.AsSpan(elementaryLength));
        elementaryLength += data.Length;
    }

    /// <summary>
    /// Local ADTS files seek exactly: the first seek indexes every frame's offset, then decoding restarts one frame before the target (to rebuild
    /// the filterbank overlap), accurate to one frame (about 21 ms). Network files that honour byte ranges seek by estimate, since indexing
    /// would mean downloading the whole file: the byte position comes from the average frame size seen so far, which is exact for constant-bitrate
    /// files and approximate for variable-bitrate ones (the position then drifts by the estimate's error, as the player counts from the requested time).
    /// Live streams and HLS refuse, and playback goes on from where it is.
    /// </summary>
    public bool Seek(long frame)
    {
        if (transport) return false;
        double seconds = Math.Max(0, frame) / (double)outputRate;
        long inputFrame;
        long byteOffset;
        if (stream is FileStream)
        {
            frameOffsets ??= BuildIndex();
            if (frameOffsets.Count == 0 || indexRate == 0) return false;
            inputFrame = (long)(seconds * indexRate / AacDecoder.FrameLength);
            int target = (int)Math.Min(inputFrame, frameOffsets.Count - 1);
            int first = Math.Max(0, target - 1);
            stream.Seek(frameOffsets[first], SeekOrigin.Begin);
            ResetAfterSeek(target - first);
            return true;
        }
        if (stream is HttpAudioStream { CanRangeSeek: true } network && framesSeen > 0 && decoder.SampleRate > 0)
        {
            double averageFrame = (double)frameBytesSeen / framesSeen;
            inputFrame = (long)(seconds * decoder.SampleRate / AacDecoder.FrameLength);
            byteOffset = (long)(firstFrameOffset + (inputFrame - 1) * averageFrame);
            byteOffset = Math.Clamp(byteOffset, 0, Math.Max(0, network.Length - 1));
            try
            {
                network.Seek(byteOffset, SeekOrigin.Begin);
            }
            catch (IOException)
            {
                return false;
            }
            // Landing mid-frame: the reader resynchronizes and the first frame found only rebuilds the overlap.
            ResetAfterSeek(1);
            return true;
        }
        return false;
    }

    private void ResetAfterSeek(int discard)
    {
        adts.Reset();
        decoder.Reset();
        resampler = null;
        inputEnded = false;
        consecutiveFailures = 0;
        discardFrames = discard;
    }

    private List<long> BuildIndex()
    {
        List<long> offsets = [];
        stream.Seek(0, SeekOrigin.Begin);
        AdtsFrameReader scan = new();
        while (true)
        {
            while (scan.TryRead(out AdtsHeader header, out _))
            {
                if (offsets.Count == 0) indexRate = header.SampleRate;
                offsets.Add(scan.LastFrameOffset);
            }
            Span<byte> room = scan.GetWriteBuffer();
            int read = stream.Read(room);
            if (read <= 0) break;
            scan.Advance(read);
        }
        scan.Complete();
        while (scan.TryRead(out _, out _)) offsets.Add(scan.LastFrameOffset);
        return offsets;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
    }
}
