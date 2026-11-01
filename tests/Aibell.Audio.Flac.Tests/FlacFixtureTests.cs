using System.IO.Compression;
using System.Security.Cryptography;

namespace Aibell.Audio.Flac.Tests;

public class FlacFixtureTests
{
    internal static string Path(string name) => System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "flac", name);

    /// <summary>The source signal the fixture was encoded from: interleaved samples left-justified in 32 bits.</summary>
    internal static int[] Reference(string name)
    {
        using GZipStream gzip = new(File.OpenRead(Path(name + ".pcm.gz")), CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        int[] samples = new int[bytes.Length / 4];
        Buffer.BlockCopy(bytes.ToArray(), 0, samples, 0, samples.Length * 4);
        return samples;
    }

    internal static float[] DecodeAll(FlacDecoder decoder)
    {
        List<float> all = [];
        float[] block = new float[decoder.Channels * 1000];
        int got;
        while ((got = decoder.Read(block, 1000)) > 0) all.AddRange(block.AsSpan(0, got * decoder.Channels).ToArray());
        return [.. all];
    }

    internal static void AssertBitExact(float[] decoded, int[] reference)
    {
        Assert.That(decoded, Has.Length.EqualTo(reference.Length));
        for (int i = 0; i < reference.Length; i++)
        {
            if (decoded[i] != (float)(reference[i] / 2147483648.0)) Assert.Fail($"Sample {i} differs: {decoded[i]} instead of {reference[i] / 2147483648.0}");
        }
    }

    [TestCase("s16_stereo_44k")]
    [TestCase("s16_stereo_44k_indep")]
    [TestCase("s16_stereo_44k_left_side")]
    [TestCase("s16_stereo_44k_right_side")]
    [TestCase("s16_stereo_44k_mid_side")]
    [TestCase("s16_stereo_44k_level0")]
    [TestCase("s16_stereo_44k_level3")]
    [TestCase("s16_stereo_44k_level12")]
    [TestCase("s16_stereo_44k_block1000")]
    [TestCase("s16_stereo_44k_silence")]
    [TestCase("s16_mono_8k")]
    [TestCase("s24_stereo_96k")]
    [TestCase("s16_surround_48k")]
    [TestCase("s16_wasted_44k")]
    public void Decoding_ReproducesTheSourceBitForBit(string name)
    {
        string source = name.Replace("_indep", "").Replace("_left_side", "").Replace("_right_side", "").Replace("_mid_side", "").Replace("_level0", "")
            .Replace("_level3", "").Replace("_level12", "").Replace("_block1000", "");
        using FlacDecoder decoder = FlacDecoder.Open(File.OpenRead(Path(name + ".flac")));
        int[] reference = Reference(source);
        Assert.That(decoder.TotalFrames * decoder.Channels, Is.EqualTo(reference.Length));
        AssertBitExact(DecodeAll(decoder), reference);
    }

    [TestCase("s16_stereo_44k_mid_side")]
    [TestCase("s24_stereo_96k")]
    [TestCase("s16_surround_48k")]
    public void Decoding_MatchesTheMd5StoredInTheStream(string name)
    {
        using FlacDecoder decoder = FlacDecoder.Open(File.OpenRead(Path(name + ".flac")));
        float[] pcm = DecodeAll(decoder);
        int bytesPerSample = (decoder.BitsPerSample + 7) / 8;
        byte[] raw = new byte[pcm.Length * bytesPerSample];
        for (int i = 0; i < pcm.Length; i++)
        {
            int value = (int)Math.Round(pcm[i] * (1L << (decoder.BitsPerSample - 1)));
            for (int b = 0; b < bytesPerSample; b++) raw[i * bytesPerSample + b] = (byte)(value >> (8 * b));
        }
        Assert.That(MD5.HashData(raw), Is.EqualTo(decoder.AudioMd5.ToArray()));
    }

    [Test]
    public void Properties_DescribeTheStream()
    {
        using FlacDecoder decoder = FlacDecoder.Open(File.OpenRead(Path("s24_stereo_96k.flac")));
        Assert.That(decoder.SampleRate, Is.EqualTo(96000));
        Assert.That(decoder.Channels, Is.EqualTo(2));
        Assert.That(decoder.BitsPerSample, Is.EqualTo(24));
        Assert.That(decoder.TotalFrames, Is.EqualTo(19200));
    }
}
