using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

[TestFixture]
public sealed class TransformTests
{
    [TestCase(2048)]
    [TestCase(256)]
    public void SineWindow_SatisfiesPrincenBradley(int length) => AssertPrincenBradley(Windows.Sine(length));

    [TestCase(2048, 4.0)]
    [TestCase(256, 6.0)]
    public void KaiserBesselDerivedWindow_SatisfiesPrincenBradleyAndIsSymmetric(int length, double alpha)
    {
        float[] window = Windows.KaiserBesselDerived(length, alpha);
        AssertPrincenBradley(window);
        for (int n = 0; n < length; n++) Assert.That(window[n], Is.EqualTo(window[length - 1 - n]).Within(1e-7));
    }

    private static void AssertPrincenBradley(float[] window)
    {
        int half = window.Length / 2;
        for (int n = 0; n < half; n++)
            Assert.That(window[n] * window[n] + window[n + half] * window[n + half], Is.EqualTo(1.0).Within(1e-5), $"n={n}");
    }

    [TestCase(128)]
    [TestCase(1024)]
    public void Imdct_MatchesDirectComputation(int coefficients)
    {
        Random random = new(7);
        float[] spectrum = new float[coefficients];
        for (int i = 0; i < coefficients; i++) spectrum[i] = (float)(random.NextDouble() * 2 - 1);
        float[] actual = new float[2 * coefficients];
        new Imdct(coefficients).Transform(spectrum, actual, 1f);

        double n0 = (coefficients + 1) / 2.0;
        for (int n = 0; n < 2 * coefficients; n++)
        {
            double sum = 0;
            for (int k = 0; k < coefficients; k++) sum += spectrum[k] * Math.Cos(Math.PI / coefficients * (n + n0) * (k + 0.5));
            Assert.That(actual[n], Is.EqualTo(sum).Within(2e-3), $"n={n}");
        }
    }
}
