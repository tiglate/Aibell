// This file is derived from the Opus reference implementation published in RFC 6716 (celt/*.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
namespace Aibell.Audio.Opus;

/// <summary>Decoding of the normalized band shapes (bands.c): splitting, stereo angles, folding, time-frequency changes and denormalization.</summary>
internal sealed class CeltBands
{
    private const int BitRes = CeltRate.BitRes;
    private static readonly short[] Edges = CeltMode.BandEdges;
    private readonly CeltPvq pvq = new();
    private RangeDecoder dec = null!;
    private readonly float[] hadamardTemp = new float[1024];
    private readonly float[] norm;
    private readonly float[] lowbandScratch;

    public CeltBands()
    {
        norm = new float[2 * 8 * Edges[CeltMode.BandCount]];
        lowbandScratch = new float[8 * (Edges[CeltMode.BandCount] - Edges[CeltMode.BandCount - 1])];
    }

    public void Attach(RangeDecoder decoder) => dec = decoder;

    public static void Denormalise(float[] x, int xOffset, float[] freq, int freqOffset, float[] bandE, int bandEOffset, int end, int m, int n)
    {
        int f = freqOffset, xi = xOffset;
        for (int i = 0; i < end; i++)
        {
            float g = bandE[bandEOffset + i];
            int bandEnd = m * Edges[i + 1];
            for (int j = m * Edges[i]; j < bandEnd; j++) freq[f++] = x[xi++] * g;
        }
        for (int i = m * Edges[end]; i < n; i++) freq[f++] = 0;
    }

    public static void AntiCollapse(float[] x, byte[] collapseMasks, int lm, int channels, int size, int start, int end, float[] logE, float[] prev1LogE, float[] prev2LogE,
        int[] pulses, uint seed)
    {
        for (int i = start; i < end; i++)
        {
            int n0 = Edges[i + 1] - Edges[i];
            int depth = (1 + pulses[i]) / ((Edges[i + 1] - Edges[i]) << lm);
            float thresh = .5f * CeltMath.Exp2(-.125f * depth);
            float sqrt1 = CeltMath.Rsqrt(n0 << lm);
            for (int c = 0; c < channels; c++)
            {
                float prev1 = prev1LogE[c * CeltMode.BandCount + i];
                float prev2 = prev2LogE[c * CeltMode.BandCount + i];
                if (channels == 1)
                {
                    prev1 = Math.Max(prev1, prev1LogE[CeltMode.BandCount + i]);
                    prev2 = Math.Max(prev2, prev2LogE[CeltMode.BandCount + i]);
                }
                float ediff = logE[c * CeltMode.BandCount + i] - Math.Min(prev1, prev2);
                ediff = Math.Max(0, ediff);
                float r = 2f * CeltMath.Exp2(-ediff);
                if (lm == 3) r *= 1.41421356f;
                r = Math.Min(thresh, r);
                r *= sqrt1;
                int offset = c * size + (Edges[i] << lm);
                bool renormalize = false;
                for (int k = 0; k < 1 << lm; k++)
                {
                    if ((collapseMasks[i * channels + c] & (1 << k)) == 0)
                    {
                        for (int j = 0; j < n0; j++)
                        {
                            seed = CeltMath.LcgRand(seed);
                            x[offset + (j << lm) + k] = (seed & 0x8000) != 0 ? r : -r;
                        }
                        renormalize = true;
                    }
                }
                if (renormalize) CeltPvq.Renormalise(x.AsSpan(offset, n0 << lm), n0 << lm, 1f);
            }
        }
    }

    private static void StereoMerge(Span<float> x, Span<float> y, float mid, int n)
    {
        float xp = 0, side = 0;
        for (int j = 0; j < n; j++)
        {
            xp += x[j] * y[j];
            side += y[j] * y[j];
        }
        xp = mid * xp;
        float mid2 = mid;
        float el = mid2 * mid2 + side - 2 * xp;
        float er = mid2 * mid2 + side + 2 * xp;
        if (er < 6e-4f || el < 6e-4f)
        {
            for (int j = 0; j < n; j++) y[j] = x[j];
            return;
        }
        float lgain = CeltMath.Rsqrt(el);
        float rgain = CeltMath.Rsqrt(er);
        for (int j = 0; j < n; j++)
        {
            float l = mid * x[j];
            float r = y[j];
            x[j] = lgain * (l - r);
            y[j] = rgain * (l + r);
        }
    }

    private void DeinterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        int n = n0 * stride;
        Span<float> tmp = hadamardTemp.AsSpan(0, n);
        if (hadamard)
        {
            ReadOnlySpan<int> ordery = CeltTables.OrderyTable.AsSpan(stride - 2);
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++) tmp[ordery[i] * n0 + j] = x[j * stride + i];
            }
        }
        else
        {
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++) tmp[i * n0 + j] = x[j * stride + i];
            }
        }
        tmp.CopyTo(x);
    }

    private void InterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        int n = n0 * stride;
        Span<float> tmp = hadamardTemp.AsSpan(0, n);
        if (hadamard)
        {
            ReadOnlySpan<int> ordery = CeltTables.OrderyTable.AsSpan(stride - 2);
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++) tmp[j * stride + i] = x[ordery[i] * n0 + j];
            }
        }
        else
        {
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++) tmp[j * stride + i] = x[i * n0 + j];
            }
        }
        tmp.CopyTo(x);
    }

    private static void Haar1(Span<float> x, int n0, int stride)
    {
        n0 >>= 1;
        for (int i = 0; i < stride; i++)
        {
            for (int j = 0; j < n0; j++)
            {
                float tmp1 = .70710678f * x[stride * 2 * j + i];
                float tmp2 = .70710678f * x[stride * (2 * j + 1) + i];
                x[stride * 2 * j + i] = tmp1 + tmp2;
                x[stride * (2 * j + 1) + i] = tmp1 - tmp2;
            }
        }
    }

    private static readonly short[] Exp2Table8 = [16384, 17866, 19483, 21247, 23170, 25267, 27554, 30048];

    private static int ComputeQn(int n, int b, int offset, int pulseCap, bool stereo)
    {
        int n2 = 2 * n - 1;
        if (stereo && n == 2) n2--;
        int qb = Math.Min(b - pulseCap - (4 << BitRes), (b + n2 * offset) / n2);
        qb = Math.Min(8 << BitRes, qb);
        int qn;
        if (qb < (1 << BitRes >> 1)) qn = 1;
        else
        {
            qn = Exp2Table8[qb & 7] >> (14 - (qb >> BitRes));
            qn = (qn + 1) >> 1 << 1;
        }
        return qn;
    }

    private uint QuantBand(int i, Span<float> x, Span<float> y, int n, int b, int spread, int bBlocks, int intensity, int tfChange, Span<float> lowband, ref int remainingBits,
        int lm, Span<float> lowbandOut, int level, ref uint seed, float gain, int fill)
    {
        int n0 = n;
        int nB = n;
        int b0 = bBlocks;
        int timeDivide = 0;
        int recombine = 0;
        bool inv = false;
        float mid = 0, side = 0;
        uint cm = 0;
        bool longBlocks = b0 == 1;
        nB /= bBlocks;
        int nB0 = nB;
        bool stereo = !y.IsEmpty;
        bool split = stereo;

        if (n == 1)
        {
            Span<float> current = x;
            for (int c = 0; c < (stereo ? 2 : 1); c++)
            {
                int sign = 0;
                if (remainingBits >= 1 << BitRes)
                {
                    sign = (int)dec.DecodeBits(1);
                    remainingBits -= 1 << BitRes;
                    b -= 1 << BitRes;
                }
                current[0] = sign != 0 ? -1f : 1f;
                current = y;
            }
            if (!lowbandOut.IsEmpty) lowbandOut[0] = x[0];
            return 1;
        }

        if (!stereo && level == 0)
        {
            if (tfChange > 0) recombine = tfChange;
            if (!lowband.IsEmpty && (recombine != 0 || ((nB & 1) == 0 && tfChange < 0) || b0 > 1))
            {
                lowband[..n].CopyTo(lowbandScratch);
                lowband = lowbandScratch;
            }
            for (int k = 0; k < recombine; k++)
            {
                if (!lowband.IsEmpty) Haar1(lowband, n >> k, 1 << k);
                fill = CeltTables.BitInterleave[fill & 0xF] | CeltTables.BitInterleave[fill >> 4] << 2;
            }
            bBlocks >>= recombine;
            nB <<= recombine;
            while ((nB & 1) == 0 && tfChange < 0)
            {
                if (!lowband.IsEmpty) Haar1(lowband, nB, bBlocks);
                fill |= fill << bBlocks;
                bBlocks <<= 1;
                nB >>= 1;
                timeDivide++;
                tfChange++;
            }
            b0 = bBlocks;
            nB0 = nB;
            if (b0 > 1 && !lowband.IsEmpty) DeinterleaveHadamard(lowband, nB >> recombine, b0 << recombine, longBlocks);
        }

        if (!stereo && lm != -1 && b > CeltRate.PulseCacheTop(i, lm) + 12 && n > 2)
        {
            n >>= 1;
            y = x[n..];
            split = true;
            lm -= 1;
            if (bBlocks == 1) fill = (fill & 1) | (fill << 1);
            bBlocks = (bBlocks + 1) >> 1;
        }

        if (split)
        {
            int itheta = 0;
            int pulseCap = CeltTables.LogN400[i] + lm * (1 << BitRes);
            int offset = (pulseCap >> 1) - (stereo && n == 2 ? CeltRate.QThetaOffsetTwoPhase : CeltRate.QThetaOffset);
            int qn = ComputeQn(n, b, offset, pulseCap, stereo);
            if (stereo && i >= intensity) qn = 1;
            int tell = dec.TellFrac();
            if (qn != 1)
            {
                if (stereo && n > 2)
                {
                    int p0 = 3;
                    int x0 = qn / 2;
                    int ft = p0 * (x0 + 1) + x0;
                    int fs = (int)dec.Decode((uint)ft);
                    int xv = fs < (x0 + 1) * p0 ? fs / p0 : x0 + 1 + (fs - (x0 + 1) * p0);
                    dec.Update((uint)(xv <= x0 ? p0 * xv : xv - 1 - x0 + (x0 + 1) * p0), (uint)(xv <= x0 ? p0 * (xv + 1) : xv - x0 + (x0 + 1) * p0), (uint)ft);
                    itheta = xv;
                }
                else if (b0 > 1 || stereo)
                {
                    itheta = (int)dec.DecodeUint((uint)(qn + 1));
                }
                else
                {
                    int ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                    int fm = (int)dec.Decode((uint)ft);
                    int fs, fl;
                    if (fm < ((qn >> 1) * ((qn >> 1) + 1) >> 1))
                    {
                        itheta = (int)(CeltMath.Isqrt32(8 * (uint)fm + 1) - 1) >> 1;
                        fs = itheta + 1;
                        fl = itheta * (itheta + 1) >> 1;
                    }
                    else
                    {
                        itheta = (2 * (qn + 1) - (int)CeltMath.Isqrt32(8 * (uint)(ft - fm - 1) + 1)) >> 1;
                        fs = qn + 1 - itheta;
                        fl = ft - ((qn + 1 - itheta) * (qn + 2 - itheta) >> 1);
                    }
                    dec.Update((uint)fl, (uint)(fl + fs), (uint)ft);
                }
                itheta = itheta * 16384 / qn;
            }
            else if (stereo)
            {
                inv = b > 2 << BitRes && remainingBits > 2 << BitRes ? dec.DecodeBitLogP(2) != 0 : false;
                itheta = 0;
            }
            int qalloc = dec.TellFrac() - tell;
            b -= qalloc;
            int origFill = fill;
            int imid, iside, delta;
            if (itheta == 0)
            {
                imid = 32767;
                iside = 0;
                fill &= (1 << bBlocks) - 1;
                delta = -16384;
            }
            else if (itheta == 16384)
            {
                imid = 0;
                iside = 32767;
                fill &= ((1 << bBlocks) - 1) << bBlocks;
                delta = 16384;
            }
            else
            {
                imid = CeltMath.BitexactCos((short)itheta);
                iside = CeltMath.BitexactCos((short)(16384 - itheta));
                delta = CeltMath.FracMul16((n - 1) << 7, CeltMath.BitexactLog2Tan(iside, imid));
            }
            mid = 1f / 32768 * imid;
            side = 1f / 32768 * iside;
            int mbits, sbits;
            if (n == 2 && stereo)
            {
                mbits = b;
                sbits = 0;
                if (itheta != 0 && itheta != 16384) sbits = 1 << BitRes;
                mbits -= sbits;
                bool c = itheta > 8192;
                remainingBits -= qalloc + sbits;
                Span<float> x2 = c ? y : x;
                Span<float> y2 = c ? x : y;
                int sign = 0;
                if (sbits != 0) sign = (int)dec.DecodeBits(1);
                sign = 1 - 2 * sign;
                cm = QuantBand(i, x2, default, n, mbits, spread, bBlocks, intensity, tfChange, lowband, ref remainingBits, lm, lowbandOut, level, ref seed, gain, origFill);
                y2[0] = -sign * x2[1];
                y2[1] = sign * x2[0];
                x[0] = mid * x[0];
                x[1] = mid * x[1];
                y[0] = side * y[0];
                y[1] = side * y[1];
                float tmp = x[0];
                x[0] = tmp - y[0];
                y[0] = tmp + y[0];
                tmp = x[1];
                x[1] = tmp - y[1];
                y[1] = tmp + y[1];
            }
            else
            {
                if (b0 > 1 && !stereo && (itheta & 0x3fff) != 0)
                {
                    if (itheta > 8192) delta -= delta >> (4 - lm);
                    else delta = Math.Min(0, delta + (n << BitRes >> (5 - lm)));
                }
                mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
                sbits = b - mbits;
                remainingBits -= qalloc;
                Span<float> nextLowband2 = default;
                if (!lowband.IsEmpty && !stereo) nextLowband2 = lowband[n..];
                Span<float> nextLowbandOut1 = stereo ? lowbandOut : default;
                int nextLevel = stereo ? 0 : level + 1;
                int rebalance = remainingBits;
                if (mbits >= sbits)
                {
                    cm = QuantBand(i, x, default, n, mbits, spread, bBlocks, intensity, tfChange, lowband, ref remainingBits, lm, nextLowbandOut1, nextLevel, ref seed,
                        stereo ? 1f : gain * mid, fill);
                    rebalance = mbits - (rebalance - remainingBits);
                    if (rebalance > 3 << BitRes && itheta != 0) sbits += rebalance - (3 << BitRes);
                    cm |= QuantBand(i, y, default, n, sbits, spread, bBlocks, intensity, tfChange, nextLowband2, ref remainingBits, lm, default, nextLevel, ref seed,
                        gain * side, fill >> bBlocks) << ((b0 >> 1) & ((stereo ? 1 : 0) - 1));
                }
                else
                {
                    cm = QuantBand(i, y, default, n, sbits, spread, bBlocks, intensity, tfChange, nextLowband2, ref remainingBits, lm, default, nextLevel, ref seed,
                        gain * side, fill >> bBlocks) << ((b0 >> 1) & ((stereo ? 1 : 0) - 1));
                    rebalance = sbits - (rebalance - remainingBits);
                    if (rebalance > 3 << BitRes && itheta != 16384) mbits += rebalance - (3 << BitRes);
                    cm |= QuantBand(i, x, default, n, mbits, spread, bBlocks, intensity, tfChange, lowband, ref remainingBits, lm, nextLowbandOut1, nextLevel, ref seed,
                        stereo ? 1f : gain * mid, fill);
                }
            }
        }
        else
        {
            int q = CeltRate.BitsToPulses(i, lm, b);
            int currBits = CeltRate.PulsesToBits(i, lm, q);
            remainingBits -= currBits;
            while (remainingBits < 0 && q > 0)
            {
                remainingBits += currBits;
                q--;
                currBits = CeltRate.PulsesToBits(i, lm, q);
                remainingBits -= currBits;
            }
            if (q != 0)
            {
                int k = CeltRate.GetPulses(q);
                cm = pvq.Unquant(x, n, k, spread, bBlocks, dec, gain);
            }
            else
            {
                uint cmMask = (uint)((1UL << bBlocks) - 1);
                fill &= (int)cmMask;
                if (fill == 0)
                {
                    x[..n].Clear();
                }
                else
                {
                    if (lowband.IsEmpty)
                    {
                        for (int j = 0; j < n; j++)
                        {
                            seed = CeltMath.LcgRand(seed);
                            x[j] = (int)seed >> 20;
                        }
                        cm = cmMask;
                    }
                    else
                    {
                        for (int j = 0; j < n; j++)
                        {
                            seed = CeltMath.LcgRand(seed);
                            float tmp = 1f / 256;
                            tmp = (seed & 0x8000) != 0 ? tmp : -tmp;
                            x[j] = lowband[j] + tmp;
                        }
                        cm = (uint)fill;
                    }
                    CeltPvq.Renormalise(x, n, gain);
                }
            }
        }

        if (stereo)
        {
            if (n != 2) StereoMerge(x, y, mid, n);
            if (inv)
            {
                for (int j = 0; j < n; j++) y[j] = -y[j];
            }
        }
        else if (level == 0)
        {
            if (b0 > 1) InterleaveHadamard(x, nB >> recombine, b0 << recombine, longBlocks);
            nB = nB0;
            bBlocks = b0;
            for (int k = 0; k < timeDivide; k++)
            {
                bBlocks >>= 1;
                nB <<= 1;
                cm |= cm >> bBlocks;
                Haar1(x, nB, bBlocks);
            }
            for (int k = 0; k < recombine; k++)
            {
                cm = CeltTables.BitDeinterleave[(int)cm];
                Haar1(x, n0 >> k, 1 << k);
            }
            bBlocks <<= recombine;
            if (!lowbandOut.IsEmpty)
            {
                float scale = CeltMath.Sqrt(n0);
                for (int j = 0; j < n0; j++) lowbandOut[j] = scale * x[j];
            }
            cm &= (uint)((1 << bBlocks) - 1);
        }
        return cm;
    }

    public void DecodeAll(int start, int end, float[] xArray, int channels, int n, byte[] collapseMasks, int[] pulses, bool shortBlocks, int spread, bool dualStereo, int intensity,
        int[] tfRes, int totalBits, int balance, int lm, int codedBands, ref uint seed)
    {
        int m = 1 << lm;
        int blocks = shortBlocks ? m : 1;
        int normSize = m * Edges[CeltMode.BandCount];
        Span<float> norm1 = norm.AsSpan(0, normSize);
        Span<float> norm2 = norm.AsSpan(normSize, normSize);
        int lowbandOffset = 0;
        bool updateLowband = true;
        int remainingBits;
        for (int i = start; i < end; i++)
        {
            int tell = dec.TellFrac();
            if (i != start) balance -= tell;
            remainingBits = totalBits - tell - 1;
            int bandN = m * Edges[i + 1] - m * Edges[i];
            int b;
            if (i <= codedBands - 1)
            {
                int currBalance = balance / Math.Min(3, codedBands - i);
                b = Math.Max(0, Math.Min(16383, Math.Min(remainingBits + 1, pulses[i] + currBalance)));
            }
            else b = 0;
            if ((m * Edges[i] - bandN >= m * Edges[start] || i == start + 1) && (updateLowband || lowbandOffset == 0)) lowbandOffset = i;
            if (i == start + 1)
            {
                // RFC 8251 section 9: repeat part of the first band so the second band can always fold from it (nothing is copied in CELT-only frames).
                int n1 = m * (Edges[start + 1] - Edges[start]);
                int n2 = m * (Edges[start + 2] - Edges[start + 1]);
                int offset = m * Edges[start];
                if (n2 > n1)
                {
                    norm1.Slice(offset + 2 * n1 - n2, n2 - n1).CopyTo(norm1[(offset + n1)..]);
                    if (channels == 2) norm2.Slice(offset + 2 * n1 - n2, n2 - n1).CopyTo(norm2[(offset + n1)..]);
                }
            }
            int tfChange = tfRes[i];
            Span<float> x = xArray.AsSpan(m * Edges[i], bandN);
            Span<float> y = channels == 2 ? xArray.AsSpan(n + m * Edges[i], bandN) : default;
            if (i >= CeltMode.BandCount)
            {
                x = norm1[..bandN];
                if (channels == 2) y = norm1[..bandN];
            }
            int effectiveLowband = -1;
            uint xCm, yCm;
            if (lowbandOffset != 0 && (spread != CeltPvq.SpreadAggressive || blocks > 1 || tfChange < 0))
            {
                effectiveLowband = Math.Max(m * Edges[start], m * Edges[lowbandOffset] - bandN);
                int foldStart = lowbandOffset;
                while (m * Edges[--foldStart] > effectiveLowband) { }
                int foldEnd = lowbandOffset - 1;
                while (++foldEnd < i && m * Edges[foldEnd] < effectiveLowband + bandN) { }
                xCm = yCm = 0;
                int foldI = foldStart;
                do
                {
                    xCm |= collapseMasks[foldI * channels + 0];
                    yCm |= collapseMasks[foldI * channels + channels - 1];
                }
                while (++foldI < foldEnd);
            }
            else xCm = yCm = (uint)((1 << blocks) - 1);
            if (dualStereo && i == intensity)
            {
                dualStereo = false;
                for (int j = m * Edges[start]; j < m * Edges[i]; j++) norm1[j] = .5f * (norm1[j] + norm2[j]);
            }
            if (dualStereo)
            {
                xCm = QuantBand(i, x, default, bandN, b / 2, spread, blocks, intensity, tfChange, effectiveLowband != -1 ? norm1[effectiveLowband..] : default, ref remainingBits, lm,
                    norm1[(m * Edges[i])..], 0, ref seed, 1f, (int)xCm);
                yCm = QuantBand(i, y, default, bandN, b / 2, spread, blocks, intensity, tfChange, effectiveLowband != -1 ? norm2[effectiveLowband..] : default, ref remainingBits, lm,
                    norm2[(m * Edges[i])..], 0, ref seed, 1f, (int)yCm);
            }
            else
            {
                xCm = QuantBand(i, x, y, bandN, b, spread, blocks, intensity, tfChange, effectiveLowband != -1 ? norm1[effectiveLowband..] : default, ref remainingBits, lm,
                    norm1[(m * Edges[i])..], 0, ref seed, 1f, (int)(xCm | yCm));
                yCm = xCm;
            }
            collapseMasks[i * channels + 0] = (byte)xCm;
            collapseMasks[i * channels + channels - 1] = (byte)yCm;
            balance += pulses[i] + tell;
            updateLowband = b > bandN << BitRes;
        }
    }
}
