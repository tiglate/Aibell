// This file is derived from the Opus reference implementation published in RFC 6716 (celt/*.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
using System.Numerics;

namespace Aibell.Audio.Opus;

/// <summary>Arithmetic helpers of the CELT layer. Only the floating-point configuration of the reference decoder (the normative one) is implemented.</summary>
internal static class CeltMath
{
    public const float Pi = 3.141592653f;
    public const float Epsilon = 1e-15f;

    public static uint LcgRand(uint seed) => 1664525u * seed + 1013904223u;

    public static int Ilog(uint value) => 32 - BitOperations.LeadingZeroCount(value);

    public static float Sqrt(float x) => (float)Math.Sqrt(x);

    public static float Rsqrt(float x) => 1f / Sqrt(x);

    public static float CosNorm(float x) => (float)Math.Cos(0.5f * Pi * x);

    public static float Log2(float x) => (float)(1.442695040888963387 * Math.Log(x));

    public static float Exp2(float x) => (float)Math.Exp(0.6931471805599453094 * x);

    /// <summary>floor(sqrt(value)) with exact integer arithmetic.</summary>
    public static uint Isqrt32(uint value)
    {
        uint g = 0;
        int shift = (Ilog(value) - 1) >> 1;
        uint b = 1u << shift;
        do
        {
            uint t = ((g << 1) + b) << shift;
            if (t <= value)
            {
                g += b;
                value -= t;
            }
            b >>= 1;
            shift--;
        }
        while (shift >= 0);
        return g;
    }

    public static int FracMul16(int a, int b) => (16384 + (short)a * (short)b) >> 15;

    /// <summary>A cosine approximation designed to be bit-exact on every platform (it influences the bit allocation).</summary>
    public static short BitexactCos(short x)
    {
        int tmp = (4096 + x * x) >> 13;
        short x2 = (short)tmp;
        x2 = (short)((32767 - x2) + FracMul16(x2, -7651 + FracMul16(x2, 8277 + FracMul16(-626, x2))));
        return (short)(1 + x2);
    }

    public static int BitexactLog2Tan(int isin, int icos)
    {
        int lc = Ilog((uint)icos);
        int ls = Ilog((uint)isin);
        icos <<= 15 - lc;
        isin <<= 15 - ls;
        return (ls - lc) * (1 << 11) + FracMul16(isin, FracMul16(isin, -2597) + 7932) - FracMul16(icos, FracMul16(icos, -2597) + 7932);
    }
}
