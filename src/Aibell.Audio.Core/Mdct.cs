namespace Aibell.Audio;

/// <summary>
/// Type-IV DCT, u[j] = sum_k X[k] cos(pi/M (j + 1/2)(k + 1/2)) for j, k in 0..M-1, computed with a complex FFT of size M/2 and pre/post rotations.
/// M must be even. A transform allocates nothing; the instance owns scratch space, so use one per thread.
/// </summary>
public sealed class Dct4
{
    private readonly int m;
    private readonly ComplexFft fft;
    private readonly float[] pre;
    private readonly float[] post;
    private readonly float[] work;
    private readonly float[] spectrum;

    public Dct4(int size)
    {
        if (size < 2 || (size & 1) != 0) throw new ArgumentOutOfRangeException(nameof(size));
        m = size;
        int n = m / 2;
        fft = new ComplexFft(n);
        pre = new float[2 * n];
        post = new float[2 * n];
        for (int j = 0; j < n; j++)
        {
            double a = -Math.PI * (4 * j + 1) / (4.0 * m);
            pre[2 * j] = (float)Math.Cos(a);
            pre[2 * j + 1] = (float)Math.Sin(a);
            double b = -Math.PI * j / m;
            post[2 * j] = (float)Math.Cos(b);
            post[2 * j + 1] = (float)Math.Sin(b);
        }
        work = new float[2 * n];
        spectrum = new float[2 * n];
    }

    public int Size => m;

    public void Transform(ReadOnlySpan<float> input, Span<float> output)
    {
        int n = m / 2;
        for (int j = 0; j < n; j++)
        {
            float a = input[2 * j], b = input[m - 1 - 2 * j];
            work[2 * j] = a * pre[2 * j] - b * pre[2 * j + 1];
            work[2 * j + 1] = a * pre[2 * j + 1] + b * pre[2 * j];
        }
        fft.Transform(work, spectrum);
        for (int j = 0; j < n; j++)
        {
            float re = spectrum[2 * j], im = spectrum[2 * j + 1];
            float yr = re * post[2 * j] - im * post[2 * j + 1];
            float yi = re * post[2 * j + 1] + im * post[2 * j];
            output[2 * j] = yr;
            output[m - 1 - 2 * j] = -yi;
        }
    }
}

/// <summary>
/// Inverse MDCT of M coefficients into 2M samples, x[n] = scale * sum_k X[k] cos(pi/M (n + 1/2 + M/2)(k + 1/2)), through a DCT-IV. The scaling
/// is up to the caller. Allocates nothing after construction.
/// </summary>
public sealed class Imdct
{
    private readonly int m;
    private readonly Dct4 dct;
    private readonly float[] u;

    public Imdct(int coefficients)
    {
        m = coefficients;
        dct = new Dct4(coefficients);
        u = new float[coefficients];
    }

    public int Coefficients => m;

    public void Transform(ReadOnlySpan<float> spectrum, Span<float> output, float scale)
    {
        dct.Transform(spectrum, u);
        int half = m / 2;
        for (int i = 0; i < half; i++) output[i] = u[half + i] * scale;
        for (int i = half; i < 3 * half; i++) output[i] = -u[3 * half - 1 - i] * scale;
        for (int i = 3 * half; i < 4 * half; i++) output[i] = -u[i - 3 * half] * scale;
    }
}
