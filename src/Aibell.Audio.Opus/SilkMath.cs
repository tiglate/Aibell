// This file is derived from the Opus reference implementation published in RFC 6716 (silk/*.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aibell.Audio.Opus;

/// <summary>The fixed-point primitives of the SILK layer (SigProc_FIX.h, macros.h, Inlines.h). All arithmetic wraps like the 32-bit C code it mirrors.</summary>
internal static class SilkMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulwb(int a, int b) => (int)(((long)a * (short)b) >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smlawb(int a, int b, int c) => unchecked(a + (int)(((long)b * (short)c) >> 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulwt(int a, int b) => (int)(((long)a * (b >> 16)) >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulbb(int a, int b) => (short)a * (short)b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smlabb(int a, int b, int c) => unchecked(a + (short)b * (short)c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulww(int a, int b) => unchecked(Smulwb(a, b) + a * RShiftRound(b, 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smlaww(int a, int b, int c) => unchecked(Smlawb(a, b, c) + b * RShiftRound(c, 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smmul(int a, int b) => (int)(((long)a * b) >> 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RShiftRound(int a, int shift) => shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long RShiftRound64(long a, int shift) => shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sat16(int a) => a > short.MaxValue ? short.MaxValue : a < short.MinValue ? short.MinValue : a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Limit(int a, int limit1, int limit2) =>
        limit1 > limit2 ? (a > limit1 ? limit1 : a < limit2 ? limit2 : a) : (a > limit2 ? limit2 : a < limit1 ? limit1 : a);

    public static int Clz32(int x) => BitOperations.LeadingZeroCount((uint)x);

    public static int Rand(int seed) => unchecked(907633515 + seed * 196314165);

    public static int Abs(int a) => a > 0 ? a : -a;

    public static int Ror32(int a, int rot)
    {
        uint x = (uint)a, r = (uint)rot, m = (uint)-rot;
        if (rot == 0) return a;
        if (rot < 0) return (int)((x << (int)m) | (x >> (int)(32 - m)));
        return (int)((x << (int)(32 - r)) | (x >> (int)r));
    }

    public static int SqrtApprox(int x)
    {
        if (x <= 0) return 0;
        int lz = Clz32(x);
        int frac = Ror32(x, 24 - lz) & 0x7f;
        int y = (lz & 1) != 0 ? 32768 : 46214;
        y >>= lz >> 1;
        return Smlawb(y, y, Smulbb(213, frac));
    }

    public static int LShiftSat32(int a, int shift) => unchecked(Limit(a, int.MinValue >> shift, int.MaxValue >> shift) << shift);

    public static int Div32VarQ(int a32, int b32, int qres)
    {
        int aHeadroom = Clz32(Abs(a32)) - 1;
        int a32Nrm = unchecked(a32 << aHeadroom);
        int bHeadroom = Clz32(Abs(b32)) - 1;
        int b32Nrm = unchecked(b32 << bHeadroom);
        int b32Inv = (int.MaxValue >> 2) / (b32Nrm >> 16);
        int result = Smulwb(a32Nrm, b32Inv);
        a32Nrm = unchecked(a32Nrm - (Smmul(b32Nrm, result) << 3));
        result = Smlawb(result, a32Nrm, b32Inv);
        int lshift = 29 + aHeadroom - bHeadroom - qres;
        if (lshift < 0) return LShiftSat32(result, -lshift);
        return lshift < 32 ? result >> lshift : 0;
    }

    public static int Inverse32VarQ(int b32, int qres)
    {
        int bHeadroom = Clz32(Abs(b32)) - 1;
        int b32Nrm = unchecked(b32 << bHeadroom);
        int b32Inv = (int.MaxValue >> 2) / (b32Nrm >> 16);
        int result = unchecked(b32Inv << 16);
        int err = unchecked(((1 << 29) - Smulwb(b32Nrm, b32Inv)) << 3);
        result = Smlaww(result, err, b32Inv);
        int lshift = 61 - bHeadroom - qres;
        if (lshift <= 0) return LShiftSat32(result, -lshift);
        return lshift < 32 ? result >> lshift : 0;
    }

    public static int Log2Lin(int inLogQ7)
    {
        if (inLogQ7 < 0) return 0;
        int output = 1 << (inLogQ7 >> 7);
        int frac = inLogQ7 & 0x7F;
        if (inLogQ7 < 2048) output = unchecked(output + ((output * Smlawb(frac, Smulbb(frac, 128 - frac), -174)) >> 7));
        else output = unchecked(output + (output >> 7) * Smlawb(frac, Smulbb(frac, 128 - frac), -174));
        return output;
    }

    /// <summary>Bandwidth expansion of 16-bit coefficients (chirp factor in Q16).</summary>
    public static void BwExpander(Span<short> ar, int d, int chirpQ16)
    {
        int chirpMinusOne = chirpQ16 - 65536;
        for (int i = 0; i < d - 1; i++)
        {
            ar[i] = (short)RShiftRound(unchecked(chirpQ16 * ar[i]), 16);
            chirpQ16 += RShiftRound(unchecked(chirpQ16 * chirpMinusOne), 16);
        }
        ar[d - 1] = (short)RShiftRound(unchecked(chirpQ16 * ar[d - 1]), 16);
    }

    public static void BwExpander32(Span<int> ar, int d, int chirpQ16)
    {
        int chirpMinusOne = chirpQ16 - 65536;
        for (int i = 0; i < d - 1; i++)
        {
            ar[i] = Smulww(chirpQ16, ar[i]);
            chirpQ16 += RShiftRound(unchecked(chirpQ16 * chirpMinusOne), 16);
        }
        ar[d - 1] = Smulww(chirpQ16, ar[d - 1]);
    }

    /// <summary>Inverse of the prediction gain of an LPC filter (Q30), or 0 when it is unstable.</summary>
    public static int LpcInversePredGain(ReadOnlySpan<short> aQ12, int order)
    {
        const int Qa = 24;
        int limit = (int)(0.99975 * (1L << Qa) + 0.5);
        Span<int> buffer = stackalloc int[32];
        int newRow = (order & 1) * 16;
        int dcResponse = 0;
        for (int k = 0; k < order; k++)
        {
            dcResponse += aQ12[k];
            buffer[newRow + k] = aQ12[k] << (Qa - 12);
        }
        if (dcResponse >= 4096) return 0;
        int invGain = 1 << 30;
        for (int k = order - 1; k > 0; k--)
        {
            if (buffer[newRow + k] > limit || buffer[newRow + k] < -limit) return 0;
            int rc = -(buffer[newRow + k] << (31 - Qa));
            int rcMult1 = (1 << 30) - Smmul(rc, rc);
            int mult2Q = 32 - Clz32(Abs(rcMult1));
            int rcMult2 = Inverse32VarQ(rcMult1, mult2Q + 30);
            invGain = Smmul(invGain, rcMult1) << 2;
            int oldRow = newRow;
            newRow = (k & 1) * 16;
            for (int n = 0; n < k; n++)
            {
                // Saturating, and refusing results that do not fit in 32 bits (RFC 8251 section 6).
                long diff = (long)buffer[oldRow + n] - (int)RShiftRound64((long)buffer[oldRow + k - n - 1] * rc, 31);
                int tmp = (int)Math.Clamp(diff, int.MinValue, int.MaxValue);
                long scaled = RShiftRound64((long)tmp * rcMult2, mult2Q);
                if (scaled > int.MaxValue || scaled < int.MinValue) return 0;
                buffer[newRow + n] = (int)scaled;
            }
        }
        if (buffer[newRow] > limit || buffer[newRow] < -limit) return 0;
        int rc0 = -(buffer[newRow] << (31 - Qa));
        int rcMult10 = (1 << 30) - Smmul(rc0, rc0);
        return Smmul(invGain, rcMult10) << 2;
    }
}
