// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 Tiglate Pileser III.
// Part of Aibell.Audio.Alac, which is licensed under the Apache License, Version 2.0 (see LICENSE.Alac and NOTICE).

namespace Aibell.Audio.Alac;

/// <summary>
/// Decodes Apple Lossless audio in MP4 / M4A files (the first ALAC track): 16, 20, 24 and 32 bit, one to eight channels, any sample rate.
/// Output is interleaved float in the WAVE channel order; integers up to 24 bits convert exactly. Every packet stands alone, so seeking is
/// exact and needs no pre-roll. A damaged packet is replaced by silence of the same length; sixteen in a row end the stream with an
/// <see cref="InvalidDataException"/>. Decoding allocates nothing once the file is open.
/// </summary>
public sealed class AlacDecoder : IAudioDecoder
{
    private const int MaxConsecutiveFailures = 16;

    private readonly Stream stream;
    private readonly Mp4AlacTrack track;
    private readonly AlacFrameDecoder decoder;
    private readonly byte[] packet;
    private readonly float[] block;
    private int blockPosition;
    private int blockLength;
    private int nextPacket;
    private int failures;

    private AlacDecoder(Stream stream, Mp4AlacTrack track)
    {
        this.stream = stream;
        this.track = track;
        decoder = new AlacFrameDecoder(track.Cookie);
        packet = new byte[track.Sizes.Max()];
        block = new float[decoder.Config.FrameLength * decoder.Config.Channels];
    }

    public int SampleRate => decoder.Config.SampleRate;

    public int Channels => decoder.Config.Channels;

    public long TotalFrames => track.TotalFrames;

    /// <summary>Bits per sample of the encoded audio (16, 20, 24 or 32).</summary>
    public int BitDepth => decoder.Config.BitDepth;

    /// <summary>True when the first bytes look like an MP4 file (an 'ftyp' box); whether it holds ALAC is only known after <see cref="Open"/>.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head) => head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8);

    /// <summary>Reads the movie box of the file from <paramref name="stream"/> (seekable, positioned at its start). Throws <see cref="InvalidDataException"/> or <see cref="NotSupportedException"/> (also for MP4 files without ALAC).</summary>
    public static AlacDecoder Open(Stream stream)
    {
        if (!stream.CanSeek) throw new NotSupportedException("ALAC decoding needs a seekable stream.");
        Mp4AlacTrack track = Mp4AlacTrack.Read(stream);
        return new AlacDecoder(stream, track);
    }

    public int Read(Span<float> destination, int frames)
    {
        int channels = Channels;
        int produced = 0;
        while (produced < frames)
        {
            if (blockPosition >= blockLength && !LoadNextPacket()) break;
            int take = Math.Min(frames - produced, blockLength - blockPosition);
            block.AsSpan(blockPosition * channels, take * channels).CopyTo(destination[(produced * channels)..]);
            blockPosition += take;
            produced += take;
        }
        return produced;
    }

    public bool Seek(long frame)
    {
        if (frame < 0 || frame > track.TotalFrames) return false;
        blockPosition = blockLength = 0;
        if (frame == track.TotalFrames)
        {
            nextPacket = track.PacketCount;
            return true;
        }
        int index = Array.BinarySearch(track.StartFrames, 0, track.PacketCount, frame);
        if (index < 0) index = ~index - 1;
        // Packets with no frames (equal start frames) cannot hold the target: take the last one that starts at it.
        while (index + 1 < track.PacketCount && track.StartFrames[index + 1] <= frame) index++;
        nextPacket = index;
        if (!LoadNextPacket()) return false;
        blockPosition = (int)Math.Min(frame - track.StartFrames[index], blockLength);
        return true;
    }

    public void Dispose() => stream.Dispose();

    private bool LoadNextPacket()
    {
        while (nextPacket < track.PacketCount)
        {
            int index = nextPacket++;
            int expected = (int)Math.Min(track.StartFrames[index + 1] - track.StartFrames[index], decoder.Config.FrameLength);
            int size = track.Sizes[index];
            try
            {
                if (stream.Position != track.Offsets[index]) stream.Seek(track.Offsets[index], SeekOrigin.Begin);
                stream.ReadExactly(packet.AsSpan(0, size));
                blockLength = Math.Min(decoder.Decode(packet.AsSpan(0, size), block), expected);
                failures = 0;
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
            {
                if (++failures >= MaxConsecutiveFailures) throw new InvalidDataException("The ALAC stream is damaged.", exception);
                Array.Clear(block, 0, expected * Channels);
                blockLength = expected;
            }
            blockPosition = 0;
            if (blockLength > 0) return true;
        }
        return false;
    }
}
