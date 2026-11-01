// This file is derived from the Opus reference implementation published in RFC 6716 (src/opus_decoder.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
namespace Aibell.Audio.Opus;

/// <summary>
/// Decodes Opus packets (RFC 6716) to 48 kHz interleaved float. SILK, CELT and hybrid frames, 5 ms redundancy frames, mode transitions, DTX and packet loss
/// concealment are supported. One instance serves one stream and is not thread safe.
/// </summary>
internal sealed class OpusDecoder
{
    public const int SampleRate = 48000;
    public const int MaxPacketFrames = 5760;
    private const int ModeNone = 0;
    private const int ModeSilkOnly = 1000;
    private const int ModeHybrid = 1001;
    private const int ModeCeltOnly = 1002;

    public const int BandwidthNarrow = 0;
    public const int BandwidthMedium = 1;
    public const int BandwidthWide = 2;
    public const int BandwidthSuperWide = 3;
    public const int BandwidthFull = 4;

    private const int F20 = SampleRate / 50;
    private const int F10 = F20 / 2;
    private const int F5 = F10 / 2;
    private const int F2_5 = F5 / 2;

    private sealed class FrameBuffers(int channels)
    {
        public readonly short[] Silk = new short[MaxPacketFrames * channels];
        public readonly float[] Transition = new float[F5 * channels];
        public readonly float[] Redundant = new float[F5 * channels];
    }

    private readonly int channels;
    private readonly SilkDecoder silk = new();
    private readonly CeltDecoder celt;
    private readonly RangeDecoder dec = new();
    private readonly FrameBuffers[] buffers;
    private readonly int[] sizes = new int[48];
    private int mode;
    private int bandwidth;
    private int frameSize = SampleRate / 400;
    private int streamChannels;
    private int prevMode;
    private bool prevRedundancy;
    private int silkInternalChannels;
    private int silkInternalRate;
    private uint rangeFinal;

    public OpusDecoder(int channels)
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        this.channels = channels;
        streamChannels = channels;
        celt = new CeltDecoder(channels);
        buffers = [new FrameBuffers(channels), new FrameBuffers(channels)];
        silk.Reset();
    }

    public int Channels => channels;

    /// <summary>The final range coder state of the last decoded frame, used by the conformance tests.</summary>
    public uint FinalRange => rangeFinal;

    /// <summary>Number of samples per frame (48 kHz) announced by a TOC byte.</summary>
    public static int SamplesPerFrame(byte toc)
    {
        if ((toc & 0x80) != 0) return (SampleRate << ((toc >> 3) & 3)) / 400;
        if ((toc & 0x60) == 0x60) return (toc & 0x08) != 0 ? SampleRate / 50 : SampleRate / 100;
        int size = (toc >> 3) & 3;
        return size == 3 ? SampleRate * 60 / 1000 : (SampleRate << size) / 100;
    }

    public static int PacketBandwidth(byte toc)
    {
        if ((toc & 0x80) != 0)
        {
            int bw = BandwidthMedium + ((toc >> 5) & 3);
            return bw == BandwidthMedium ? BandwidthNarrow : bw;
        }
        if ((toc & 0x60) == 0x60) return (toc & 0x10) != 0 ? BandwidthFull : BandwidthSuperWide;
        return BandwidthNarrow + ((toc >> 5) & 3);
    }

    private static int PacketMode(byte toc) => (toc & 0x80) != 0 ? ModeCeltOnly : (toc & 0x60) == 0x60 ? ModeHybrid : ModeSilkOnly;

    /// <summary>Total number of samples per channel in a packet, or -1 when it is invalid.</summary>
    public static int PacketSamples(byte[] data, int offset, int len) => len < 1 ? -1 : PacketSamples(data.AsSpan(offset, len));

    /// <summary>Total number of samples per channel in a packet, or -1 when it is invalid.</summary>
    public static int PacketSamples(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 1) return -1;
        int perFrame = SamplesPerFrame(packet[0]);
        int count;
        switch (packet[0] & 3)
        {
            case 0:
                count = 1;
                break;
            case 1:
            case 2:
                count = 2;
                break;
            default:
                if (packet.Length < 2) return -1;
                count = packet[1] & 0x3F;
                break;
        }
        int total = count * perFrame;
        return count <= 0 || total > MaxPacketFrames ? -1 : total;
    }

    public void Reset()
    {
        silk.Reset();
        celt.Reset();
        mode = ModeNone;
        prevMode = ModeNone;
        prevRedundancy = false;
        frameSize = SampleRate / 400;
        streamChannels = channels;
        rangeFinal = 0;
        silkInternalChannels = 0;
        silkInternalRate = 0;
    }

    private static int ParseSize(byte[] data, int pos, int len, out int size)
    {
        if (len < 1)
        {
            size = -1;
            return -1;
        }
        if (data[pos] < 252)
        {
            size = data[pos];
            return 1;
        }
        if (len < 2)
        {
            size = -1;
            return -1;
        }
        size = 4 * data[pos + 1] + data[pos];
        return 2;
    }

    /// <summary>
    /// Splits a packet into frames. Returns the frame count, or -1 for an invalid packet. With <paramref name="selfDelimited"/> (RFC 6716 Appendix B) the
    /// packet carries the length of its last frame explicitly and <paramref name="packetEnd"/> is where it ends inside <paramref name="data"/>; otherwise the
    /// packet extends to the end of the data.
    /// </summary>
    private int ParsePacket(byte[] data, int offset, int len, bool selfDelimited, out int payloadOffset, out int packetEnd)
    {
        payloadOffset = offset;
        int limit = offset + len;
        packetEnd = limit;
        int framesize = SamplesPerFrame(data[offset]);
        int toc = data[offset];
        int pos = offset + 1;
        len--;
        int lastSize = len;
        int padding = 0;
        int count;
        switch (toc & 3)
        {
            case 0:
                count = 1;
                if (selfDelimited && !ReadExplicitSize(data, ref pos, ref len, out lastSize)) return -1;
                break;
            case 1:
                count = 2;
                if (selfDelimited)
                {
                    if (!ReadExplicitSize(data, ref pos, ref len, out lastSize) || 2 * lastSize > len) return -1;
                    sizes[0] = lastSize;
                }
                else
                {
                    if ((len & 1) != 0) return -1;
                    sizes[0] = lastSize = len / 2;
                }
                break;
            case 2:
                {
                    count = 2;
                    int bytes = ParseSize(data, pos, len, out sizes[0]);
                    len -= bytes;
                    if (sizes[0] < 0 || sizes[0] > len) return -1;
                    pos += bytes;
                    if (selfDelimited)
                    {
                        if (!ReadExplicitSize(data, ref pos, ref len, out lastSize) || sizes[0] + lastSize > len) return -1;
                    }
                    else lastSize = len - sizes[0];
                    break;
                }
            default:
                {
                    if (len < 1) return -1;
                    int ch = data[pos++];
                    count = ch & 0x3F;
                    if (count <= 0 || framesize * count > MaxPacketFrames) return -1;
                    len--;
                    if ((ch & 0x40) != 0)
                    {
                        int p;
                        do
                        {
                            if (len <= 0) return -1;
                            p = data[pos++];
                            len--;
                            int extra = p == 255 ? 254 : p;
                            len -= extra;
                            padding += extra;
                        }
                        while (p == 255);
                    }
                    if (len < 0) return -1;
                    bool cbr = (ch & 0x80) == 0;
                    if (!cbr)
                    {
                        lastSize = len;
                        for (int i = 0; i < count - 1; i++)
                        {
                            int bytes = ParseSize(data, pos, len, out sizes[i]);
                            len -= bytes;
                            if (sizes[i] < 0 || sizes[i] > len) return -1;
                            pos += bytes;
                            lastSize -= bytes + sizes[i];
                        }
                        if (lastSize < 0) return -1;
                        if (selfDelimited)
                        {
                            if (!ReadExplicitSize(data, ref pos, ref len, out lastSize)) return -1;
                            int others = 0;
                            for (int i = 0; i < count - 1; i++) others += sizes[i];
                            if (others + lastSize > len) return -1;
                        }
                    }
                    else if (selfDelimited)
                    {
                        if (!ReadExplicitSize(data, ref pos, ref len, out lastSize) || lastSize * count > len) return -1;
                        for (int i = 0; i < count - 1; i++) sizes[i] = lastSize;
                    }
                    else
                    {
                        lastSize = len / count;
                        if (lastSize * count != len) return -1;
                        for (int i = 0; i < count - 1; i++) sizes[i] = lastSize;
                    }
                    break;
                }
        }
        if (lastSize > 1275) return -1;
        sizes[count - 1] = lastSize;
        payloadOffset = pos;
        if (selfDelimited)
        {
            int frameBytes = 0;
            for (int i = 0; i < count; i++) frameBytes += sizes[i];
            packetEnd = pos + frameBytes + padding;
            if (packetEnd > limit) return -1;
        }
        return count;
    }

    private static bool ReadExplicitSize(byte[] data, ref int pos, ref int len, out int size)
    {
        int bytes = ParseSize(data, pos, len, out size);
        if (bytes < 0 || size < 0) return false;
        pos += bytes;
        len -= bytes;
        return size <= len;
    }

    /// <summary>
    /// Decodes one packet. <paramref name="data"/> null or empty conceals <paramref name="maxFrames"/> samples per channel instead. Returns the number of samples
    /// per channel written to <paramref name="pcm"/>, or a negative value for an invalid packet.
    /// </summary>
    public int Decode(byte[]? data, int offset, int len, float[] pcm, int pcmOffset, int maxFrames)
        => Decode(data, offset, len, false, out _, pcm, pcmOffset, maxFrames);

    /// <summary>
    /// Decodes one packet that is followed by more data (a stream of a multistream packet, RFC 6716 Appendix B): it carries the length of its last frame and
    /// <paramref name="consumed"/> is the number of bytes it takes.
    /// </summary>
    public int DecodeSelfDelimited(byte[] data, int offset, int len, out int consumed, float[] pcm, int pcmOffset, int maxFrames)
        => Decode(data, offset, len, true, out consumed, pcm, pcmOffset, maxFrames);

    private int Decode(byte[]? data, int offset, int len, bool selfDelimited, out int consumed, float[] pcm, int pcmOffset, int maxFrames)
    {
        consumed = 0;
        if (data is null || len == 0) return DecodeFrame(null, 0, 0, pcm, pcmOffset, maxFrames, false, 0);
        if (len < 0) return -1;

        byte toc = data[offset];
        mode = PacketMode(toc);
        bandwidth = PacketBandwidth(toc);
        frameSize = SamplesPerFrame(toc);
        streamChannels = (toc & 4) != 0 ? 2 : 1;
        int count = ParsePacket(data, offset, len, selfDelimited, out int pos, out int packetEnd);
        if (count < 0) return -1;
        if (count * frameSize > maxFrames) return -2;
        consumed = packetEnd - offset;

        int decoded = 0;
        for (int i = 0; i < count; i++)
        {
            int ret = DecodeFrame(data, pos, sizes[i], pcm, pcmOffset + decoded * channels, maxFrames - decoded, false, 0);
            if (ret < 0) return ret;
            pos += sizes[i];
            decoded += ret;
        }
        return decoded;
    }

    private static void SmoothFade(float[] in1, int in1Pos, float[] in2, int in2Pos, float[] output, int outPos, int overlap, int channels)
    {
        float[] window = CeltMode.Window;
        for (int c = 0; c < channels; c++)
        {
            for (int i = 0; i < overlap; i++)
            {
                float w = window[i] * window[i];
                output[outPos + i * channels + c] = w * in2[in2Pos + i * channels + c] + (1f - w) * in1[in1Pos + i * channels + c];
            }
        }
    }

    private int DecodeFrame(byte[]? data, int dataOffset, int len, float[] pcm, int pcmOffset, int frameSizeIn, bool decodeFec, int depth)
    {
        FrameBuffers buf = buffers[depth];
        int audioSize;
        int curMode;
        int frameSizeLocal = frameSizeIn;
        if (frameSizeLocal < F2_5) return -2;
        if (len <= 1)
        {
            data = null;
            frameSizeLocal = Math.Min(frameSizeLocal, frameSize);
        }
        if (data is not null)
        {
            audioSize = frameSize;
            curMode = mode;
            dec.Init(data, dataOffset, len);
        }
        else
        {
            audioSize = frameSizeLocal;
            if (prevMode == ModeNone)
            {
                Array.Clear(pcm, pcmOffset, audioSize * channels);
                return audioSize;
            }
            curMode = prevMode;
        }

        if (data is null && frameSizeLocal > F20 && curMode != ModeSilkOnly)
        {
            int done = 0;
            do
            {
                int ret = DecodeFrame(null, 0, 0, pcm, pcmOffset, F20, false, depth);
                if (ret != F20) return -3;
                pcmOffset += F20 * channels;
                done += F20;
            }
            while (done < frameSizeLocal);
            return frameSizeLocal;
        }

        bool transition = false;
        if (data is not null && prevMode > 0 &&
            ((curMode == ModeCeltOnly && prevMode != ModeCeltOnly && !prevRedundancy) || (curMode != ModeCeltOnly && prevMode == ModeCeltOnly)))
        {
            transition = true;
            if (curMode == ModeCeltOnly) DecodeFrame(null, 0, 0, buf.Transition, 0, Math.Min(F5, audioSize), false, depth + 1);
        }
        if (audioSize > frameSizeLocal) return -1;
        frameSizeLocal = audioSize;

        if (curMode != ModeCeltOnly)
        {
            if (prevMode == ModeCeltOnly) silk.Reset();
            int payloadMs = Math.Max(10, 1000 * audioSize / SampleRate);
            if (data is not null)
            {
                silkInternalChannels = streamChannels;
                silkInternalRate = curMode == ModeSilkOnly
                    ? bandwidth switch { BandwidthNarrow => 8000, BandwidthMedium => 12000, _ => 16000 }
                    : 16000;
            }
            int lostFlag = data is null ? 1 : 2 * (decodeFec ? 1 : 0);
            int decodedSamples = 0;
            int silkPos = 0;
            do
            {
                int n = silk.Decode(dec, lostFlag, decodedSamples == 0, payloadMs, silkInternalRate, silkInternalChannels, channels, SampleRate, buf.Silk.AsSpan(silkPos));
                silkPos += n * channels;
                decodedSamples += n;
            }
            while (decodedSamples < frameSizeLocal);
        }

        int startBand = 0;
        bool redundancy = false;
        bool celtToSilk = false;
        int redundancyBytes = 0;
        if (!decodeFec && curMode != ModeCeltOnly && data is not null && dec.Tell() + 17 + 20 * (mode == ModeHybrid ? 1 : 0) <= 8 * len)
        {
            redundancy = curMode == ModeHybrid ? dec.DecodeBitLogP(12) != 0 : true;
            if (redundancy)
            {
                celtToSilk = dec.DecodeBitLogP(1) != 0;
                redundancyBytes = curMode == ModeHybrid ? (int)dec.DecodeUint(256) + 2 : len - ((dec.Tell() + 7) >> 3);
                len -= redundancyBytes;
                if (len * 8 < dec.Tell())
                {
                    len = 0;
                    redundancyBytes = 0;
                    redundancy = false;
                }
                dec.Shrink(redundancyBytes);
            }
        }
        if (curMode != ModeCeltOnly) startBand = 17;

        celt.End = bandwidth switch { BandwidthNarrow => 13, BandwidthMedium or BandwidthWide => 17, BandwidthSuperWide => 19, _ => 21 };
        celt.StreamChannels = streamChannels;

        if (redundancy) transition = false;
        if (transition && curMode != ModeCeltOnly) DecodeFrame(null, 0, 0, buf.Transition, 0, Math.Min(F5, audioSize), false, depth + 1);

        uint redundantRng = 0;
        if (redundancy && celtToSilk)
        {
            celt.Start = 0;
            celt.Decode(data, dataOffset + len, redundancyBytes, null, buf.Redundant, 0, F5);
            redundantRng = celt.FinalRange;
        }

        celt.Start = startBand;
        int celtRet = 0;
        if (curMode != ModeSilkOnly)
        {
            int celtFrameSize = Math.Min(F20, frameSizeLocal);
            if (curMode != prevMode && prevMode > 0 && !prevRedundancy) celt.Reset();
            celtRet = celt.Decode(decodeFec ? null : data, dataOffset, len, data is null ? null : dec, pcm, pcmOffset, celtFrameSize);
        }
        else
        {
            Array.Clear(pcm, pcmOffset, frameSizeLocal * channels);
            if (prevMode == ModeHybrid && !(redundancy && celtToSilk && prevRedundancy))
            {
                celt.Start = 0;
                celt.Decode([0xFF, 0xFF], 0, 2, null, pcm, pcmOffset, F2_5);
            }
        }

        if (curMode != ModeCeltOnly)
        {
            int total = frameSizeLocal * channels;
            for (int i = 0; i < total; i++) pcm[pcmOffset + i] += (float)((1.0 / 32768.0) * buf.Silk[i]);
        }

        if (redundancy && !celtToSilk)
        {
            celt.Reset();
            celt.Start = 0;
            celt.Decode(data, dataOffset + len, redundancyBytes, null, buf.Redundant, 0, F5);
            redundantRng = celt.FinalRange;
            int tail = pcmOffset + channels * (frameSizeLocal - F2_5);
            SmoothFade(pcm, tail, buf.Redundant, channels * F2_5, pcm, tail, F2_5, channels);
        }
        if (redundancy && celtToSilk)
        {
            for (int c = 0; c < channels; c++)
            {
                for (int i = 0; i < F2_5; i++) pcm[pcmOffset + channels * i + c] = buf.Redundant[channels * i + c];
            }
            SmoothFade(buf.Redundant, channels * F2_5, pcm, pcmOffset + channels * F2_5, pcm, pcmOffset + channels * F2_5, F2_5, channels);
        }
        if (transition)
        {
            if (audioSize >= F5)
            {
                Array.Copy(buf.Transition, 0, pcm, pcmOffset, channels * F2_5);
                SmoothFade(buf.Transition, channels * F2_5, pcm, pcmOffset + channels * F2_5, pcm, pcmOffset + channels * F2_5, F2_5, channels);
            }
            else SmoothFade(buf.Transition, 0, pcm, pcmOffset, pcm, pcmOffset, F2_5, channels);
        }

        rangeFinal = len <= 1 ? 0 : dec.Range ^ redundantRng;
        prevMode = curMode;
        prevRedundancy = redundancy && !celtToSilk;
        return celtRet < 0 ? celtRet : audioSize;
    }
}
