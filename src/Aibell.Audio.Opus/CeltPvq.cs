// This file is derived from the Opus reference implementation published in RFC 6716 (celt/*.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
namespace Aibell.Audio.Opus;

/// <summary>Pyramid vector quantizer decoding (cwrs.c, vq.c): turns a codeword index into a vector of pulses, rotates it to spread the energy and normalizes it.</summary>
internal sealed class CeltPvq
{
    public const int SpreadNone = 0;
    public const int SpreadLight = 1;
    public const int SpreadNormal = 2;
    public const int SpreadAggressive = 3;

    private readonly uint[] u = new uint[1024];
    private readonly int[] pulses = new int[1024];

    // Computes the next row/column of a recurrence u[i][j] = u[i-1][j] + u[i][j-1] + u[i-1][j-1]; ui0 is the base case of the new row.
    private static void UNext(Span<uint> ui, int len, uint ui0)
    {
        int j = 1;
        do
        {
            uint ui1 = ui[j] + ui[j - 1] + ui0;
            ui[j - 1] = ui0;
            ui0 = ui1;
        }
        while (++j < len);
        ui[j - 1] = ui0;
    }

    private static void UPrev(Span<uint> ui, int n, uint ui0)
    {
        int j = 1;
        do
        {
            uint ui1 = ui[j] - ui[j - 1] - ui0;
            ui[j - 1] = ui0;
            ui0 = ui1;
        }
        while (++j < n);
        ui[j - 1] = ui0;
    }

    // Computes V(n, k) and fills u[i] = U(n, i) for i in 0..k+1.
    private uint NcwrsURow(int n, int k)
    {
        int len = k + 2;
        u[0] = 0;
        u[1] = 1;
        for (int i = 2; i < len; i++) u[i] = (uint)((i << 1) - 1);
        for (int i = 2; i < n; i++) UNext(u.AsSpan(1), k + 1, 1);
        return u[k] + u[k + 1];
    }

    private void Cwrsi(int n, int k, uint index, int[] y)
    {
        int j = 0;
        do
        {
            uint p = u[k + 1];
            int s = index >= p ? -1 : 0;
            index -= p & (uint)s;
            int yj = k;
            p = u[k];
            while (p > index) p = u[--k];
            index -= p;
            yj -= k;
            y[j] = (yj + s) ^ s;
            UPrev(u, k + 2, 0);
        }
        while (++j < n);
    }

    /// <summary>Decodes the pulse vector of n dimensions and k pulses into <paramref name="y"/>.</summary>
    private void DecodePulses(int[] y, int n, int k, RangeDecoder dec)
    {
        uint total = NcwrsURow(n, k);
        Cwrsi(n, k, dec.DecodeUint(total), y);
    }

    private static readonly int[] SpreadFactor = [15, 10, 5];

    private static void ExpRotation1(Span<float> x, int len, int stride, float c, float s)
    {
        for (int i = 0; i < len - stride; i++)
        {
            float x1 = x[i], x2 = x[i + stride];
            x[i + stride] = c * x2 + s * x1;
            x[i] = c * x1 - s * x2;
        }
        for (int i = len - 2 * stride - 1; i >= 0; i--)
        {
            float x1 = x[i], x2 = x[i + stride];
            x[i + stride] = c * x2 + s * x1;
            x[i] = c * x1 - s * x2;
        }
    }

    public static void ExpRotation(Span<float> x, int len, int dir, int stride, int k, int spread)
    {
        if (2 * k >= len || spread == SpreadNone) return;
        int factor = SpreadFactor[spread - 1];
        float gain = 1f * len / (len + factor * k);
        float theta = .5f * (gain * gain);
        float c = CeltMath.CosNorm(theta);
        float s = CeltMath.CosNorm(1f - theta);
        int stride2 = 0;
        if (len >= 8 * stride)
        {
            stride2 = 1;
            while ((stride2 * stride2 + stride2) * stride + (stride >> 2) < len) stride2++;
        }
        len /= stride;
        for (int i = 0; i < stride; i++)
        {
            Span<float> block = x.Slice(i * len, len);
            if (dir < 0)
            {
                if (stride2 != 0) ExpRotation1(block, len, stride2, s, c);
                ExpRotation1(block, len, 1, c, s);
            }
            else
            {
                ExpRotation1(block, len, 1, c, -s);
                if (stride2 != 0) ExpRotation1(block, len, stride2, s, -c);
            }
        }
    }

    private static uint ExtractCollapseMask(int[] iy, int n, int b)
    {
        if (b <= 1) return 1;
        int n0 = n / b;
        uint mask = 0;
        for (int i = 0; i < b; i++)
        {
            for (int j = 0; j < n0; j++) mask |= (iy[i * n0 + j] != 0 ? 1u : 0u) << i;
        }
        return mask;
    }

    /// <summary>Decodes a band's shape of n dimensions and k pulses into x; returns the collapse mask.</summary>
    public uint Unquant(Span<float> x, int n, int k, int spread, int blocks, RangeDecoder dec, float gain)
    {
        DecodePulses(pulses, n, k, dec);
        float ryy = 0;
        for (int i = 0; i < n; i++) ryy += (float)pulses[i] * pulses[i];
        float g = CeltMath.Rsqrt(ryy) * gain;
        for (int i = 0; i < n; i++) x[i] = g * pulses[i];
        ExpRotation(x, n, -1, blocks, k, spread);
        return ExtractCollapseMask(pulses, n, blocks);
    }

    public static void Renormalise(Span<float> x, int n, float gain)
    {
        float e = CeltMath.Epsilon;
        for (int i = 0; i < n; i++) e += x[i] * x[i];
        float g = CeltMath.Rsqrt(e) * gain;
        for (int i = 0; i < n; i++) x[i] *= g;
    }
}
