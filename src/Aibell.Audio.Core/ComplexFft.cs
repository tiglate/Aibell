using System.Runtime.CompilerServices;

namespace Aibell.Audio;

/// <summary>
/// Forward complex FFT of any size, as a mixed-radix decimation-in-time transform (radices 4, 2, 3 and 5 have dedicated butterflies; any other prime
/// uses a direct DFT stage). Data is interleaved (re, im) floats. Twiddles are precomputed in double precision; a transform allocates nothing.
/// Not thread safe only in that the instance holds no scratch: one instance may be shared by readers that never run the same call concurrently
/// on the same buffers.
/// </summary>
public sealed class ComplexFft
{
    private const float Sin60 = 0.8660254037844386f;
    private const float Cos72 = 0.30901699437494745f;
    private const float Sin72 = 0.9510565162951535f;
    private const float Cos144 = -0.8090169943749475f;
    private const float Sin144 = 0.5877852522924731f;

    private readonly int size;
    private readonly float[] twiddle;
    private readonly int[] radices;

    public ComplexFft(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        this.size = size;
        twiddle = new float[2 * size];
        for (int k = 0; k < size; k++)
        {
            double angle = -2.0 * Math.PI * k / size;
            twiddle[2 * k] = (float)Math.Cos(angle);
            twiddle[2 * k + 1] = (float)Math.Sin(angle);
        }
        List<int> factors = [];
        int rest = size;
        while (rest > 1)
        {
            int radix = rest % 4 == 0 ? 4 : rest % 2 == 0 ? 2 : rest % 3 == 0 ? 3 : rest % 5 == 0 ? 5 : SmallestFactor(rest);
            factors.Add(radix);
            rest /= radix;
        }
        radices = [.. factors];
    }

    public int Size => size;

    /// <summary>Transforms <paramref name="input"/> (size complex values) into <paramref name="output"/>; the two must not overlap.</summary>
    public void Transform(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length < 2 * size || output.Length < 2 * size) throw new ArgumentException("The buffers are too small for the transform.");
        Work(input, 0, 1, output, 0, size, 0);
    }

    private static int SmallestFactor(int n)
    {
        for (int f = 7; f * f <= n; f += 2)
        {
            if (n % f == 0) return f;
        }
        return n;
    }

    private void Work(ReadOnlySpan<float> input, int inputOffset, int inputStride, Span<float> output, int outputOffset, int length, int level)
    {
        if (length == 1)
        {
            output[2 * outputOffset] = input[2 * inputOffset];
            output[2 * outputOffset + 1] = input[2 * inputOffset + 1];
            return;
        }
        int radix = radices[level];
        int sub = length / radix;
        for (int q = 0; q < radix; q++) Work(input, inputOffset + q * inputStride, inputStride * radix, output, outputOffset + q * sub, sub, level + 1);
        int step = size / length;
        switch (radix)
        {
            case 2: Radix2(output, outputOffset, sub, step); break;
            case 3: Radix3(output, outputOffset, sub, step); break;
            case 4: Radix4(output, outputOffset, sub, step); break;
            case 5: Radix5(output, outputOffset, sub, step); break;
            default: Generic(output, outputOffset, sub, step, radix); break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Twiddled(ReadOnlySpan<float> data, int index, int exponent, out float re, out float im)
    {
        float c = twiddle[2 * exponent], s = twiddle[2 * exponent + 1];
        float xr = data[2 * index], xi = data[2 * index + 1];
        re = xr * c - xi * s;
        im = xr * s + xi * c;
    }

    private void Radix2(Span<float> d, int offset, int m, int step)
    {
        for (int k = 0; k < m; k++)
        {
            int i0 = offset + k, i1 = i0 + m;
            Twiddled(d, i1, k * step, out float br, out float bi);
            float ar = d[2 * i0], ai = d[2 * i0 + 1];
            d[2 * i0] = ar + br;
            d[2 * i0 + 1] = ai + bi;
            d[2 * i1] = ar - br;
            d[2 * i1 + 1] = ai - bi;
        }
    }

    private void Radix4(Span<float> d, int offset, int m, int step)
    {
        for (int k = 0; k < m; k++)
        {
            int i0 = offset + k, i1 = i0 + m, i2 = i1 + m, i3 = i2 + m;
            float ar = d[2 * i0], ai = d[2 * i0 + 1];
            Twiddled(d, i1, k * step, out float br, out float bi);
            Twiddled(d, i2, 2 * k * step, out float cr, out float ci);
            Twiddled(d, i3, 3 * k * step, out float dr, out float di);
            float s0r = ar + cr, s0i = ai + ci, s1r = ar - cr, s1i = ai - ci;
            float s2r = br + dr, s2i = bi + di, s3r = br - dr, s3i = bi - di;
            d[2 * i0] = s0r + s2r;
            d[2 * i0 + 1] = s0i + s2i;
            d[2 * i2] = s0r - s2r;
            d[2 * i2 + 1] = s0i - s2i;
            // multiplication of s3 by -i for the forward transform
            d[2 * i1] = s1r + s3i;
            d[2 * i1 + 1] = s1i - s3r;
            d[2 * i3] = s1r - s3i;
            d[2 * i3 + 1] = s1i + s3r;
        }
    }

    private void Radix3(Span<float> d, int offset, int m, int step)
    {
        for (int k = 0; k < m; k++)
        {
            int i0 = offset + k, i1 = i0 + m, i2 = i1 + m;
            float ar = d[2 * i0], ai = d[2 * i0 + 1];
            Twiddled(d, i1, k * step, out float br, out float bi);
            Twiddled(d, i2, 2 * k * step, out float cr, out float ci);
            float sr = br + cr, si = bi + ci;
            float dr = br - cr, di = bi - ci;
            float mr = ar - 0.5f * sr, mi = ai - 0.5f * si;
            d[2 * i0] = ar + sr;
            d[2 * i0 + 1] = ai + si;
            d[2 * i1] = mr + Sin60 * di;
            d[2 * i1 + 1] = mi - Sin60 * dr;
            d[2 * i2] = mr - Sin60 * di;
            d[2 * i2 + 1] = mi + Sin60 * dr;
        }
    }

    private void Radix5(Span<float> d, int offset, int m, int step)
    {
        for (int k = 0; k < m; k++)
        {
            int i0 = offset + k, i1 = i0 + m, i2 = i1 + m, i3 = i2 + m, i4 = i3 + m;
            float ar = d[2 * i0], ai = d[2 * i0 + 1];
            Twiddled(d, i1, k * step, out float br, out float bi);
            Twiddled(d, i2, 2 * k * step, out float cr, out float ci);
            Twiddled(d, i3, 3 * k * step, out float er, out float ei);
            Twiddled(d, i4, 4 * k * step, out float fr, out float fi);
            float s14r = br + fr, s14i = bi + fi, d14r = br - fr, d14i = bi - fi;
            float s23r = cr + er, s23i = ci + ei, d23r = cr - er, d23i = ci - ei;
            d[2 * i0] = ar + s14r + s23r;
            d[2 * i0 + 1] = ai + s14i + s23i;
            float m1r = ar + Cos72 * s14r + Cos144 * s23r, m1i = ai + Cos72 * s14i + Cos144 * s23i;
            float m2r = ar + Cos144 * s14r + Cos72 * s23r, m2i = ai + Cos144 * s14i + Cos72 * s23i;
            float n1r = Sin72 * d14r + Sin144 * d23r, n1i = Sin72 * d14i + Sin144 * d23i;
            float n2r = Sin144 * d14r - Sin72 * d23r, n2i = Sin144 * d14i - Sin72 * d23i;
            d[2 * i1] = m1r + n1i;
            d[2 * i1 + 1] = m1i - n1r;
            d[2 * i4] = m1r - n1i;
            d[2 * i4 + 1] = m1i + n1r;
            d[2 * i2] = m2r + n2i;
            d[2 * i2 + 1] = m2i - n2r;
            d[2 * i3] = m2r - n2i;
            d[2 * i3 + 1] = m2i + n2r;
        }
    }

    private void Generic(Span<float> d, int offset, int m, int step, int radix)
    {
        Span<float> t = radix <= 64 ? stackalloc float[2 * radix] : new float[2 * radix];
        int stride = size / radix;
        for (int k = 0; k < m; k++)
        {
            for (int q = 0; q < radix; q++)
            {
                Twiddled(d, offset + k + q * m, q * k * step, out float re, out float im);
                t[2 * q] = re;
                t[2 * q + 1] = im;
            }
            for (int s = 0; s < radix; s++)
            {
                float sr = 0, si = 0;
                for (int q = 0; q < radix; q++)
                {
                    int e = (int)((long)q * s % radix) * stride;
                    float c = twiddle[2 * e], sn = twiddle[2 * e + 1];
                    sr += t[2 * q] * c - t[2 * q + 1] * sn;
                    si += t[2 * q] * sn + t[2 * q + 1] * c;
                }
                d[2 * (offset + k + s * m)] = sr;
                d[2 * (offset + k + s * m) + 1] = si;
            }
        }
    }
}
