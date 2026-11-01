using System.IO.Compression;

namespace Aibell.Audio.Mp3.Tests;

public class Mp3FixtureTests
{
    internal static string Path(string name) => System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "mp3", name);

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

    public static IEnumerable<string> Names =>
        Directory.GetFiles(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "mp3"), "*.f32.gz").Select(f => System.IO.Path.GetFileName(f)[..^7]).Order(StringComparer.Ordinal);

    [TestCaseSource(nameof(Names))]
    public void Decoding_AgreesWithTheYardstick(string name)
    {
        using Mp3Decoder decoder = Mp3Decoder.Open(File.OpenRead(Path(name + ".mp3")));
        float[] pcm = DecodeAll(decoder);
        float[] reference = Reference(name);
        TestContext.Out.WriteLine($"{name}: frames {pcm.Length / decoder.Channels} vs {reference.Length / decoder.Channels}, SNR {SnrDb(pcm, reference):F1} dB, total {decoder.TotalFrames}");
        Assert.Multiple(() =>
        {
            Assert.That(pcm.Length / decoder.Channels, Is.EqualTo(decoder.TotalFrames).Within(1152), "length");
            Assert.That(pcm.Length, Is.EqualTo(reference.Length).Within(2 * decoder.Channels), "length against the reference");
            Assert.That(SnrDb(pcm, reference), Is.GreaterThan(80));
        });
    }
}
