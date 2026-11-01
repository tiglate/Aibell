using System.IO.Compression;

namespace Aibell.Audio.Opus.Tests;

/// <summary>Multichannel Ogg Opus (mapping family 1, several coded streams per packet) against libopus's own decode.</summary>
public class OpusSurroundTests
{
    private static string Path(string name) => System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "opus-surround", name);

    private static float[] Reference(string name)
    {
        using GZipStream gzip = new(File.OpenRead(Path(name + ".f32.gz")), CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        float[] samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes.ToArray(), 0, samples, 0, samples.Length * 4);
        return samples;
    }

    [TestCase("surround_4ch", 4)]
    [TestCase("surround_6ch", 6)]
    [TestCase("surround_8ch", 8)]
    public void Decoding_AgreesWithTheYardstick(string name, int channels)
    {
        using OggOpusDecoder decoder = OggOpusDecoder.Open(File.OpenRead(Path(name + ".opus")));
        float[] pcm = OpusFixtureTests.DecodeAll(decoder);
        float[] reference = Reference(name);
        TestContext.Out.WriteLine($"{name}: frames {pcm.Length / channels} vs {reference.Length / channels}, SNR {OpusFixtureTests.SnrDb(pcm, reference):F1} dB");
        Assert.Multiple(() =>
        {
            Assert.That(decoder.Channels, Is.EqualTo(channels));
            Assert.That(pcm.Length / channels, Is.EqualTo(decoder.TotalFrames));
            Assert.That(pcm, Has.Length.EqualTo(reference.Length));
            Assert.That(OpusFixtureTests.SnrDb(pcm, reference), Is.GreaterThan(80));
        });
    }
}
