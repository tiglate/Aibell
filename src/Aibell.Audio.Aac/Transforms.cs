namespace Aibell.Audio.Aac;

/// <summary>Window shapes of the filterbank (ISO/IEC 14496-3 subclause 4.6.11.3): sine and Kaiser-Bessel-derived.</summary>
internal static class Windows
{
    /// <summary>Window of <paramref name="length"/> samples (2048 for long, 256 for short blocks); the first half rises, the second falls.</summary>
    public static float[] Sine(int length)
    {
        float[] window = new float[length];
        for (int n = 0; n < length; n++) window[n] = (float)Math.Sin(Math.PI * (n + 0.5) / length);
        return window;
    }

    /// <summary>KBD window with the given Kaiser alpha (4 for long, 6 for short windows).</summary>
    public static float[] KaiserBesselDerived(int length, double alpha)
    {
        int half = length / 2;
        double[] kaiser = new double[half + 1];
        double sum = 0;
        for (int n = 0; n <= half; n++)
        {
            double ratio = 2.0 * n / half - 1.0;
            kaiser[n] = BesselI0(Math.PI * alpha * Math.Sqrt(Math.Max(0, 1 - ratio * ratio)));
            sum += kaiser[n];
        }
        float[] window = new float[length];
        double running = 0;
        for (int n = 0; n < half; n++)
        {
            running += kaiser[n];
            window[n] = (float)Math.Sqrt(running / sum);
            window[length - 1 - n] = window[n];
        }
        return window;
    }

    private static double BesselI0(double x)
    {
        double term = 1, sum = 1;
        for (int k = 1; k < 60; k++)
        {
            term *= x / (2 * k) * (x / (2 * k));
            sum += term;
            if (term < sum * 1e-18) break;
        }
        return sum;
    }
}

/// <summary>
/// IMDCT of M coefficients into 2M samples, computed with a DCT-IV that runs on a complex FFT of size M/2. Unscaled:
/// x[n] = sum_k X[k] cos(pi/M (n + n0)(k + 1/2)) with n0 = (M + 1) / 2. No allocation after construction; not thread safe.
/// </summary>
internal sealed class Imdct
{
    private readonly int m;
    private readonly float[] twiddleRe;
    private readonly float[] twiddleIm;
    private readonly float[] postRe;
    private readonly float[] postIm;
    private readonly float[] fftCos;
    private readonly float[] fftSin;
    private readonly int[] bitReverse;
    private readonly float[] re;
    private readonly float[] im;
    private readonly float[] dct;

    public Imdct(int coefficients)
    {
        m = coefficients;
        int n = m / 2;
        twiddleRe = new float[n];
        twiddleIm = new float[n];
        postRe = new float[n];
        postIm = new float[n];
        for (int j = 0; j < n; j++)
        {
            double angle = -Math.PI * (4 * j + 1) / (4.0 * m);
            twiddleRe[j] = (float)Math.Cos(angle);
            twiddleIm[j] = (float)Math.Sin(angle);
            postRe[j] = (float)Math.Cos(-Math.PI * j / m);
            postIm[j] = (float)Math.Sin(-Math.PI * j / m);
        }
        fftCos = new float[n / 2];
        fftSin = new float[n / 2];
        for (int j = 0; j < n / 2; j++)
        {
            fftCos[j] = (float)Math.Cos(-2 * Math.PI * j / n);
            fftSin[j] = (float)Math.Sin(-2 * Math.PI * j / n);
        }
        bitReverse = new int[n];
        int bits = System.Numerics.BitOperations.Log2((uint)n);
        for (int j = 0; j < n; j++)
        {
            int reversed = 0;
            for (int b = 0; b < bits; b++) reversed |= (j >> b & 1) << (bits - 1 - b);
            bitReverse[j] = reversed;
        }
        re = new float[n];
        im = new float[n];
        dct = new float[m];
    }

    /// <summary>Type-IV DCT, u[j] = sum_k X[k] cos(pi/M (j + 1/2)(k + 1/2)).</summary>
    public void Dct4(ReadOnlySpan<float> input, Span<float> output)
    {
        int n = m / 2;
        for (int j = 0; j < n; j++)
        {
            float a = input[2 * j], b = input[m - 1 - 2 * j];
            int target = bitReverse[j];
            re[target] = a * twiddleRe[j] - b * twiddleIm[j];
            im[target] = a * twiddleIm[j] + b * twiddleRe[j];
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1, step = n / size;
            for (int start = 0; start < n; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    float c = fftCos[k * step], s = fftSin[k * step];
                    int p = start + k, q = p + half;
                    float tr = re[q] * c - im[q] * s, ti = re[q] * s + im[q] * c;
                    re[q] = re[p] - tr;
                    im[q] = im[p] - ti;
                    re[p] += tr;
                    im[p] += ti;
                }
            }
        }
        for (int j = 0; j < n; j++)
        {
            float yr = re[j] * postRe[j] - im[j] * postIm[j];
            float yi = re[j] * postIm[j] + im[j] * postRe[j];
            output[2 * j] = yr;
            output[m - 1 - 2 * j] = -yi;
        }
    }

    /// <summary>Transforms <paramref name="spectrum"/> (M values) into <paramref name="output"/> (2M samples) multiplied by <paramref name="scale"/>.</summary>
    public void Transform(ReadOnlySpan<float> spectrum, Span<float> output, float scale)
    {
        Dct4(spectrum, dct);
        int half = m / 2;
        for (int i = 0; i < half; i++) output[i] = dct[half + i] * scale;                    // n in [0, M/2): u[n + M/2]
        for (int i = half; i < 3 * half; i++) output[i] = -dct[3 * half - 1 - i] * scale;   // n in [M/2, 3M/2): -u[3M/2 - 1 - n]
        for (int i = 3 * half; i < 4 * half; i++) output[i] = -dct[i - 3 * half] * scale;   // n in [3M/2, 2M): -u[n - 3M/2]
    }
}
