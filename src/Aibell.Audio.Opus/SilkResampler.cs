// This file is derived from the Opus reference implementation published in RFC 6716 (silk/*.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
using static Aibell.Audio.Opus.SilkMath;

namespace Aibell.Audio.Opus;

/// <summary>The decoder-side resampler from the SILK internal rate (8, 12 or 16 kHz) to 48 kHz.</summary>
internal sealed class SilkResampler
{
    private const int OrderFir = 8;
    private const int MaxBatchMs = 10;

    private readonly int[] sIir = new int[6];
    private readonly short[] sFir = new short[OrderFir];
    private readonly short[] delayBuf = new short[48];
    private readonly short[] buf = new short[MaxBatchMs * 48 + OrderFir + 16];
    private int fsInKHz;
    private int fsOutKHz;
    private int batchSize;
    private int inputDelay;
    private int invRatioQ16;

    public void Init(int fsHzIn, int fsHzOut)
    {
        if (fsHzIn is not (8000 or 12000 or 16000) || fsHzOut != 48000) throw new InvalidOperationException("Unsupported SILK resampling ratio.");
        Array.Clear(sIir);
        Array.Clear(sFir);
        Array.Clear(delayBuf);
        inputDelay = fsHzIn switch { 8000 => 0, 12000 => 4, _ => 7 };
        fsInKHz = fsHzIn / 1000;
        fsOutKHz = fsHzOut / 1000;
        batchSize = fsInKHz * MaxBatchMs;
        const int up2x = 1;
        invRatioQ16 = ((fsHzIn << (14 + up2x)) / fsHzOut) << 2;
        while (Smulww(invRatioQ16, fsHzOut) < (fsHzIn << up2x)) invRatioQ16++;
    }

    public void CopyStateFrom(SilkResampler other)
    {
        Array.Copy(other.sIir, sIir, sIir.Length);
        Array.Copy(other.sFir, sFir, sFir.Length);
        Array.Copy(other.delayBuf, delayBuf, delayBuf.Length);
        fsInKHz = other.fsInKHz;
        fsOutKHz = other.fsOutKHz;
        batchSize = other.batchSize;
        inputDelay = other.inputDelay;
        invRatioQ16 = other.invRatioQ16;
    }

    /// <summary>Resamples <paramref name="inLen"/> (at least 1 ms) samples; the output holds inLen * fsOut / fsIn samples.</summary>
    public void Process(Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        int nSamples = fsInKHz - inputDelay;
        input[..nSamples].CopyTo(delayBuf.AsSpan(inputDelay));
        IirFir(output, delayBuf, fsInKHz);
        IirFir(output[fsOutKHz..], input[nSamples..], inLen - fsInKHz);
        input.Slice(inLen - inputDelay, inputDelay).CopyTo(delayBuf);
    }

    private static void Up2Hq(Span<int> s, Span<short> output, ReadOnlySpan<short> input, int len)
    {
        ReadOnlySpan<short> c0 = SilkTables.ResamplerUp2Hq0, c1 = SilkTables.ResamplerUp2Hq1;
        for (int k = 0; k < len; k++)
        {
            int in32 = input[k] << 10;
            int y = in32 - s[0];
            int x = Smulwb(y, c0[0]);
            int out1 = s[0] + x;
            s[0] = in32 + x;
            y = out1 - s[1];
            x = Smulwb(y, c0[1]);
            int out2 = s[1] + x;
            s[1] = out1 + x;
            y = out2 - s[2];
            x = Smlawb(y, y, c0[2]);
            out1 = s[2] + x;
            s[2] = out2 + x;
            output[2 * k] = (short)Sat16(RShiftRound(out1, 10));

            y = in32 - s[3];
            x = Smulwb(y, c1[0]);
            out1 = s[3] + x;
            s[3] = in32 + x;
            y = out1 - s[4];
            x = Smulwb(y, c1[1]);
            out2 = s[4] + x;
            s[4] = out1 + x;
            y = out2 - s[5];
            x = Smlawb(y, y, c1[2]);
            out1 = s[5] + x;
            s[5] = out2 + x;
            output[2 * k + 1] = (short)Sat16(RShiftRound(out1, 10));
        }
    }

    private void IirFir(Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        ReadOnlySpan<short> frac = SilkTables.ResamplerFrac12;
        Span<short> b = buf;
        sFir.CopyTo(b);
        int outPos = 0, inPos = 0, n;
        while (true)
        {
            n = Math.Min(inLen, batchSize);
            Up2Hq(sIir, b[OrderFir..], input[inPos..], n);
            int maxIndexQ16 = n << 17;
            for (int indexQ16 = 0; indexQ16 < maxIndexQ16; indexQ16 += invRatioQ16)
            {
                int tableIndex = Smulwb(indexQ16 & 0xFFFF, 12);
                ReadOnlySpan<short> p = b.Slice(indexQ16 >> 16, 8);
                int a = tableIndex * 4, z = (11 - tableIndex) * 4;
                int res = Smulbb(p[0], frac[a]);
                res = Smlabb(res, p[1], frac[a + 1]);
                res = Smlabb(res, p[2], frac[a + 2]);
                res = Smlabb(res, p[3], frac[a + 3]);
                res = Smlabb(res, p[4], frac[z + 3]);
                res = Smlabb(res, p[5], frac[z + 2]);
                res = Smlabb(res, p[6], frac[z + 1]);
                res = Smlabb(res, p[7], frac[z]);
                output[outPos++] = (short)Sat16(RShiftRound(res, 15));
            }
            inPos += n;
            inLen -= n;
            if (inLen > 0) b.Slice(n << 1, OrderFir).CopyTo(b);
            else break;
        }
        b.Slice(n << 1, OrderFir).CopyTo(sFir);
    }
}
