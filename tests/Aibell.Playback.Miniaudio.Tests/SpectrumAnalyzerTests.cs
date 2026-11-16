using Aibell.Playback.Core;
using NUnit.Framework;

namespace Aibell.Playback.Miniaudio.Tests;

[TestFixture]
public sealed class SpectrumAnalyzerTests
{
    private const uint Rate = 48000;

    private static float[] Sine(double frequency, int frames)
    {
        float[] samples = new float[frames * 2];
        for (int i = 0; i < frames; i++) samples[i * 2] = samples[(i * 2) + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / Rate));
        return samples;
    }

    [Test]
    public void Read_SineWave_PeaksInTheBandHoldingItsFrequency()
    {
        SpectrumAnalyzer analyzer = new() { SampleRate = Rate, Enabled = true };
        analyzer.Write(Sine(1000, 4096));
        float[] bands = new float[32];

        Assert.That(analyzer.Read(bands), Is.True);

        int loudest = Array.IndexOf(bands, bands.Max());
        // 1 kHz on a log scale from 40 Hz to 16 kHz is about 60% of the way up.
        Assert.That(loudest, Is.InRange(16, 22));
        Assert.That(bands[loudest], Is.GreaterThan(0.6f));
        Assert.That(bands[0], Is.LessThan(0.2f));
    }

    [Test]
    public void Write_WhileDisabled_RecordsNothing()
    {
        SpectrumAnalyzer analyzer = new() { SampleRate = Rate };
        analyzer.Write(Sine(1000, 4096));
        float[] bands = new float[16];

        Assert.That(analyzer.Read(bands), Is.False);
        Assert.That(bands, Is.All.EqualTo(0f));
    }

    [Test]
    public void Read_AfterAudioStops_ReportsNoSignal()
    {
        SpectrumAnalyzer analyzer = new() { SampleRate = Rate, Enabled = true };
        analyzer.Write(Sine(1000, 4096));
        Thread.Sleep(350);

        Assert.That(analyzer.Read(new float[16]), Is.False);
    }
}
