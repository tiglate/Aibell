// This file is derived from the Opus reference implementation published in RFC 6716 (silk/*.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
using static Aibell.Audio.Opus.SilkMath;

namespace Aibell.Audio.Opus;

/// <summary>The SILK layer of an Opus stream: up to two channels decoded to int16 and resampled to the API rate (silk_Decode).</summary>
internal sealed class SilkDecoder
{
    public const int FlagDecodeNormal = SilkChannel.FlagDecodeNormal;
    public const int FlagPacketLost = SilkChannel.FlagPacketLost;
    public const int FlagDecodeLbrr = SilkChannel.FlagDecodeLbrr;

    private readonly SilkChannel[] channels = [new SilkChannel(), new SilkChannel()];
    private readonly int[] predPrevQ13 = new int[2];
    private readonly short[] sMid = new short[2];
    private readonly short[] sSide = new short[2];
    private readonly short[][] tmp = [new short[16 * 20 + 2], new short[16 * 20 + 2]];
    private readonly short[] resampled = new short[48 * 20];
    private readonly int[] msPredQ13 = new int[2];
    private int nChannelsApi;
    private int nChannelsInternal;
    private int prevDecodeOnlyMiddle;

    /// <summary>The pitch lag of the last voiced frame at 48 kHz, or 0.</summary>
    public int PrevPitchLag { get; private set; }

    public void Reset()
    {
        foreach (SilkChannel c in channels) c.Reset();
        Array.Clear(predPrevQ13);
        Array.Clear(sMid);
        Array.Clear(sSide);
        nChannelsApi = 0;
        nChannelsInternal = 0;
        prevDecodeOnlyMiddle = 0;
        PrevPitchLag = 0;
    }

    private static void StereoDecodePred(RangeDecoder dec, Span<int> predQ13)
    {
        Span<int> ix0 = stackalloc int[3], ix1 = stackalloc int[3];
        int n = dec.DecodeIcdf(SilkTables.StereoPredJoint, 8);
        ix0[2] = n / 5;
        ix1[2] = n - 5 * ix0[2];
        ix0[0] = dec.DecodeIcdf(SilkTables.Uniform3, 8);
        ix0[1] = dec.DecodeIcdf(SilkTables.Uniform5, 8);
        ix1[0] = dec.DecodeIcdf(SilkTables.Uniform3, 8);
        ix1[1] = dec.DecodeIcdf(SilkTables.Uniform5, 8);
        for (n = 0; n < 2; n++)
        {
            Span<int> ix = n == 0 ? ix0 : ix1;
            ix[0] += 3 * ix[2];
            int lowQ13 = SilkTables.StereoPredQuantQ13[ix[0]];
            int stepQ13 = Smulwb(SilkTables.StereoPredQuantQ13[ix[0] + 1] - lowQ13, (int)(0.5 / 5 * 65536 + 0.5));
            predQ13[n] = Smlabb(lowQ13, stepQ13, 2 * ix[1] + 1);
        }
        predQ13[0] -= predQ13[1];
    }

    private void StereoMsToLr(short[] x1, short[] x2, ReadOnlySpan<int> predQ13, int fsKHz, int frameLength)
    {
        x1[0] = sMid[0];
        x1[1] = sMid[1];
        x2[0] = sSide[0];
        x2[1] = sSide[1];
        sMid[0] = x1[frameLength];
        sMid[1] = x1[frameLength + 1];
        sSide[0] = x2[frameLength];
        sSide[1] = x2[frameLength + 1];

        int pred0 = predPrevQ13[0], pred1 = predPrevQ13[1];
        int denomQ16 = (1 << 16) / (8 * fsKHz);
        int delta0 = RShiftRound(Smulbb(predQ13[0] - predPrevQ13[0], denomQ16), 16);
        int delta1 = RShiftRound(Smulbb(predQ13[1] - predPrevQ13[1], denomQ16), 16);
        int interp = 8 * fsKHz;
        for (int n = 0; n < interp; n++)
        {
            pred0 += delta0;
            pred1 += delta1;
            int sum = (x1[n] + x1[n + 2] + (x1[n + 1] << 1)) << 9;
            sum = Smlawb(x2[n + 1] << 8, sum, pred0);
            sum = Smlawb(sum, x1[n + 1] << 11, pred1);
            x2[n + 1] = (short)Sat16(RShiftRound(sum, 8));
        }
        pred0 = predQ13[0];
        pred1 = predQ13[1];
        for (int n = interp; n < frameLength; n++)
        {
            int sum = (x1[n] + x1[n + 2] + (x1[n + 1] << 1)) << 9;
            sum = Smlawb(x2[n + 1] << 8, sum, pred0);
            sum = Smlawb(sum, x1[n + 1] << 11, pred1);
            x2[n + 1] = (short)Sat16(RShiftRound(sum, 8));
        }
        predPrevQ13[0] = predQ13[0];
        predPrevQ13[1] = predQ13[1];
        for (int n = 0; n < frameLength; n++)
        {
            int sum = x1[n + 1] + x2[n + 1];
            int diff = x1[n + 1] - x2[n + 1];
            x1[n + 1] = (short)Sat16(sum);
            x2[n + 1] = (short)Sat16(diff);
        }
    }

    /// <summary>
    /// Decodes one frame of every internal channel. <paramref name="samplesOut"/> receives interleaved samples at <paramref name="apiRate"/>;
    /// the result is the number of samples per channel.
    /// </summary>
    public int Decode(RangeDecoder dec, int lostFlag, bool newPacket, int payloadMs, int internalRate, int internalChannels, int apiChannels, int apiRate, Span<short> samplesOut)
    {
        SilkChannel c0 = channels[0];
        Span<int> msPred = msPredQ13;
        msPred.Clear();
        int decodeOnlyMiddle = 0;

        if (newPacket)
        {
            for (int n = 0; n < internalChannels; n++) channels[n].NFramesDecoded = 0;
        }
        if (internalChannels > nChannelsInternal) channels[1].Reset();
        bool stereoToMono = internalChannels == 1 && nChannelsInternal == 2 && internalRate == 1000 * c0.FsKHz;

        if (c0.NFramesDecoded == 0)
        {
            for (int n = 0; n < internalChannels; n++)
            {
                SilkChannel c = channels[n];
                switch (payloadMs)
                {
                    case 0:
                    case 10:
                        c.NFramesPerPacket = 1;
                        c.NbSubfr = 2;
                        break;
                    case 20:
                        c.NFramesPerPacket = 1;
                        c.NbSubfr = 4;
                        break;
                    case 40:
                        c.NFramesPerPacket = 2;
                        c.NbSubfr = 4;
                        break;
                    case 60:
                        c.NFramesPerPacket = 3;
                        c.NbSubfr = 4;
                        break;
                    default:
                        throw new InvalidDataException("Invalid SILK frame size.");
                }
                int fsKHz = (internalRate >> 10) + 1;
                if (fsKHz is not (8 or 12 or 16)) throw new InvalidDataException("Invalid SILK sampling rate.");
                c.SetFs(fsKHz, apiRate);
            }
        }

        if (apiChannels == 2 && internalChannels == 2 && (nChannelsApi == 1 || nChannelsInternal == 1))
        {
            Array.Clear(predPrevQ13);
            Array.Clear(sSide);
            channels[1].Resampler.CopyStateFrom(channels[0].Resampler);
        }
        nChannelsApi = apiChannels;
        nChannelsInternal = internalChannels;

        if (lostFlag != FlagPacketLost && c0.NFramesDecoded == 0)
        {
            for (int n = 0; n < internalChannels; n++)
            {
                SilkChannel c = channels[n];
                for (int i = 0; i < c.NFramesPerPacket; i++) c.VadFlags[i] = dec.DecodeBitLogP(1);
                c.LbrrFlag = dec.DecodeBitLogP(1);
            }
            for (int n = 0; n < internalChannels; n++)
            {
                SilkChannel c = channels[n];
                Array.Clear(c.LbrrFlags);
                if (c.LbrrFlag == 0) continue;
                if (c.NFramesPerPacket == 1) c.LbrrFlags[0] = 1;
                else
                {
                    int symbol = dec.DecodeIcdf(c.NFramesPerPacket == 2 ? SilkTables.LbrrFlags2 : SilkTables.LbrrFlags3, 8) + 1;
                    for (int i = 0; i < c.NFramesPerPacket; i++) c.LbrrFlags[i] = (symbol >> i) & 1;
                }
            }
            if (lostFlag == FlagDecodeNormal)
            {
                int[] skipped = new int[SilkChannel.MaxFrameLength];
                for (int i = 0; i < c0.NFramesPerPacket; i++)
                {
                    for (int n = 0; n < internalChannels; n++)
                    {
                        SilkChannel c = channels[n];
                        if (c.LbrrFlags[i] == 0) continue;
                        if (internalChannels == 2 && n == 0)
                        {
                            StereoDecodePred(dec, msPred);
                            if (channels[1].LbrrFlags[i] == 0) decodeOnlyMiddle = dec.DecodeIcdf(SilkTables.StereoOnlyMid, 8);
                        }
                        int cond = i > 0 && c.LbrrFlags[i - 1] != 0 ? SilkChannel.CodeConditionally : SilkChannel.CodeIndependently;
                        c.DecodeIndices(dec, i, true, cond);
                        SilkChannel.DecodePulses(dec, skipped, c.Indices.SignalType, c.Indices.QuantOffsetType, c.FrameLength);
                    }
                }
            }
        }

        if (internalChannels == 2)
        {
            if (lostFlag == FlagDecodeNormal || (lostFlag == FlagDecodeLbrr && c0.LbrrFlags[c0.NFramesDecoded] == 1))
            {
                StereoDecodePred(dec, msPred);
                if ((lostFlag == FlagDecodeNormal && channels[1].VadFlags[c0.NFramesDecoded] == 0) ||
                    (lostFlag == FlagDecodeLbrr && channels[1].LbrrFlags[c0.NFramesDecoded] == 0))
                {
                    decodeOnlyMiddle = dec.DecodeIcdf(SilkTables.StereoOnlyMid, 8);
                }
                else decodeOnlyMiddle = 0;
            }
            else
            {
                msPred[0] = predPrevQ13[0];
                msPred[1] = predPrevQ13[1];
            }
        }

        if (internalChannels == 2 && decodeOnlyMiddle == 0 && prevDecodeOnlyMiddle == 1)
        {
            SilkChannel s = channels[1];
            Array.Clear(s.OutBuf);
            Array.Clear(s.SLpcQ14Buf);
            s.LagPrev = 100;
            s.LastGainIndex = 10;
            s.PrevSignalType = 0;
            s.FirstFrameAfterReset = true;
        }

        bool hasSide;
        if (lostFlag == FlagDecodeNormal) hasSide = decodeOnlyMiddle == 0;
        else hasSide = prevDecodeOnlyMiddle == 0 ||
            (internalChannels == 2 && lostFlag == FlagDecodeLbrr && channels[1].LbrrFlags[channels[1].NFramesDecoded] == 1);

        int decoded = 0;
        for (int n = 0; n < internalChannels; n++)
        {
            SilkChannel c = channels[n];
            if (n == 0 || hasSide)
            {
                int frameIndex = c0.NFramesDecoded - n;
                int cond;
                if (frameIndex <= 0) cond = SilkChannel.CodeIndependently;
                else if (lostFlag == FlagDecodeLbrr) cond = c.LbrrFlags[frameIndex - 1] != 0 ? SilkChannel.CodeConditionally : SilkChannel.CodeIndependently;
                else if (n > 0 && prevDecodeOnlyMiddle == 1) cond = SilkChannel.CodeIndependentlyNoLtpScaling;
                else cond = SilkChannel.CodeConditionally;
                decoded = c.DecodeFrame(dec, tmp[n].AsSpan(2), lostFlag, cond);
            }
            else tmp[n].AsSpan(2, decoded).Clear();
            c.NFramesDecoded++;
        }

        if (apiChannels == 2 && internalChannels == 2) StereoMsToLr(tmp[0], tmp[1], msPred, c0.FsKHz, decoded);
        else
        {
            tmp[0][0] = sMid[0];
            tmp[0][1] = sMid[1];
            sMid[0] = tmp[0][decoded];
            sMid[1] = tmp[0][decoded + 1];
        }

        int nSamplesOut = decoded * apiRate / (c0.FsKHz * 1000);
        Span<short> resampleOut = apiChannels == 2 ? resampled : samplesOut;
        int limit = Math.Min(apiChannels, internalChannels);
        for (int n = 0; n < limit; n++)
        {
            channels[n].Resampler.Process(resampleOut, tmp[n].AsSpan(1), decoded);
            if (apiChannels == 2)
            {
                for (int i = 0; i < nSamplesOut; i++) samplesOut[n + 2 * i] = resampled[i];
            }
        }
        if (apiChannels == 2 && internalChannels == 1)
        {
            if (stereoToMono)
            {
                channels[1].Resampler.Process(resampled, tmp[0].AsSpan(1), decoded);
                for (int i = 0; i < nSamplesOut; i++) samplesOut[1 + 2 * i] = resampled[i];
            }
            else
            {
                for (int i = 0; i < nSamplesOut; i++) samplesOut[1 + 2 * i] = samplesOut[2 * i];
            }
        }

        if (c0.PrevSignalType == 2) PrevPitchLag = c0.LagPrev * (((c0.FsKHz - 8) >> 2) switch { 0 => 6, 1 => 4, _ => 3 });
        else PrevPitchLag = 0;

        if (lostFlag == FlagPacketLost)
        {
            for (int i = 0; i < nChannelsInternal; i++) channels[i].LastGainIndex = 10;
        }
        else prevDecodeOnlyMiddle = decodeOnlyMiddle;
        return nSamplesOut;
    }
}
