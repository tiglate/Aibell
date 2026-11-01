using System.IO.Compression;
using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

/// <summary>
/// Decodes small AAC-LC files made by FFmpeg's encoder (see tests/fixtures/generate-aac.sh) and compares the PCM with FFmpeg's own decode
/// of the same bytes. The reference is a development-time oracle only; the fixtures are synthetic signals generated for this repository.
/// </summary>
[TestFixture]
public sealed class ConformanceTests
{
    internal static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "aac", name);

    /// <summary>Decodes a fixture to interleaved samples in 16-bit scale. Also returns which coding tools the stream used.</summary>
    internal static (float[] Samples, int Channels, AacStatistics Statistics) Decode(string name, Action<AacDecoder>? configure = null)
    {
        byte[] data = File.ReadAllBytes(FixturePath(name + ".aac"));
        AdtsFrameReader reader = new();
        AacDecoder decoder = new();
        configure?.Invoke(decoder);
        List<float> samples = [];
        int channels = 0;
        data.CopyTo(reader.GetWriteBuffer());
        reader.Advance(data.Length);
        reader.Complete();
        while (reader.TryRead(out AdtsHeader header, out ReadOnlySpan<byte> payload))
        {
            decoder.Decode(header, payload);
            channels = decoder.Channels;
            for (int n = 0; n < AacDecoder.FrameLength; n++)
            {
                for (int c = 0; c < channels; c++) samples.Add(decoder.GetChannel(c)[n] * 32768f);
            }
        }
        return ([.. samples], channels, decoder.Statistics);
    }

    internal static short[] Reference(string name)
    {
        using GZipStream gzip = new(File.OpenRead(FixturePath(name + ".pcm.gz")), CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        byte[] raw = bytes.ToArray();
        short[] samples = new short[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, samples, 0, raw.Length);
        return samples;
    }

    internal static double SnrDb(float[] actual, short[] expected)
    {
        int count = Math.Min(actual.Length, expected.Length);
        double signal = 0, error = 0;
        for (int i = 0; i < count; i++)
        {
            signal += (double)expected[i] * expected[i];
            // FFmpeg's reference was clipped to 16 bits and rounded; do the same to ours before comparing.
            double d = Math.Clamp(Math.Round(actual[i]), -32768, 32767) - expected[i];
            error += d * d;
        }
        return error == 0 ? 200 : 10 * Math.Log10(signal / error);
    }

    private static IEnumerable<string> Waveform() =>
        ["long_sweep_44k_stereo", "long_noise_48k_stereo", "short_clicks_22k_stereo", "ms_22k_stereo", "is_44k_stereo"];

    [TestCaseSource(nameof(Waveform))]
    public void Decode_MatchesReferencePcm(string name)
    {
        (float[] samples, int channels, _) = Decode(name);
        short[] reference = Reference(name);
        Assert.That(channels, Is.EqualTo(2));
        Assert.That(samples.Length, Is.EqualTo(reference.Length));
        Assert.That(SnrDb(samples, reference), Is.GreaterThan(50), name);
    }

    [TestCase("long_sweep_44k_stereo")]
    [TestCase("long_noise_48k_stereo")]
    public void LongBlockFixtures_AreMostlyLongWindowsWithoutOtherTools(string name)
    {
        AacStatistics statistics = Decode(name).Statistics;
        Assert.That((statistics.TnsChannels, statistics.MidSideBands, statistics.IntensityBands, statistics.NoiseBands), Is.EqualTo((0L, 0L, 0L, 0L)));
        Assert.That(statistics.LongChannels, Is.GreaterThan(statistics.ShortChannels));
    }

    [Test]
    public void TransientFixture_UsesShortWindowsAndStartStopTransitions()
    {
        AacStatistics statistics = Decode("short_clicks_22k_stereo").Statistics;
        Assert.That(statistics.ShortChannels, Is.GreaterThan(4));
        Assert.That(statistics.StartChannels, Is.GreaterThan(0));
        Assert.That(statistics.StopChannels, Is.GreaterThan(0));
    }

    [TestCase("tns_long_44k_mono")]
    [TestCase("tns_short_44k_mono")]
    public void TnsFixtures_UseTnsAndMatchReferencePcm(string name)
    {
        (float[] samples, _, AacStatistics statistics) = Decode(name);
        Assert.That(statistics.TnsChannels, Is.GreaterThan(5));
        Assert.That(SnrDb(samples, Reference(name)), Is.GreaterThan(50), name);
    }

    [Test]
    public void TnsLongFixture_HasTnsInLongBlocks() => Assert.That(Decode("tns_long_44k_mono").Statistics.LongChannels, Is.GreaterThan(5));

    [Test]
    public void Fixtures_CoverBothWindowShapes()
    {
        long kaiser = 0, total = 0;
        foreach (string name in Waveform().Concat(["tns_long_44k_mono", "tns_short_44k_mono"]))
        {
            AacStatistics statistics = Decode(name).Statistics;
            kaiser += statistics.KaiserBesselChannels;
            total += statistics.LongChannels + statistics.ShortChannels + statistics.StartChannels + statistics.StopChannels;
        }
        Assert.That(kaiser, Is.GreaterThan(0));
        Assert.That(kaiser, Is.LessThan(total));
    }

    [Test]
    public void MidSideFixture_UsesMidSide() => Assert.That(Decode("ms_22k_stereo").Statistics.MidSideBands, Is.GreaterThan(0));

    [Test]
    public void IntensityFixture_UsesIntensity() => Assert.That(Decode("is_44k_stereo").Statistics.IntensityBands, Is.GreaterThan(0));

    [Test]
    public void NoiseFixture_UsesNoiseSubstitutionAndKeepsTheReferenceEnergy()
    {
        // Noise substitution draws its own random numbers, so only the energy of each stretch of signal can agree, not the samples.
        (float[] samples, _, AacStatistics statistics) = Decode("pns_22k_stereo");
        Assert.That(statistics.NoiseBands, Is.GreaterThan(10));
        short[] reference = Reference("pns_22k_stereo");
        const int Block = 4096;
        for (int start = 2 * Block; start + Block <= Math.Min(samples.Length, reference.Length) - 2 * Block; start += Block)
        {
            double actual = 0, expected = 0;
            for (int i = start; i < start + Block; i++)
            {
                actual += samples[i] * samples[i];
                expected += (double)reference[i] * reference[i];
            }
            Assert.That(10 * Math.Log10(actual / expected), Is.EqualTo(0).Within(1.0), $"block at {start}");
        }
    }

    [TestCase(8000)]
    [TestCase(11025)]
    [TestCase(12000)]
    [TestCase(16000)]
    [TestCase(22050)]
    [TestCase(24000)]
    [TestCase(32000)]
    [TestCase(44100)]
    [TestCase(48000)]
    [TestCase(64000)]
    [TestCase(88200)]
    [TestCase(96000)]
    public void Decode_EverySamplingRateMatchesReferencePcm(int rate)
    {
        string name = $"rate_{rate}_mono";
        (float[] samples, int channels, _) = Decode(name);
        short[] reference = Reference(name);
        Assert.That(channels, Is.EqualTo(1));
        Assert.That(samples.Length, Is.EqualTo(reference.Length));
        Assert.That(SnrDb(samples, reference), Is.GreaterThan(50), name);
    }
}
