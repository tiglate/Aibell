namespace Aibell.Audio.Core.Tests;

public class TransformTests
{
    private static float[] RandomData(int count, int seed)
    {
        Random random = new(seed);
        float[] data = new float[count];
        for (int i = 0; i < count; i++) data[i] = (float)(random.NextDouble() * 2 - 1);
        return data;
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(12)]
    [TestCase(15)]
    [TestCase(30)]
    [TestCase(45)]
    [TestCase(60)]
    [TestCase(64)]
    [TestCase(77)]
    [TestCase(120)]
    [TestCase(240)]
    [TestCase(480)]
    [TestCase(1000)]
    [TestCase(1024)]
    [TestCase(4096)]
    public void Fft_MatchesTheDirectDft(int n)
    {
        float[] input = RandomData(2 * n, n);
        float[] output = new float[2 * n];
        new ComplexFft(n).Transform(input, output);
        double worst = 0;
        for (int k = 0; k < n; k++)
        {
            double re = 0, im = 0;
            for (int j = 0; j < n; j++)
            {
                double angle = -2 * Math.PI * ((long)j * k % n) / n;
                re += input[2 * j] * Math.Cos(angle) - input[2 * j + 1] * Math.Sin(angle);
                im += input[2 * j] * Math.Sin(angle) + input[2 * j + 1] * Math.Cos(angle);
            }
            worst = Math.Max(worst, Math.Max(Math.Abs(re - output[2 * k]), Math.Abs(im - output[2 * k + 1])));
        }
        Assert.That(worst, Is.LessThan(1e-4 * Math.Max(1, Math.Sqrt(n))));
    }

    [TestCase(2)]
    [TestCase(4)]
    [TestCase(6)]
    [TestCase(18)]
    [TestCase(32)]
    [TestCase(120)]
    [TestCase(240)]
    [TestCase(960)]
    [TestCase(1024)]
    public void Dct4_MatchesTheDefinition(int m)
    {
        float[] x = RandomData(m, m);
        float[] u = new float[m];
        new Dct4(m).Transform(x, u);
        for (int j = 0; j < m; j++)
        {
            double sum = 0;
            for (int k = 0; k < m; k++) sum += x[k] * Math.Cos(Math.PI / m * (j + 0.5) * (k + 0.5));
            Assert.That(u[j], Is.EqualTo(sum).Within(1e-3), $"j={j}");
        }
    }

    [TestCase(4)]
    [TestCase(12)]
    [TestCase(32)]
    [TestCase(120)]
    [TestCase(256)]
    [TestCase(2048)]
    public void Imdct_MatchesTheDefinition(int m)
    {
        float[] x = RandomData(m, m + 1);
        float[] y = new float[2 * m];
        new Imdct(m).Transform(x, y, 0.5f);
        for (int n = 0; n < 2 * m; n += Math.Max(1, m / 64))
        {
            double sum = 0;
            for (int k = 0; k < m; k++) sum += x[k] * Math.Cos(Math.PI / m * (n + 0.5 + m / 2.0) * (k + 0.5));
            Assert.That(y[n], Is.EqualTo(0.5 * sum).Within(2e-3), $"n={n}");
        }
    }

    [Test]
    public void Transforms_AllocateNothing()
    {
        Imdct imdct = new(1024);
        float[] x = RandomData(1024, 1), y = new float[2048];
        imdct.Transform(x, y, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) imdct.Transform(x, y, 1);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
