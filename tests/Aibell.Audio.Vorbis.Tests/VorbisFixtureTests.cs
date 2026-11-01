using System.IO.Compression;

namespace Aibell.Audio.Vorbis.Tests;

public class VorbisFixtureTests
{
    internal static string Path(string name) => System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "vorbis", name);

    internal static float[] Reference(string name)
    {
        using GZipStream gzip = new(File.OpenRead(Path(name + ".f32.gz")), CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        float[] samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes.ToArray(), 0, samples, 0, samples.Length * 4);
        return samples;
    }

    internal static float[] DecodeAll(IAudioDecoder decoder)
    {
        List<float> all = [];
        float[] block = new float[decoder.Channels * 1000];
        int got;
        while ((got = decoder.Read(block, 1000)) > 0) all.AddRange(block.AsSpan(0, got * decoder.Channels).ToArray());
        return [.. all];
    }

    internal static double SnrDb(float[] decoded, float[] reference)
    {
        double signal = 0, noise = 0;
        int count = Math.Min(decoded.Length, reference.Length);
        for (int i = 0; i < count; i++)
        {
            signal += (double)reference[i] * reference[i];
            double d = decoded[i] - reference[i];
            noise += d * d;
        }
        return noise == 0 ? 200 : 10 * Math.Log10(signal / noise);
    }

    [TestCase("stereo_44k_q3")]
    [TestCase("stereo_44k_clicks_q5")]
    [TestCase("stereo_44k_clicks_q0")]
    [TestCase("mono_22k_q4")]
    [TestCase("surround_48k_q4")]
    [TestCase("stereo_8k_q2")]
    [TestCase("stereo_96k_q6")]
    public void Decoding_AgreesWithTheYardstick(string name)
    {
        using VorbisDecoder decoder = VorbisDecoder.Open(File.OpenRead(Path(name + ".ogg")));
        float[] pcm = DecodeAll(decoder);
        float[] reference = Reference(name);
        double energyRatio = 0, a = 0, b = 0;
        for (int i = 0; i < Math.Min(pcm.Length, reference.Length); i++)
        {
            a += (double)pcm[i] * reference[i];
            b += (double)pcm[i] * pcm[i];
        }
        energyRatio = b == 0 ? 0 : a / b;
        TestContext.Out.WriteLine($"{name}: frames {pcm.Length / decoder.Channels} vs {reference.Length / decoder.Channels}, gain to match {energyRatio:F6}, SNR {SnrDb(pcm, reference):F1} dB, total {decoder.TotalFrames}");
        // The yardstick keeps the padding after the last sample; the stream's own length is the granule position of its last page.
        Assert.That(pcm.Length / decoder.Channels, Is.EqualTo(decoder.TotalFrames), "length");
        int common = Math.Min(pcm.Length, reference.Length);
        Assert.That(reference.Length, Is.EqualTo(pcm.Length).Within(300 * decoder.Channels));
        Assert.That(SnrDb(pcm[..common], reference[..common]), Is.GreaterThan(100));
    }
}
