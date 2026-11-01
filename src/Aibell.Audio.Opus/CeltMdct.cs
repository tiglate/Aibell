// This file is derived from the Opus reference implementation published in RFC 6716 (celt/mdct.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
using Aibell.Audio;

namespace Aibell.Audio.Opus;

/// <summary>The inverse MDCT of the CELT layer with its low-overlap window, computed with a quarter-size complex FFT.</summary>
internal sealed class CeltMdct
{
    private const int BaseSize = 2 * CeltMode.ShortMdctSize << CeltMode.MaxLm; // 1920
    private readonly ComplexFft[] ffts = new ComplexFft[CeltMode.MaxLm + 1];
    private readonly float[] trig = new float[BaseSize / 4 + 1];
    private readonly float[] f = new float[BaseSize / 2];
    private readonly float[] f2 = new float[BaseSize / 2];

    public CeltMdct()
    {
        for (int i = 0; i <= CeltMode.MaxLm; i++) ffts[i] = new ComplexFft(BaseSize / 4 >> i);
        for (int i = 0; i <= BaseSize / 4; i++) trig[i] = (float)Math.Cos(2 * 3.141592653589793 * i / BaseSize);
    }

    /// <summary>
    /// Transforms the coefficients <c>input[inputOffset + stride * k]</c> into the windowed output <c>output[outputOffset ..]</c>, adding into the overlap region
    /// (the caller clears it). <paramref name="shift"/> selects the transform size (BaseSize &gt;&gt; shift).
    /// </summary>
    public void Backward(float[] input, int inputOffset, int stride, float[] output, int outputOffset, int shift, ReadOnlySpan<float> window, int overlap)
    {
        int n = BaseSize >> shift;
        int n2 = n >> 1;
        int n4 = n >> 2;
        float sine = (float)(2 * 3.141592653589793 * .125 / n);

        int xp1 = inputOffset;
        int xp2 = inputOffset + stride * (n2 - 1);
        for (int i = 0; i < n4; i++)
        {
            float t0 = trig[i << shift], t1 = trig[(n4 - i) << shift];
            float yr = -input[xp2] * t0 + input[xp1] * t1;
            float yi = -input[xp2] * t1 - input[xp1] * t0;
            f2[2 * i] = yr - yi * sine;
            f2[2 * i + 1] = yi + yr * sine;
            xp1 += 2 * stride;
            xp2 -= 2 * stride;
        }

        // Inverse FFT (no downscaling), as the conjugate of a forward FFT of the conjugate.
        for (int i = 0; i < n4; i++) f2[2 * i + 1] = -f2[2 * i + 1];
        ffts[shift].Transform(f2, f);
        for (int i = 0; i < n4; i++) f[2 * i + 1] = -f[2 * i + 1];

        for (int i = 0; i < n4; i++)
        {
            float re = f[2 * i], im = f[2 * i + 1];
            float t0 = trig[i << shift], t1 = trig[(n4 - i) << shift];
            float yr = re * t0 - im * t1;
            float yi = im * t0 + re * t1;
            f[2 * i] = yr - yi * sine;
            f[2 * i + 1] = yi + yr * sine;
        }

        {
            int fp1 = 0, fp2 = n2 - 1;
            for (int i = 0; i < n4; i++)
            {
                f2[2 * i] = -f[fp1];
                f2[2 * i + 1] = f[fp2];
                fp1 += 2;
                fp2 -= 2;
            }
        }

        int o = outputOffset - ((n2 - overlap) >> 1);
        {
            int fp1 = n4 - 1, xo = o + n2 - 1, yo = o + n4 - overlap / 2, w1 = 0, w2 = overlap - 1;
            int i = 0;
            for (; i < n4 - overlap / 2; i++) output[xo--] = f2[fp1--];
            for (; i < n4; i++)
            {
                float x1 = f2[fp1--];
                output[yo++] += -window[w1] * x1;
                output[xo--] += window[w2] * x1;
                w1++;
                w2--;
            }
        }
        {
            int fp2 = n4, xo = o + n2, yo = o + n - 1 - (n4 - overlap / 2), w1 = 0, w2 = overlap - 1;
            int i = 0;
            for (; i < n4 - overlap / 2; i++) output[xo++] = f2[fp2++];
            for (; i < n4; i++)
            {
                float x2 = f2[fp2++];
                output[yo--] = window[w1] * x2;
                output[xo++] = window[w2] * x2;
                w1++;
                w2--;
            }
        }
    }
}
