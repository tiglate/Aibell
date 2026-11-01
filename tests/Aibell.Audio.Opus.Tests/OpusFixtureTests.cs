using System.IO.Compression;
using Aibell.Audio.Ogg;

namespace Aibell.Audio.Opus.Tests;

public class OpusFixtureTests
{
    internal static string Path(string name) => System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "opus", name);

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

    /// <summary>The TOC configuration numbers of the audio packets of a file.</summary>
    internal static SortedSet<int> Configurations(string file)
    {
        SortedSet<int> configurations = [];
        OggPacketReader packets = new(File.OpenRead(file), data => data.Length >= 8 && data[..8].SequenceEqual("OpusHead"u8));
        int index = 0;
        while (packets.ReadPacket(out OggPacket packet))
        {
            if (index++ >= 2 && packet.Data.Length > 0) configurations.Add(packet.Data[0] >> 3);
        }
        return configurations;
    }

    [TestCase("celt_stereo_20ms", 28, 31)]
    [TestCase("celt_stereo_10ms", 28, 31)]
    [TestCase("celt_stereo_2_5ms", 16, 31)]
    [TestCase("celt_mono_wb", 16, 31)]
    [TestCase("silk_mono_nb", 0, 3)]
    [TestCase("silk_mono_wb_60ms", 8, 11)]
    [TestCase("silk_stereo_mb", 4, 7)]
    [TestCase("hybrid_stereo_swb", 12, 15)]
    [TestCase("hybrid_mono_fb", 12, 15)]
    public void Decoding_AgreesWithTheYardstick(string name, int firstConfiguration, int lastConfiguration)
    {
        Assert.That(Configurations(Path(name + ".opus")), Is.All.InRange(firstConfiguration, lastConfiguration), "the encoder was steered to another mode");
        using OggOpusDecoder decoder = OggOpusDecoder.Open(File.OpenRead(Path(name + ".opus")));
        float[] pcm = DecodeAll(decoder);
        float[] reference = Reference(name);
        TestContext.Out.WriteLine($"{name}: frames {pcm.Length / decoder.Channels} vs {reference.Length / decoder.Channels}, SNR {SnrDb(pcm, reference):F1} dB, total {decoder.TotalFrames}");
        Assert.Multiple(() =>
        {
            Assert.That(decoder.SampleRate, Is.EqualTo(48000));
            Assert.That(pcm.Length / decoder.Channels, Is.EqualTo(decoder.TotalFrames), "length");
            Assert.That(pcm, Has.Length.EqualTo(reference.Length));
            Assert.That(SnrDb(pcm, reference), Is.GreaterThan(80));
        });
    }
}
