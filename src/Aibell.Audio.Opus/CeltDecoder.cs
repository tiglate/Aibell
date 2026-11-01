// This file is derived from the Opus reference implementation published in RFC 6716 (celt/celt.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
namespace Aibell.Audio.Opus;

/// <summary>
/// The CELT layer of an Opus decoder at 48 kHz: energy envelope, bit allocation, PVQ shapes, inverse MDCT with overlap-add, the pitch post-filter and de-emphasis,
/// plus the packet-loss concealment. Output is float in +/-1. One instance serves one stream and is not thread safe.
/// </summary>
internal sealed class CeltDecoder
{
    private const int DecodeBufferSize = 2048;
    private const int MaxPeriod = 1024;
    private const int CombMinPeriod = 15;
    private const int Bands = CeltMode.BandCount;
    private const int Overlap = CeltMode.Overlap;
    private static readonly short[] Edges = CeltMode.BandEdges;

    private readonly int channels;
    private readonly float[][] decodeMem;
    private readonly float[] lpc;
    private readonly float[] oldBandE = new float[2 * Bands];
    private readonly float[] oldLogE = new float[2 * Bands];
    private readonly float[] oldLogE2 = new float[2 * Bands];
    private readonly float[] backgroundLogE = new float[2 * Bands];
    private readonly float[] preemphMem = new float[2];
    private readonly CeltMdct mdct = new();
    private readonly CeltBands bandDecoder = new();
    private readonly RangeDecoder ownDecoder = new();
    private readonly float[] freq;
    private readonly float[] shape;
    private readonly float[] bandE = new float[2 * Bands];
    private readonly int[] tfRes = new int[Bands];
    private readonly int[] pulses = new int[Bands];
    private readonly int[] cap = new int[Bands];
    private readonly int[] offsets = new int[Bands];
    private readonly int[] finePriority = new int[Bands];
    private readonly int[] fineQuant = new int[Bands];
    private readonly byte[] collapseMasks = new byte[2 * Bands];
    private readonly float[] mdctScratch = new float[CeltMode.Overlap + (CeltMode.ShortMdctSize << CeltMode.MaxLm)];
    private uint rng;
    private int lastPitchIndex;
    private int lossCount;
    private int postfilterPeriod;
    private int postfilterPeriodOld;
    private float postfilterGain;
    private float postfilterGainOld;
    private int postfilterTapset;
    private int postfilterTapsetOld;

    public CeltDecoder(int channels)
    {
        this.channels = channels;
        StreamChannels = channels;
        decodeMem = new float[channels][];
        for (int c = 0; c < channels; c++) decodeMem[c] = new float[DecodeBufferSize + Overlap];
        lpc = new float[channels * CeltPitch.LpcOrder];
        freq = new float[2 * (CeltMode.ShortMdctSize << CeltMode.MaxLm)];
        shape = new float[2 * (CeltMode.ShortMdctSize << CeltMode.MaxLm)];
        End = Bands;
        Reset();
    }

    /// <summary>First coded band (0, or 17 in hybrid frames).</summary>
    public int Start { get; set; }

    /// <summary>One past the last coded band, from the audio bandwidth.</summary>
    public int End { get; set; }

    /// <summary>1 or 2: how many channels the frame carries (the decoder itself may output more).</summary>
    public int StreamChannels { get; set; }

    public uint FinalRange => rng;

    public void Reset()
    {
        foreach (float[] mem in decodeMem) Array.Clear(mem);
        Array.Clear(lpc);
        Array.Clear(oldBandE);
        Array.Clear(backgroundLogE);
        Array.Clear(preemphMem);
        Array.Fill(oldLogE, -28f);
        Array.Fill(oldLogE2, -28f);
        rng = 0;
        lastPitchIndex = 0;
        lossCount = 0;
        postfilterPeriod = postfilterPeriodOld = 0;
        postfilterGain = postfilterGainOld = 0;
        postfilterTapset = postfilterTapsetOld = 0;
    }

    private static void CombFilter(float[] buffer, int yOffset, int xOffset, int t0, int t1, int n, float g0, float g1, int tapset0, int tapset1, ReadOnlySpan<float> window, int overlap)
    {
        ReadOnlySpan<float> gains = CeltTables.CombGains;
        float g00 = g0 * gains[tapset0 * 3], g01 = g0 * gains[tapset0 * 3 + 1], g02 = g0 * gains[tapset0 * 3 + 2];
        float g10 = g1 * gains[tapset1 * 3], g11 = g1 * gains[tapset1 * 3 + 1], g12 = g1 * gains[tapset1 * 3 + 2];
        float[] x = buffer;
        int i = 0;
        for (; i < overlap; i++)
        {
            float f = window[i] * window[i];
            int xi = xOffset + i;
            buffer[yOffset + i] = x[xi]
                + (1f - f) * g00 * x[xi - t0]
                + (1f - f) * g01 * x[xi - t0 - 1]
                + (1f - f) * g01 * x[xi - t0 + 1]
                + (1f - f) * g02 * x[xi - t0 - 2]
                + (1f - f) * g02 * x[xi - t0 + 2]
                + f * g10 * x[xi - t1]
                + f * g11 * x[xi - t1 - 1]
                + f * g11 * x[xi - t1 + 1]
                + f * g12 * x[xi - t1 - 2]
                + f * g12 * x[xi - t1 + 2];
        }
        for (; i < n; i++)
        {
            int xi = xOffset + i;
            buffer[yOffset + i] = x[xi] + g10 * x[xi - t1] + g11 * x[xi - t1 - 1] + g11 * x[xi - t1 + 1] + g12 * x[xi - t1 - 2] + g12 * x[xi - t1 + 2];
        }
    }

    private void Deemphasis(int outSynOffset, float[] pcm, int pcmOffset, int n)
    {
        for (int c = 0; c < channels; c++)
        {
            float m = preemphMem[c];
            float[] x = decodeMem[c];
            int y = pcmOffset + c;
            for (int j = 0; j < n; j++)
            {
                float tmp = x[outSynOffset + j] + m;
                m = .8500061035f * tmp - 0f * x[outSynOffset + j];
                pcm[y] = tmp * (1f / 32768f);
                y += channels;
            }
            preemphMem[c] = m;
        }
    }

    // Inverse MDCT of all channels and blocks into the decode buffer, with the overlap added to what the previous frame left.
    private void ComputeInverseMdcts(bool shortBlocks, int lm)
    {
        int n = CeltMode.ShortMdctSize << lm;
        int outSyn = DecodeBufferSize - n;
        for (int c = 0; c < channels; c++)
        {
            int n2 = n;
            int blocks = 1;
            if (shortBlocks)
            {
                n2 = CeltMode.ShortMdctSize;
                blocks = 1 << lm;
            }
            Array.Clear(mdctScratch, 0, Overlap);
            for (int b = 0; b < blocks; b++)
                mdct.Backward(freq, b + c * n2 * blocks, blocks, mdctScratch, n2 * b, shortBlocks ? CeltMode.MaxLm : CeltMode.MaxLm - lm, CeltMode.Window, Overlap);
            float[] mem = decodeMem[c];
            int j = 0;
            for (; j < Overlap; j++) mem[outSyn + j] = mdctScratch[j] + mem[DecodeBufferSize + j];
            for (; j < n; j++) mem[outSyn + j] = mdctScratch[j];
            for (j = 0; j < Overlap; j++) mem[DecodeBufferSize + j] = mdctScratch[n + j];
        }
    }

    /// <summary>
    /// Decodes one CELT frame of <paramref name="frameSize"/> samples (120, 240, 480 or 960) per channel into <paramref name="pcm"/>. With <paramref name="data"/> null or a payload of
    /// at most one byte the frame is concealed. <paramref name="shared"/> is the range decoder already positioned inside a hybrid frame; otherwise a new one is started on the data.
    /// </summary>
    public int Decode(byte[]? data, int dataOffset, int len, RangeDecoder? shared, float[] pcm, int pcmOffset, int frameSize)
    {
        int cc = channels;
        int c2 = StreamChannels;
        int lm;
        for (lm = 0; lm <= CeltMode.MaxLm; lm++)
        {
            if (CeltMode.ShortMdctSize << lm == frameSize) break;
        }
        if (lm > CeltMode.MaxLm || len < 0 || len > 1275) return -1;
        int m = 1 << lm;
        int n = m * CeltMode.ShortMdctSize;
        int effEnd = Math.Min(End, Bands);
        Array.Clear(shape, 0, c2 * n);

        if (data is null || len <= 1)
        {
            DecodeLost(pcm, pcmOffset, n, lm);
            return frameSize;
        }

        RangeDecoder dec = shared ?? ownDecoder;
        if (shared is null) ownDecoder.Init(data, dataOffset, len);
        bandDecoder.Attach(dec);

        if (c2 == 1)
        {
            for (int i = 0; i < Bands; i++) oldBandE[i] = Math.Max(oldBandE[i], oldBandE[Bands + i]);
        }
        int totalBits = len * 8;
        int tell = dec.Tell();
        bool silence;
        if (tell >= totalBits) silence = true;
        else if (tell == 1) silence = dec.DecodeBitLogP(15) != 0;
        else silence = false;
        if (silence)
        {
            tell = len * 8;
            dec.SkipBits(tell - dec.Tell());
        }
        float newPostfilterGain = 0;
        int newPostfilterPitch = 0;
        int newPostfilterTapset = 0;
        if (Start == 0 && tell + 16 <= totalBits)
        {
            if (dec.DecodeBitLogP(1) != 0)
            {
                int octave = (int)dec.DecodeUint(6);
                newPostfilterPitch = (16 << octave) + (int)dec.DecodeBits(4 + octave) - 1;
                int qg = (int)dec.DecodeBits(3);
                if (dec.Tell() + 2 <= totalBits) newPostfilterTapset = dec.DecodeIcdf(CeltTables.TapsetIcdf, 2);
                newPostfilterGain = .09375f * (qg + 1);
            }
            tell = dec.Tell();
        }
        bool isTransient = false;
        if (lm > 0 && tell + 3 <= totalBits)
        {
            isTransient = dec.DecodeBitLogP(3) != 0;
            tell = dec.Tell();
        }
        bool shortBlocks = isTransient;
        bool intraEnergy = tell + 3 <= totalBits && dec.DecodeBitLogP(3) != 0;
        CeltEnergy.UnquantCoarse(Start, End, oldBandE, intraEnergy, dec, c2, lm);
        DecodeTf(Start, End, isTransient, lm, dec);
        tell = dec.Tell();
        int spreadDecision = CeltPvq.SpreadNormal;
        if (tell + 4 <= totalBits) spreadDecision = dec.DecodeIcdf(CeltTables.SpreadIcdf, 5);
        CeltRate.InitCaps(cap, lm, c2);
        int dynallocLogp = 6;
        totalBits <<= CeltRate.BitRes;
        tell = dec.TellFrac();
        for (int i = Start; i < End; i++)
        {
            int width = c2 * (Edges[i + 1] - Edges[i]) << lm;
            int quanta = Math.Min(width << CeltRate.BitRes, Math.Max(6 << CeltRate.BitRes, width));
            int loopLogp = dynallocLogp;
            int boost = 0;
            while (tell + (loopLogp << CeltRate.BitRes) < totalBits && boost < cap[i])
            {
                int flag = dec.DecodeBitLogP(loopLogp);
                tell = dec.TellFrac();
                if (flag == 0) break;
                boost += quanta;
                totalBits -= quanta;
                loopLogp = 1;
            }
            offsets[i] = boost;
            if (boost > 0) dynallocLogp = Math.Max(2, dynallocLogp - 1);
        }
        int allocTrim = tell + (6 << CeltRate.BitRes) <= totalBits ? dec.DecodeIcdf(CeltTables.TrimIcdf, 7) : 5;
        int bits = (len * 8 << CeltRate.BitRes) - dec.TellFrac() - 1;
        int antiCollapseRsv = isTransient && lm >= 2 && bits >= (lm + 2) << CeltRate.BitRes ? 1 << CeltRate.BitRes : 0;
        bits -= antiCollapseRsv;
        int intensity = 0;
        bool dualStereo = false;
        int codedBands = CeltRate.ComputeAllocation(Start, End, offsets, cap, allocTrim, ref intensity, ref dualStereo, bits, out int balance, pulses, fineQuant, finePriority, c2, lm, dec);
        CeltEnergy.UnquantFine(Start, End, oldBandE, fineQuant, dec, c2);
        Array.Clear(collapseMasks);
        bandDecoder.DecodeAll(Start, End, shape, c2, n, collapseMasks, pulses, shortBlocks, spreadDecision, dualStereo, intensity, tfRes,
            len * (8 << CeltRate.BitRes) - antiCollapseRsv, balance, lm, codedBands, ref rng);
        bool antiCollapseOn = false;
        if (antiCollapseRsv > 0) antiCollapseOn = dec.DecodeBits(1) != 0;
        CeltEnergy.UnquantFinalise(Start, End, oldBandE, fineQuant, finePriority, len * 8 - dec.Tell(), dec, c2);
        if (antiCollapseOn) CeltBands.AntiCollapse(shape, collapseMasks, lm, c2, n, Start, End, oldBandE, oldLogE, oldLogE2, pulses, rng);
        CeltEnergy.Log2Amp(Start, End, bandE, oldBandE, c2);
        if (silence)
        {
            for (int i = 0; i < c2 * Bands; i++)
            {
                bandE[i] = 0;
                oldBandE[i] = -28f;
            }
        }

        // Synthesis.
        for (int c = 0; c < c2; c++) CeltBands.Denormalise(shape, c * n, freq, c * n, bandE, c * Bands, effEnd, m, n);
        foreach (float[] mem in decodeMem) Array.Copy(mem, n, mem, 0, DecodeBufferSize - n);
        for (int c = 0; c < c2; c++)
        {
            for (int i = 0; i < m * Edges[Start]; i++) freq[c * n + i] = 0;
            for (int i = m * Edges[effEnd]; i < n; i++) freq[c * n + i] = 0;
        }
        if (cc == 2 && c2 == 1)
        {
            for (int i = 0; i < n; i++) freq[n + i] = freq[i];
        }
        else if (cc == 1 && c2 == 2)
        {
            for (int i = 0; i < n; i++) freq[i] = .5f * (freq[i] + freq[n + i]);
        }
        ComputeInverseMdcts(shortBlocks, lm);
        int outSyn = DecodeBufferSize - n;
        for (int c = 0; c < cc; c++)
        {
            postfilterPeriod = Math.Max(postfilterPeriod, CombMinPeriod);
            postfilterPeriodOld = Math.Max(postfilterPeriodOld, CombMinPeriod);
            CombFilter(decodeMem[c], outSyn, outSyn, postfilterPeriodOld, postfilterPeriod, CeltMode.ShortMdctSize, postfilterGainOld, postfilterGain, postfilterTapsetOld, postfilterTapset,
                CeltMode.Window, Overlap);
            if (lm != 0)
            {
                CombFilter(decodeMem[c], outSyn + CeltMode.ShortMdctSize, outSyn + CeltMode.ShortMdctSize, postfilterPeriod, newPostfilterPitch, n - CeltMode.ShortMdctSize,
                    postfilterGain, newPostfilterGain, postfilterTapset, newPostfilterTapset, CeltMode.Window, Overlap);
            }
        }
        postfilterPeriodOld = postfilterPeriod;
        postfilterGainOld = postfilterGain;
        postfilterTapsetOld = postfilterTapset;
        postfilterPeriod = newPostfilterPitch;
        postfilterGain = newPostfilterGain;
        postfilterTapset = newPostfilterTapset;
        if (lm != 0)
        {
            postfilterPeriodOld = postfilterPeriod;
            postfilterGainOld = postfilterGain;
            postfilterTapsetOld = postfilterTapset;
        }
        if (c2 == 1)
        {
            for (int i = 0; i < Bands; i++) oldBandE[Bands + i] = oldBandE[i];
        }
        if (!isTransient)
        {
            Array.Copy(oldLogE, oldLogE2, 2 * Bands);
            Array.Copy(oldBandE, oldLogE, 2 * Bands);
            for (int i = 0; i < 2 * Bands; i++) backgroundLogE[i] = Math.Min(backgroundLogE[i] + m * 0.001f, oldBandE[i]);
        }
        else
        {
            for (int i = 0; i < 2 * Bands; i++) oldLogE[i] = Math.Min(oldLogE[i], oldBandE[i]);
        }
        for (int c = 0; c < 2; c++)
        {
            for (int i = 0; i < Start; i++)
            {
                oldBandE[c * Bands + i] = 0;
                oldLogE[c * Bands + i] = oldLogE2[c * Bands + i] = -28f;
            }
            for (int i = End; i < Bands; i++)
            {
                oldBandE[c * Bands + i] = 0;
                oldLogE[c * Bands + i] = oldLogE2[c * Bands + i] = -28f;
            }
        }
        rng = dec.Range;
        Deemphasis(outSyn, pcm, pcmOffset, n);
        lossCount = 0;
        if (dec.Tell() > 8 * len) return -3;
        return frameSize;
    }

    private void DecodeTf(int start, int end, bool isTransient, int lm, RangeDecoder dec)
    {
        int budget = dec.Storage * 8;
        int tell = dec.Tell();
        int logp = isTransient ? 2 : 4;
        bool selectReserved = lm > 0 && tell + logp + 1 <= budget;
        if (selectReserved) budget--;
        int curr = 0, changed = 0;
        for (int i = start; i < end; i++)
        {
            if (tell + logp <= budget)
            {
                curr ^= dec.DecodeBitLogP(logp);
                tell = dec.Tell();
                changed |= curr;
            }
            tfRes[i] = curr;
            logp = isTransient ? 4 : 5;
        }
        int select = 0;
        int row = lm * 8 + 4 * (isTransient ? 1 : 0);
        if (selectReserved && CeltTables.TfSelect[row + changed] != CeltTables.TfSelect[row + 2 + changed]) select = dec.DecodeBitLogP(1);
        for (int i = start; i < end; i++) tfRes[i] = CeltTables.TfSelect[row + 2 * select + tfRes[i]];
    }

    private void DecodeLost(float[] pcm, int pcmOffset, int n, int lm)
    {
        int c2 = channels;
        int outSyn = DecodeBufferSize - n;
        int len = n + Overlap;
        int outMem = DecodeBufferSize - MaxPeriod;
        if (lossCount >= 5 || Start != 0)
        {
            int effEnd = Math.Min(End, Bands);
            if (lossCount >= 5) CeltEnergy.Log2Amp(Start, End, bandE, backgroundLogE, c2);
            else
            {
                float decay = lossCount == 0 ? 1.5f : .5f;
                for (int c = 0; c < c2; c++)
                {
                    for (int i = Start; i < End; i++) oldBandE[c * Bands + i] -= decay;
                }
                CeltEnergy.Log2Amp(Start, End, bandE, oldBandE, c2);
            }
            uint seed = rng;
            for (int c = 0; c < c2; c++)
            {
                for (int i = 0; i < Edges[Start] << lm; i++) shape[c * n + i] = 0;
                for (int i = Start; i < Bands; i++)
                {
                    int boffs = n * c + (Edges[i] << lm);
                    int blen = (Edges[i + 1] - Edges[i]) << lm;
                    for (int j = 0; j < blen; j++)
                    {
                        seed = CeltMath.LcgRand(seed);
                        shape[boffs + j] = (int)seed >> 20;
                    }
                    CeltPvq.Renormalise(shape.AsSpan(boffs, blen), blen, 1f);
                }
                for (int i = Edges[End] << lm; i < n; i++) shape[c * n + i] = 0;
            }
            rng = seed;
            for (int c = 0; c < c2; c++) CeltBands.Denormalise(shape, c * n, freq, c * n, bandE, c * Bands, Bands, 1 << lm, n);
            for (int c = 0; c < c2; c++)
            {
                for (int i = 0; i < Edges[Start] << lm; i++) freq[c * n + i] = 0;
                for (int i = Edges[effEnd] << lm; i < n; i++) freq[c * n + i] = 0;
            }
            ComputeInverseMdcts(false, lm);
        }
        else
        {
            int pitchIndex;
            float fade = 1f;
            if (lossCount == 0)
            {
                float[] pitchBuf = new float[DecodeBufferSize >> 1];
                int poffset = 720;
                CeltPitch.Downsample(decodeMem, new int[2], pitchBuf, DecodeBufferSize, c2);
                pitchIndex = CeltPitch.Search(pitchBuf.AsSpan(poffset >> 1), pitchBuf, DecodeBufferSize - poffset, poffset - 100);
                pitchIndex = poffset - pitchIndex;
                lastPitchIndex = pitchIndex;
            }
            else
            {
                pitchIndex = lastPitchIndex;
                fade = .8f;
            }
            float[] exc = new float[MaxPeriod];
            float[] e = new float[MaxPeriod + 2 * Overlap];
            float[] ac = new float[CeltPitch.LpcOrder + 1];
            float[] mem = new float[CeltPitch.LpcOrder];
            for (int c = 0; c < c2; c++)
            {
                float[] om = decodeMem[c];
                float decay = 1f;
                float s1 = 0;
                Array.Clear(mem);
                int offset = MaxPeriod - pitchIndex;
                for (int i = 0; i < MaxPeriod; i++) exc[i] = om[outMem + i];
                Span<float> channelLpc = lpc.AsSpan(c * CeltPitch.LpcOrder, CeltPitch.LpcOrder);
                if (lossCount == 0)
                {
                    CeltPitch.Autocorr(exc, ac, CeltMode.Window, Overlap, CeltPitch.LpcOrder, MaxPeriod);
                    ac[0] *= 1.0001f;
                    for (int i = 1; i <= CeltPitch.LpcOrder; i++) ac[i] -= ac[i] * (.008f * i) * (.008f * i);
                    CeltPitch.Lpc(channelLpc, ac, CeltPitch.LpcOrder);
                }
                for (int i = 0; i < CeltPitch.LpcOrder; i++) mem[i] = om[outMem + MaxPeriod - 1 - i];
                CeltPitch.Fir(exc, channelLpc, exc, MaxPeriod, CeltPitch.LpcOrder, mem);
                {
                    float e1 = 1, e2 = 1;
                    int period = pitchIndex <= MaxPeriod / 2 ? pitchIndex : MaxPeriod / 2;
                    for (int i = 0; i < period; i++)
                    {
                        e1 += exc[MaxPeriod - period + i] * exc[MaxPeriod - period + i];
                        e2 += exc[MaxPeriod - 2 * period + i] * exc[MaxPeriod - 2 * period + i];
                    }
                    if (e1 > e2) e1 = e2;
                    decay = CeltMath.Sqrt(e1 / e2);
                }
                for (int i = 0; i < len + Overlap; i++)
                {
                    if (offset + i >= MaxPeriod)
                    {
                        offset -= pitchIndex;
                        decay *= decay;
                    }
                    e[i] = decay * exc[offset + i];
                    float tmp = om[outMem + offset + i];
                    s1 += tmp * tmp;
                }
                for (int i = 0; i < CeltPitch.LpcOrder; i++) mem[i] = om[outMem + MaxPeriod - 1 - i];
                for (int i = 0; i < len + Overlap; i++) e[i] = fade * e[i];
                CeltPitch.Iir(e, channelLpc, e, len + Overlap, CeltPitch.LpcOrder, mem);
                {
                    float s2 = 0;
                    for (int i = 0; i < len + Overlap; i++) s2 += e[i] * e[i];
                    if (!(s1 > .2f * s2))
                    {
                        for (int i = 0; i < len + Overlap; i++) e[i] = 0;
                    }
                    else if (s1 < s2)
                    {
                        float ratio = CeltMath.Sqrt((s1 + 1) / (s2 + 1));
                        for (int i = 0; i < len + Overlap; i++) e[i] *= ratio;
                    }
                }
                int pfPeriod = Math.Max(postfilterPeriod, CombMinPeriod);
                CombFilter(om, outMem + MaxPeriod, outMem + MaxPeriod, pfPeriod, pfPeriod, Overlap, postfilterGain, postfilterGain, postfilterTapset, postfilterTapset, default, 0);
                for (int i = 0; i < MaxPeriod + Overlap - n; i++) om[outMem + i] = om[outMem + n + i];
                for (int i = 0; i < Overlap / 2; i++)
                {
                    float tmp = CeltMode.Window[i] * e[n + Overlap - 1 - i] + CeltMode.Window[Overlap - i - 1] * e[n + i];
                    om[outMem + MaxPeriod + i] = CeltMode.Window[Overlap - i - 1] * tmp;
                    om[outMem + MaxPeriod + Overlap - i - 1] = CeltMode.Window[i] * tmp;
                }
                for (int i = 0; i < n; i++) om[outMem + MaxPeriod - n + i] = e[i];
                // Prepare the overlap for the next frame (the post-filter is applied to it then).
                CombFilterExternal(e, om, outMem + MaxPeriod, pfPeriod, Overlap, -postfilterGain, postfilterTapset);
                for (int i = 0; i < Overlap; i++) om[outMem + MaxPeriod + i] = e[i];
            }
        }
        Deemphasis(outSyn, pcm, pcmOffset, n);
        lossCount++;
    }

    // y[i] = x[i] + g*(taps of x delayed by t) with y distinct from x, and no cross-fade (used by the concealment on the overlap of the previous frame).
    private static void CombFilterExternal(float[] y, float[] x, int xOffset, int t, int n, float g, int tapset)
    {
        ReadOnlySpan<float> gains = CeltTables.CombGains;
        float g0 = g * gains[tapset * 3], g1 = g * gains[tapset * 3 + 1], g2 = g * gains[tapset * 3 + 2];
        for (int i = 0; i < n; i++)
        {
            int xi = xOffset + i;
            y[i] = x[xi] + g0 * x[xi - t] + g1 * x[xi - t - 1] + g1 * x[xi - t + 1] + g2 * x[xi - t - 2] + g2 * x[xi - t + 2];
        }
    }
}
