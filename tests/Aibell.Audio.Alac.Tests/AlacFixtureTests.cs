using System.IO.Compression;

namespace Aibell.Audio.Alac.Tests;

public class AlacFixtureTests
{
    internal static string ApplePath(string name) => System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "alac-apple", name);

    internal static string Path(string name) => System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "alac", name);

    /// <summary>The source signal: interleaved samples left-justified in 32 bits.</summary>
    internal static int[] Reference(string name, bool apple = false)
    {
        using GZipStream gzip = new(File.OpenRead((apple ? ApplePath(name) : Path(name)) + ".pcm.gz"), CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        int[] samples = new int[bytes.Length / 4];
        Buffer.BlockCopy(bytes.ToArray(), 0, samples, 0, samples.Length * 4);
        return samples;
    }

    internal static float[] DecodeAll(IAudioDecoder decoder, int block = 1000)
    {
        List<float> all = [];
        float[] buffer = new float[decoder.Channels * block];
        int got;
        while ((got = decoder.Read(buffer, block)) > 0) all.AddRange(buffer.AsSpan(0, got * decoder.Channels).ToArray());
        return [.. all];
    }

    /// <summary>The decoded float samples as integers of the given width.</summary>
    internal static int[] ToInteger(float[] samples, int bits)
    {
        double scale = Math.Pow(2, bits - 1);
        int[] result = new int[samples.Length];
        for (int i = 0; i < samples.Length; i++) result[i] = (int)Math.Round(samples[i] * scale);
        return result;
    }

    [TestCase("s16_stereo_44k", 2, 44100, 16)]
    [TestCase("s16_stereo_44k_deep", 2, 44100, 16)]
    [TestCase("s16_stereo_noise", 2, 44100, 16)]
    [TestCase("s16_stereo_silence", 2, 44100, 16)]
    [TestCase("s16_mono_8k", 1, 8000, 16)]
    [TestCase("s24_stereo_96k", 2, 96000, 24)]
    [TestCase("s24_stereo_noise", 2, 48000, 24)]
    [TestCase("s16_3ch_48k", 3, 48000, 16)]
    [TestCase("s16_4ch_48k", 4, 48000, 16)]
    [TestCase("s16_5ch_48k", 5, 48000, 16)]
    [TestCase("s16_6ch_48k", 6, 48000, 16)]
    [TestCase("s16_7ch_48k", 7, 48000, 16)]
    [TestCase("s16_8ch_48k", 8, 48000, 16)]
    public void Decode_ReproducesTheSourceBitForBit(string name, int channels, int rate, int bits)
    {
        int[] reference = Reference(name);
        using AlacDecoder decoder = AlacDecoder.Open(File.OpenRead(Path(name + ".m4a")));
        Assert.That(decoder.Channels, Is.EqualTo(channels));
        Assert.That(decoder.SampleRate, Is.EqualTo(rate));
        Assert.That(decoder.BitDepth, Is.EqualTo(bits));
        Assert.That(decoder.TotalFrames, Is.EqualTo(reference.Length / channels));

        int[] decoded = ToInteger(DecodeAll(decoder), bits);
        Assert.That(decoded, Has.Length.EqualTo(reference.Length));
        int shift = 32 - bits;
        for (int i = 0; i < reference.Length; i++)
        {
            if (decoded[i] != reference[i] >> shift) Assert.Fail($"Sample {i} (frame {i / channels}, channel {i % channels}): {decoded[i]} instead of {reference[i] >> shift}.");
        }
    }

    [TestCase("s16_stereo_44k", 2, 16)]
    [TestCase("s24_stereo_96k", 2, 24)]
    [TestCase("s16_6ch_48k", 6, 16)]
    public void Seek_LandsOnTheExactFrame(string name, int channels, int bits)
    {
        int[] reference = Reference(name);
        int total = reference.Length / channels;
        int shift = 32 - bits;
        using AlacDecoder decoder = AlacDecoder.Open(File.OpenRead(Path(name + ".m4a")));
        float[] buffer = new float[channels * 700];
        foreach (long target in new long[] { 0, 1, 4095, 4096, 4097, 5000, total - 700, total - 1, 3 })
        {
            Assert.That(decoder.Seek(target), Is.True, $"seek {target}");
            int got = decoder.Read(buffer, 700);
            Assert.That(got, Is.EqualTo((int)Math.Min(700, total - target)));
            int[] decoded = ToInteger(buffer.AsSpan(0, got * channels).ToArray(), bits);
            for (int i = 0; i < decoded.Length; i++)
            {
                if (decoded[i] != reference[target * channels + i] >> shift) Assert.Fail($"Seek {target}: sample {i} differs.");
            }
        }
        Assert.That(decoder.Seek(total), Is.True);
        Assert.That(decoder.Read(buffer, 700), Is.EqualTo(0));
        Assert.That(decoder.Seek(total + 1), Is.False);
        Assert.That(decoder.Seek(-1), Is.False);
    }

    [Test]
    public void Read_InOddSizedBlocks_GivesTheSameSamples()
    {
        using AlacDecoder whole = AlacDecoder.Open(File.OpenRead(Path("s16_stereo_44k.m4a")));
        using AlacDecoder pieces = AlacDecoder.Open(File.OpenRead(Path("s16_stereo_44k.m4a")));
        Assert.That(DecodeAll(pieces, 333), Is.EqualTo(DecodeAll(whole, 4096)));
    }

    [Test]
    public void Open_AnMp4WithoutAlac_IsNotSupported()
    {
        string[] others = Directory.GetFiles(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures"), "*.m4a", SearchOption.AllDirectories);
        foreach (string path in others.Where(p => !p.Contains("alac")))
        {
            using FileStream stream = File.OpenRead(path);
            Assert.Throws<NotSupportedException>(() => AlacDecoder.Open(stream), path);
        }
    }

    [Test]
    public void Read_ADamagedPacket_IsReplacedBySilenceOfTheSameLength()
    {
        byte[] file = File.ReadAllBytes(Path("s16_stereo_44k.m4a"));
        Mp4AlacTrack track = Mp4AlacTrack.Read(new MemoryStream(file));
        // Overwrite the second packet with bytes that make no valid element sequence.
        Array.Fill(file, (byte)0xFF, (int)track.Offsets[1], track.Sizes[1]);
        using AlacDecoder decoder = AlacDecoder.Open(new MemoryStream(file));
        float[] decoded = DecodeAll(decoder);
        float[] intact = DecodeAll(AlacDecoder.Open(File.OpenRead(Path("s16_stereo_44k.m4a"))));
        Assert.That(decoded, Has.Length.EqualTo(intact.Length));
        int from = (int)track.StartFrames[1] * 2, to = (int)track.StartFrames[2] * 2;
        Assert.That(decoded.AsSpan(0, from).ToArray(), Is.EqualTo(intact.AsSpan(0, from).ToArray()));
        Assert.That(decoded.AsSpan(from, to - from).ToArray(), Is.All.EqualTo(0f));
        Assert.That(decoded.AsSpan(to).ToArray(), Is.EqualTo(intact.AsSpan(to).ToArray()));
    }

    // Streams made by Apple's own encoder (tests/fixtures/generate-alac-apple.sh): 20 and 32 bit, other frame sizes, shifted-out low bytes.
    [TestCase("a20_stereo_44k", 2, 20, 4096)]
    [TestCase("a20_mono_22k", 1, 20, 4096)]
    [TestCase("a24_stereo_44k_lowbits", 2, 24, 4096)]
    [TestCase("a32_stereo_48k", 2, 32, 4096)]
    [TestCase("a32_stereo_lowbits", 2, 32, 4096)]
    [TestCase("a32_mono_noise", 1, 32, 4096)]
    [TestCase("a16_stereo_frame1000", 2, 16, 1000)]
    [TestCase("a16_stereo_frame16384", 2, 16, 16384)]
    [TestCase("a24_6ch_44k", 6, 24, 4096)]
    public void Decode_StreamsFromApplesEncoder_ReproduceTheSource(string name, int channels, int bits, int frameLength)
    {
        int[] reference = Reference(name, apple: true);
        // Apple's encoder takes channels in the ALAC order; WAVE slot of each ALAC channel for the layouts used here.
        int[] slot = channels == 6 ? [2, 0, 1, 4, 5, 3] : [.. Enumerable.Range(0, channels)];
        using AlacDecoder decoder = AlacDecoder.Open(File.OpenRead(ApplePath(name + ".m4a")));
        Assert.That(decoder.Channels, Is.EqualTo(channels));
        Assert.That(decoder.BitDepth, Is.EqualTo(bits));
        Assert.That(decoder.TotalFrames, Is.EqualTo(reference.Length / channels));
        float[] decoded = DecodeAll(decoder, 777);
        Assert.That(decoded, Has.Length.EqualTo(reference.Length));
        double scale = Math.Pow(2, 31);
        for (int frame = 0; frame < reference.Length / channels; frame++)
        {
            for (int c = 0; c < channels; c++)
            {
                double expected = reference[frame * channels + c] / scale;
                double actual = decoded[frame * channels + slot[c]];
                // Up to 24 bits a float holds the sample exactly; 32-bit samples round to 24 bits of mantissa.
                double tolerance = bits == 32 ? Math.Abs(expected) * 1.2e-7 : 0;
                if (Math.Abs(actual - expected) > tolerance) Assert.Fail($"Frame {frame}, channel {c}: {actual} instead of {expected}.");
            }
        }
        Assert.That(decoder.Seek(frameLength + 3), Is.True);
        Assert.That(decoder.Read(new float[channels * 5], 5), Is.EqualTo(5));
    }
}
