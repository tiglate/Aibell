using Aibell.Domain.Playback;
using Aibell.Playback.Core;
using NUnit.Framework;

namespace Aibell.Playback.Miniaudio.Tests;

[TestFixture]
public sealed class DspTests
{
    private const uint Rate = 48000;

    private static float[] Sine(double frequency, int frames, double amplitude = 0.25)
    {
        float[] samples = new float[frames * 2];
        for (int i = 0; i < frames; i++) samples[i * 2] = samples[(i * 2) + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate));
        return samples;
    }

    private static double Rms(ReadOnlySpan<float> samples, int skipFrames = 0)
    {
        double sum = 0;
        int count = 0;
        for (int i = skipFrames * 2; i < samples.Length; i += 2, count++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / Math.Max(1, count));
    }

    private static int ZeroCrossings(ReadOnlySpan<float> samples)
    {
        int crossings = 0;
        for (int i = 2; i < samples.Length; i += 2)
            if (samples[i - 2] < 0 != samples[i] < 0) crossings++;
        return crossings;
    }

    private static float[] Stretch(float[] input, double rate)
    {
        TimeStretcher stretcher = new() { Rate = rate };
        List<float> output = [];
        float[] pull = new float[16384 * 2];
        for (int offset = 0; offset < input.Length; offset += 4096 * 2)
        {
            int frames = Math.Min(4096, (input.Length - offset) / 2);
            stretcher.Push(input.AsSpan(offset, frames * 2), frames);
            int produced;
            while ((produced = stretcher.Pull(pull)) > 0) output.AddRange(pull.AsSpan(0, produced * 2).ToArray());
        }
        stretcher.Flush();
        int rest;
        while ((rest = stretcher.Pull(pull)) > 0) output.AddRange(pull.AsSpan(0, rest * 2).ToArray());
        return [.. output];
    }

    [TestCase(2.0)]
    [TestCase(1.5)]
    [TestCase(0.5)]
    [TestCase(1.0)]
    public void Stretcher_ChangesTheLengthButNotThePitchOrLoudness(double rate)
    {
        float[] input = Sine(440, (int)Rate * 3);

        float[] output = Stretch(input, rate);

        double seconds = output.Length / 2.0 / Rate;
        Assert.That(seconds, Is.EqualTo(3 / rate).Within(0.05));
        double hertz = ZeroCrossings(output) / 2.0 / seconds;
        Assert.That(hertz, Is.EqualTo(440).Within(12), "pitch is preserved");
        Assert.That(Rms(output, 2000), Is.EqualTo(Rms(input)).Within(0.02), "loudness is preserved");
    }

    [Test]
    public void Stretcher_Reset_StartsAgainFromTheBeginning()
    {
        TimeStretcher stretcher = new() { Rate = 2 };
        float[] input = Sine(440, 20000);
        float[] pull = new float[16384 * 2];
        stretcher.Push(input, 20000);
        int first = stretcher.Pull(pull);
        stretcher.Reset();
        stretcher.Push(input, 20000);

        Assert.That(stretcher.Pull(pull), Is.EqualTo(first));
    }

    [Test]
    public void Equalizer_FlatAndDisabled_LeaveTheAudioUntouched()
    {
        float[] original = Sine(1000, 4800);
        float[] flat = (float[])original.Clone();
        EqualizerProcessor processor = new(Rate);
        processor.Set(new EqualizerSettings { IsEnabled = true });
        processor.Process(flat);
        float[] disabled = (float[])original.Clone();
        processor.Set(new EqualizerSettings { IsEnabled = false, Bands = [12, 12, 12, 12, 12, 12, 12, 12, 12, 12] });
        processor.Process(disabled);

        Assert.That(flat, Is.EqualTo(original).Within(1e-5));
        Assert.That(disabled, Is.EqualTo(original));
    }

    [Test]
    public void Equalizer_BoostsTheBandItIsSetTo_AndLeavesFarBandsAlone()
    {
        double[] bands = new double[EqualizerSettings.BandCount];
        bands[5] = 12;
        EqualizerProcessor processor = new(Rate);
        processor.Set(new EqualizerSettings { IsEnabled = true, Bands = bands });
        float[] atBand = Sine(1000, 9600, 0.1);
        float[] farAway = Sine(125, 9600, 0.1);
        processor.Process(atBand);
        EqualizerProcessor second = new(Rate);
        second.Set(new EqualizerSettings { IsEnabled = true, Bands = bands });
        second.Process(farAway);

        double boost = 20 * Math.Log10(Rms(atBand, 2000) / Rms(Sine(1000, 9600, 0.1), 2000));
        double leak = 20 * Math.Log10(Rms(farAway, 2000) / Rms(Sine(125, 9600, 0.1), 2000));
        Assert.That(boost, Is.EqualTo(12).Within(0.6));
        Assert.That(Math.Abs(leak), Is.LessThan(1.0));
    }

    [Test]
    public void Equalizer_CutAndPreamp_ReduceTheLevel()
    {
        double[] bands = new double[EqualizerSettings.BandCount];
        bands[7] = -12;
        EqualizerProcessor processor = new(Rate);
        processor.Set(new EqualizerSettings { IsEnabled = true, Preamp = -6, Bands = bands });
        float[] tone = Sine(4000, 9600, 0.4);

        processor.Process(tone);

        double change = 20 * Math.Log10(Rms(tone, 2000) / Rms(Sine(4000, 9600, 0.4), 2000));
        Assert.That(change, Is.EqualTo(-18).Within(1.0));
    }

    [Test]
    public void Equalizer_NeverClipsBeyondFullScale()
    {
        double[] bands = Enumerable.Repeat(12.0, EqualizerSettings.BandCount).ToArray();
        EqualizerProcessor processor = new(Rate);
        processor.Set(new EqualizerSettings { IsEnabled = true, Preamp = 8, Bands = bands });
        float[] loud = Sine(1000, 4800, 0.9);

        processor.Process(loud);

        Assert.That(loud.Max(Math.Abs), Is.LessThanOrEqualTo(1f));
    }
}
