// This file is derived from the Opus reference implementation published in RFC 6716 (celt/*.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
namespace Aibell.Audio.Opus;

/// <summary>Bit allocation of the CELT layer (rate.c): how many bits each band gets for its shape and its fine energy.</summary>
internal static class CeltRate
{
    public const int MaxFineBits = 8;
    public const int BitRes = 3;
    public const int FineOffset = 21;
    public const int QThetaOffset = 4;
    public const int QThetaOffsetTwoPhase = 16;
    private const int LogMaxPseudo = 6;
    private const int AllocSteps = 6;
    private static readonly short[] Edges = CeltMode.BandEdges;

    public static int GetPulses(int i) => i < 8 ? i : (8 + (i & 7)) << ((i >> 3) - 1);

    private static int CacheBase(int lm, int band) => CeltTables.CacheIndex50[(lm + 1) * CeltMode.BandCount + band];

    /// <summary>The number of pulses whose cost is nearest to <paramref name="bits"/> (1/8 bit units) in the band.</summary>
    public static int BitsToPulses(int band, int lm, int bits)
    {
        ReadOnlySpan<byte> cache = CeltTables.CacheBits50[CacheBase(lm, band)..];
        int lo = 0, hi = cache[0];
        bits--;
        for (int i = 0; i < LogMaxPseudo; i++)
        {
            int mid = (lo + hi + 1) >> 1;
            if (cache[mid] >= bits) hi = mid;
            else lo = mid;
        }
        return bits - (lo == 0 ? -1 : cache[lo]) <= cache[hi] - bits ? lo : hi;
    }

    public static int PulsesToBits(int band, int lm, int pulses)
    {
        ReadOnlySpan<byte> cache = CeltTables.CacheBits50[CacheBase(lm, band)..];
        return pulses == 0 ? 0 : cache[pulses] + 1;
    }

    /// <summary>The cost (1/8 bits) of the largest pulse count the cache holds for the band; a band needing more than this plus 1.5 bits is split.</summary>
    public static int PulseCacheTop(int band, int lm)
    {
        ReadOnlySpan<byte> cache = CeltTables.CacheBits50[CacheBase(lm, band)..];
        return cache[cache[0]];
    }

    public static void InitCaps(int[] cap, int lm, int channels)
    {
        for (int i = 0; i < CeltMode.BandCount; i++)
        {
            int n = (Edges[i + 1] - Edges[i]) << lm;
            cap[i] = (CeltTables.CacheCaps50[CeltMode.BandCount * (2 * lm + channels - 1) + i] + 64) * channels * n >> 2;
        }
    }

    /// <summary>Returns the number of coded bands and fills pulses, fineBits and finePriority; may read the skip, intensity and dual-stereo symbols.</summary>
    public static int ComputeAllocation(int start, int end, int[] offsets, int[] cap, int allocTrim, ref int intensity, ref bool dualStereo, int total, out int balance,
        int[] pulses, int[] fineBits, int[] finePriority, int channels, int lm, RangeDecoder dec)
    {
        total = Math.Max(total, 0);
        int len = CeltMode.BandCount;
        int skipStart = start;
        int skipRsv = total >= 1 << BitRes ? 1 << BitRes : 0;
        total -= skipRsv;
        int intensityRsv = 0, dualStereoRsv = 0;
        if (channels == 2)
        {
            intensityRsv = CeltTables.Log2Frac[end - start];
            if (intensityRsv > total) intensityRsv = 0;
            else
            {
                total -= intensityRsv;
                dualStereoRsv = total >= 1 << BitRes ? 1 << BitRes : 0;
                total -= dualStereoRsv;
            }
        }
        Span<int> bits1 = stackalloc int[len], bits2 = stackalloc int[len], thresh = stackalloc int[len], trimOffset = stackalloc int[len];
        for (int j = start; j < end; j++)
        {
            thresh[j] = Math.Max(channels << BitRes, (3 * (Edges[j + 1] - Edges[j]) << lm << BitRes) >> 4);
            trimOffset[j] = channels * (Edges[j + 1] - Edges[j]) * (allocTrim - 5 - lm) * (end - j - 1) * (1 << (lm + BitRes)) >> 6;
            if ((Edges[j + 1] - Edges[j]) << lm == 1) trimOffset[j] -= channels << BitRes;
        }
        int lo = 1, hi = CeltMode.AllocationRows - 1;
        do
        {
            bool done = false;
            int psum = 0;
            int mid = (lo + hi) >> 1;
            for (int j = end; j-- > start;)
            {
                int n = Edges[j + 1] - Edges[j];
                int bitsj = channels * n * CeltTables.BandAllocation[mid * len + j] << lm >> 2;
                if (bitsj > 0) bitsj = Math.Max(0, bitsj + trimOffset[j]);
                bitsj += offsets[j];
                if (bitsj >= thresh[j] || done)
                {
                    done = true;
                    psum += Math.Min(bitsj, cap[j]);
                }
                else if (bitsj >= channels << BitRes) psum += channels << BitRes;
            }
            if (psum > total) hi = mid - 1;
            else lo = mid + 1;
        }
        while (lo <= hi);
        hi = lo--;
        for (int j = start; j < end; j++)
        {
            int n = Edges[j + 1] - Edges[j];
            int bits1j = channels * n * CeltTables.BandAllocation[lo * len + j] << lm >> 2;
            int bits2j = hi >= CeltMode.AllocationRows ? cap[j] : channels * n * CeltTables.BandAllocation[hi * len + j] << lm >> 2;
            if (bits1j > 0) bits1j = Math.Max(0, bits1j + trimOffset[j]);
            if (bits2j > 0) bits2j = Math.Max(0, bits2j + trimOffset[j]);
            if (lo > 0) bits1j += offsets[j];
            bits2j += offsets[j];
            if (offsets[j] > 0) skipStart = j;
            bits2j = Math.Max(0, bits2j - bits1j);
            bits1[j] = bits1j;
            bits2[j] = bits2j;
        }
        return InterpBitsToPulses(start, end, skipStart, bits1, bits2, thresh, cap, total, out balance, skipRsv, ref intensity, intensityRsv, ref dualStereo, dualStereoRsv,
            pulses, fineBits, finePriority, channels, lm, dec);
    }

    private static int InterpBitsToPulses(int start, int end, int skipStart, Span<int> bits1, Span<int> bits2, Span<int> thresh, int[] cap, int total, out int balanceOut,
        int skipRsv, ref int intensity, int intensityRsv, ref bool dualStereo, int dualStereoRsv, int[] bits, int[] ebits, int[] finePriority, int channels, int lm, RangeDecoder dec)
    {
        int allocFloor = channels << BitRes;
        bool stereo = channels > 1;
        int logM = lm << BitRes;
        int lo = 0, hi = 1 << AllocSteps;
        for (int i = 0; i < AllocSteps; i++)
        {
            int mid = (lo + hi) >> 1;
            int psum = 0;
            bool done = false;
            for (int j = end; j-- > start;)
            {
                int tmp = bits1[j] + (mid * bits2[j] >> AllocSteps);
                if (tmp >= thresh[j] || done)
                {
                    done = true;
                    psum += Math.Min(tmp, cap[j]);
                }
                else if (tmp >= allocFloor) psum += allocFloor;
            }
            if (psum > total) hi = mid;
            else lo = mid;
        }
        int sum = 0;
        bool finished = false;
        for (int j = end; j-- > start;)
        {
            int tmp = bits1[j] + (lo * bits2[j] >> AllocSteps);
            if (tmp < thresh[j] && !finished)
            {
                tmp = tmp >= allocFloor ? allocFloor : 0;
            }
            else finished = true;
            tmp = Math.Min(tmp, cap[j]);
            bits[j] = tmp;
            sum += tmp;
        }
        int codedBands;
        for (codedBands = end; ; codedBands--)
        {
            int j = codedBands - 1;
            if (j <= skipStart)
            {
                total += skipRsv;
                break;
            }
            int left = total - sum;
            int percoeff = left / (Edges[codedBands] - Edges[start]);
            left -= (Edges[codedBands] - Edges[start]) * percoeff;
            int rem = Math.Max(left - (Edges[j] - Edges[start]), 0);
            int bandWidth = Edges[codedBands] - Edges[j];
            int bandBits = bits[j] + percoeff * bandWidth + rem;
            if (bandBits >= Math.Max(thresh[j], allocFloor + (1 << BitRes)))
            {
                if (dec.DecodeBitLogP(1) != 0) break;
                sum += 1 << BitRes;
                bandBits -= 1 << BitRes;
            }
            sum -= bits[j] + intensityRsv;
            if (intensityRsv > 0) intensityRsv = CeltTables.Log2Frac[j - start];
            sum += intensityRsv;
            if (bandBits >= allocFloor)
            {
                sum += allocFloor;
                bits[j] = allocFloor;
            }
            else bits[j] = 0;
        }
        if (intensityRsv > 0) intensity = start + (int)dec.DecodeUint((uint)(codedBands + 1 - start));
        else intensity = 0;
        if (intensity <= start)
        {
            total += dualStereoRsv;
            dualStereoRsv = 0;
        }
        if (dualStereoRsv > 0) dualStereo = dec.DecodeBitLogP(1) != 0;
        else dualStereo = false;
        {
            int left = total - sum;
            int percoeff = left / (Edges[codedBands] - Edges[start]);
            left -= (Edges[codedBands] - Edges[start]) * percoeff;
            for (int j = start; j < codedBands; j++) bits[j] += percoeff * (Edges[j + 1] - Edges[j]);
            for (int j = start; j < codedBands; j++)
            {
                int tmp = Math.Min(left, Edges[j + 1] - Edges[j]);
                bits[j] += tmp;
                left -= tmp;
            }
        }
        int balance = 0;
        int band;
        for (band = start; band < codedBands; band++)
        {
            int n0 = Edges[band + 1] - Edges[band];
            int n = n0 << lm;
            bits[band] += balance;
            int excess;
            if (n > 1)
            {
                excess = Math.Max(bits[band] - cap[band], 0);
                bits[band] -= excess;
                int den = channels * n + (channels == 2 && n > 2 && !dualStereo && band < intensity ? 1 : 0);
                int nClogN = den * (CeltTables.LogN400[band] + logM);
                int offset = (nClogN >> 1) - den * FineOffset;
                if (n == 2) offset += den << BitRes >> 2;
                if (bits[band] + offset < den * 2 << BitRes) offset += nClogN >> 2;
                else if (bits[band] + offset < den * 3 << BitRes) offset += nClogN >> 3;
                ebits[band] = Math.Max(0, (bits[band] + offset + (den << (BitRes - 1))) / (den << BitRes));
                if (channels * ebits[band] > bits[band] >> BitRes) ebits[band] = bits[band] >> (stereo ? 1 : 0) >> BitRes;
                ebits[band] = Math.Min(ebits[band], MaxFineBits);
                finePriority[band] = ebits[band] * (den << BitRes) >= bits[band] + offset ? 1 : 0;
                bits[band] -= channels * ebits[band] << BitRes;
            }
            else
            {
                excess = Math.Max(0, bits[band] - (channels << BitRes));
                bits[band] -= excess;
                ebits[band] = 0;
                finePriority[band] = 1;
            }
            if (excess > 0)
            {
                int extraFine = Math.Min(excess >> ((stereo ? 1 : 0) + BitRes), MaxFineBits - ebits[band]);
                ebits[band] += extraFine;
                int extraBits = extraFine * channels << BitRes;
                finePriority[band] = extraBits >= excess - balance ? 1 : 0;
                excess -= extraBits;
            }
            balance = excess;
        }
        balanceOut = balance;
        for (; band < end; band++)
        {
            ebits[band] = bits[band] >> (stereo ? 1 : 0) >> BitRes;
            bits[band] = 0;
            finePriority[band] = ebits[band] < 1 ? 1 : 0;
        }
        return codedBands;
    }
}
