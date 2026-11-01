// This file is derived from the Opus reference implementation published in RFC 6716 (celt/*.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
namespace Aibell.Audio.Opus;

/// <summary>Band energy decoding (quant_bands.c, laplace.c).</summary>
internal static class CeltEnergy
{
    private const int NbEBands = CeltMode.BandCount;
    private static readonly float[] PredCoef = [29440 / 32768f, 26112 / 32768f, 21248 / 32768f, 16384 / 32768f];
    private static readonly float[] BetaCoef = [30147 / 32768f, 22282 / 32768f, 12124 / 32768f, 6554 / 32768f];
    private const float BetaIntra = 4915 / 32768f;

    private static uint LaplaceFreq1(uint fs0, int decay)
    {
        uint ft = 32768 - 1 * (2 * 16) - fs0;
        return ft * (uint)(16384 - decay) >> 15;
    }

    private static int LaplaceDecode(RangeDecoder dec, uint fs, int decay)
    {
        int value = 0;
        uint fm = dec.DecodeBin(15);
        uint fl = 0;
        if (fm >= fs)
        {
            value++;
            fl = fs;
            fs = LaplaceFreq1(fs, decay) + 1;
            while (fs > 1 && fm >= fl + 2 * fs)
            {
                fs *= 2;
                fl += fs;
                fs = (uint)(((int)(fs - 2) * decay) >> 15);
                fs += 1;
                value++;
            }
            if (fs <= 1)
            {
                int di = (int)(fm - fl) >> 1;
                value += di;
                fl += (uint)(2 * di);
            }
            if (fm < fl + fs) value = -value;
            else fl += fs;
        }
        dec.Update(fl, Math.Min(fl + fs, 32768), 32768);
        return value;
    }

    public static void UnquantCoarse(int start, int end, float[] oldBandE, bool intra, RangeDecoder dec, int channels, int lm)
    {
        int model = (lm * 2 + (intra ? 1 : 0)) * 42;
        float coef, beta;
        if (intra)
        {
            coef = 0;
            beta = BetaIntra;
        }
        else
        {
            beta = BetaCoef[lm];
            coef = PredCoef[lm];
        }
        int budget = dec.Storage * 8;
        Span<float> prev = stackalloc float[2];
        for (int i = start; i < end; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                int tell = dec.Tell();
                int qi;
                if (budget - tell >= 15)
                {
                    int pi = 2 * Math.Min(i, 20);
                    qi = LaplaceDecode(dec, (uint)CeltTables.EnergyProbabilityModel[model + pi] << 7, CeltTables.EnergyProbabilityModel[model + pi + 1] << 6);
                }
                else if (budget - tell >= 2)
                {
                    qi = dec.DecodeIcdf(CeltTables.SmallEnergyIcdf, 2);
                    qi = (qi >> 1) ^ -(qi & 1);
                }
                else if (budget - tell >= 1) qi = -dec.DecodeBitLogP(1);
                else qi = -1;
                float q = qi;
                int index = i + c * NbEBands;
                oldBandE[index] = Math.Max(-9f, oldBandE[index]);
                float tmp = coef * oldBandE[index] + prev[c] + q;
                oldBandE[index] = tmp;
                prev[c] = prev[c] + q - beta * q;
            }
        }
    }

    public static void UnquantFine(int start, int end, float[] oldBandE, int[] fineQuant, RangeDecoder dec, int channels)
    {
        for (int i = start; i < end; i++)
        {
            if (fineQuant[i] <= 0) continue;
            for (int c = 0; c < channels; c++)
            {
                int q2 = (int)dec.DecodeBits(fineQuant[i]);
                float offset = (q2 + .5f) * (1 << (14 - fineQuant[i])) * (1f / 16384) - .5f;
                oldBandE[i + c * NbEBands] += offset;
            }
        }
    }

    public static void UnquantFinalise(int start, int end, float[] oldBandE, int[] fineQuant, int[] finePriority, int bitsLeft, RangeDecoder dec, int channels)
    {
        for (int prio = 0; prio < 2; prio++)
        {
            for (int i = start; i < end && bitsLeft >= channels; i++)
            {
                if (fineQuant[i] >= CeltRate.MaxFineBits || finePriority[i] != prio) continue;
                for (int c = 0; c < channels; c++)
                {
                    int q2 = (int)dec.DecodeBits(1);
                    float offset = (q2 - .5f) * (1 << (14 - fineQuant[i] - 1)) * (1f / 16384);
                    oldBandE[i + c * NbEBands] += offset;
                    bitsLeft--;
                }
            }
        }
    }

    public static void Log2Amp(int start, int end, float[] bandE, float[] oldBandE, int channels)
    {
        for (int c = 0; c < channels; c++)
        {
            int i = 0;
            for (; i < start; i++) bandE[i + c * NbEBands] = 0;
            for (; i < end; i++) bandE[i + c * NbEBands] = CeltMath.Exp2(Math.Min(32f, oldBandE[i + c * NbEBands] + CeltTables.EnergyMeans[i]));
            for (; i < NbEBands; i++) bandE[i + c * NbEBands] = 0;
        }
    }
}
