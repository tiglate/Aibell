// This file is derived from the Opus reference implementation published in RFC 6716 (celt/celt.c, pitch.c, celt_lpc.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
namespace Aibell.Audio.Opus;

/// <summary>Pitch search and linear prediction helpers used only by the packet-loss concealment of the CELT layer.</summary>
internal static class CeltPitch
{
    public const int LpcOrder = 24;

    public static void Autocorr(ReadOnlySpan<float> x, Span<float> ac, ReadOnlySpan<float> window, int overlap, int lag, int n)
    {
        float[] xx = new float[n];
        for (int i = 0; i < n; i++) xx[i] = x[i];
        for (int i = 0; i < overlap; i++)
        {
            xx[i] = x[i] * window[i];
            xx[n - i - 1] = x[n - i - 1] * window[i];
        }
        while (lag >= 0)
        {
            float d = 0;
            for (int i = lag; i < n; i++) d += xx[i] * xx[i - lag];
            ac[lag] = d;
            lag--;
        }
        ac[0] += 10;
    }

    public static void Lpc(Span<float> lpc, ReadOnlySpan<float> ac, int p)
    {
        float error = ac[0];
        for (int i = 0; i < p; i++) lpc[i] = 0;
        if (ac[0] == 0) return;
        for (int i = 0; i < p; i++)
        {
            float rr = 0;
            for (int j = 0; j < i; j++) rr += lpc[j] * ac[i - j];
            rr += ac[i + 1];
            float r = -(rr / error);
            lpc[i] = r;
            for (int j = 0; j < (i + 1) >> 1; j++)
            {
                float tmp1 = lpc[j], tmp2 = lpc[i - 1 - j];
                lpc[j] = tmp1 + r * tmp2;
                lpc[i - 1 - j] = tmp2 + r * tmp1;
            }
            error -= r * r * error;
            if (error < .001f * ac[0]) break;
        }
    }

    public static void Fir(ReadOnlySpan<float> x, ReadOnlySpan<float> num, Span<float> y, int n, int ord, Span<float> mem)
    {
        for (int i = 0; i < n; i++)
        {
            float sum = x[i];
            for (int j = 0; j < ord; j++) sum += num[j] * mem[j];
            for (int j = ord - 1; j >= 1; j--) mem[j] = mem[j - 1];
            mem[0] = x[i];
            y[i] = sum;
        }
    }

    public static void Iir(ReadOnlySpan<float> x, ReadOnlySpan<float> den, Span<float> y, int n, int ord, Span<float> mem)
    {
        for (int i = 0; i < n; i++)
        {
            float sum = x[i];
            for (int j = 0; j < ord; j++) sum -= den[j] * mem[j];
            for (int j = ord - 1; j >= 1; j--) mem[j] = mem[j - 1];
            mem[0] = sum;
            y[i] = sum;
        }
    }

    private static void FindBestPitch(ReadOnlySpan<float> xcorr, ReadOnlySpan<float> y, int len, int maxPitch, Span<int> bestPitch)
    {
        float syy = 1;
        Span<float> bestNum = [-1, -1];
        Span<float> bestDen = [0, 0];
        bestPitch[0] = 0;
        bestPitch[1] = 1;
        for (int j = 0; j < len; j++) syy += y[j] * y[j];
        for (int i = 0; i < maxPitch; i++)
        {
            if (xcorr[i] > 0)
            {
                float num = xcorr[i] * xcorr[i];
                if (num * bestDen[1] > bestNum[1] * syy)
                {
                    if (num * bestDen[0] > bestNum[0] * syy)
                    {
                        bestNum[1] = bestNum[0];
                        bestDen[1] = bestDen[0];
                        bestPitch[1] = bestPitch[0];
                        bestNum[0] = num;
                        bestDen[0] = syy;
                        bestPitch[0] = i;
                    }
                    else
                    {
                        bestNum[1] = num;
                        bestDen[1] = syy;
                        bestPitch[1] = i;
                    }
                }
            }
            syy += y[i + len] * y[i + len] - y[i] * y[i];
            syy = Math.Max(1, syy);
        }
    }

    /// <summary>Low-passes and halves the rate of the decoded history (both channels summed) for the pitch search.</summary>
    public static void Downsample(float[][] x, int[] offsets, Span<float> xLp, int len, int channels)
    {
        for (int i = 1; i < len >> 1; i++) xLp[i] = .5f * (.5f * (x[0][offsets[0] + 2 * i - 1] + x[0][offsets[0] + 2 * i + 1]) + x[0][offsets[0] + 2 * i]);
        xLp[0] = .5f * (.5f * x[0][offsets[0] + 1] + x[0][offsets[0]]);
        if (channels == 2)
        {
            for (int i = 1; i < len >> 1; i++) xLp[i] += .5f * (.5f * (x[1][offsets[1] + 2 * i - 1] + x[1][offsets[1] + 2 * i + 1]) + x[1][offsets[1] + 2 * i]);
            xLp[0] += .5f * (.5f * x[1][offsets[1] + 1] + x[1][offsets[1]]);
        }
        Span<float> ac = stackalloc float[5];
        Autocorr(xLp, ac, default, 0, 4, len >> 1);
        ac[0] *= 1.0001f;
        for (int i = 1; i <= 4; i++) ac[i] -= ac[i] * (.008f * i) * (.008f * i);
        Span<float> lpc = stackalloc float[4];
        Lpc(lpc, ac, 4);
        float tmp = 1f;
        for (int i = 0; i < 4; i++)
        {
            tmp = .9f * tmp;
            lpc[i] *= tmp;
        }
        Span<float> mem = stackalloc float[4];
        Fir(xLp, lpc, xLp, len >> 1, 4, mem);
        mem.Clear();
        lpc[0] = .8f;
        Fir(xLp, lpc, xLp, len >> 1, 1, mem);
    }

    public static int Search(ReadOnlySpan<float> xLp, ReadOnlySpan<float> y, int len, int maxPitch)
    {
        int lag = len + maxPitch;
        float[] xLp4 = new float[len >> 2];
        float[] yLp4 = new float[lag >> 2];
        float[] xcorr = new float[maxPitch >> 1];
        Span<int> best = stackalloc int[2];
        for (int j = 0; j < len >> 2; j++) xLp4[j] = xLp[2 * j];
        for (int j = 0; j < lag >> 2; j++) yLp4[j] = y[2 * j];
        for (int i = 0; i < maxPitch >> 2; i++)
        {
            float sum = 0;
            for (int j = 0; j < len >> 2; j++) sum += xLp4[j] * yLp4[i + j];
            xcorr[i] = Math.Max(-1, sum);
        }
        FindBestPitch(xcorr, yLp4, len >> 2, maxPitch >> 2, best);
        for (int i = 0; i < maxPitch >> 1; i++)
        {
            float sum = 0;
            xcorr[i] = 0;
            if (Math.Abs(i - 2 * best[0]) > 2 && Math.Abs(i - 2 * best[1]) > 2) continue;
            for (int j = 0; j < len >> 1; j++) sum += xLp[j] * y[i + j];
            xcorr[i] = Math.Max(-1, sum);
        }
        FindBestPitch(xcorr, y, len >> 1, maxPitch >> 1, best);
        int offset = 0;
        if (best[0] > 0 && best[0] < (maxPitch >> 1) - 1)
        {
            float a = xcorr[best[0] - 1], b = xcorr[best[0]], c = xcorr[best[0] + 1];
            if (c - a > .7f * (b - a)) offset = 1;
            else if (a - c > .7f * (b - c)) offset = -1;
        }
        return 2 * best[0] - offset;
    }
}
