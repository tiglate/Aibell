using System.IO.Compression;

namespace Aibell.Audio.Mp3.Tests;

/// <summary>Layer I streams and free-format Layer III streams against FFmpeg's decode (of the same audio for the free-format ones, see the generator).</summary>
public class Mp3LayerTests
{
    private static string Path(string folder, string name) => System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", folder, name);

    private static IEnumerable<string> Files(string folder, string pattern) =>
        Directory.GetFiles(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", folder), pattern).Select(f => System.IO.Path.GetFileName(f)).Order(StringComparer.Ordinal);

    public static IEnumerable<string> Layer1Files => Files("mp3-layer1", "*.mp1");

    public static IEnumerable<string> Layer2Files => Files("mp3-layer2", "*.mp2");

    public static IEnumerable<string> FreeFormatFiles => Files("mp3-freeformat", "*.mp3");

    private static float[] Reference(string folder, string file)
    {
        using GZipStream gzip = new(File.OpenRead(Path(folder, System.IO.Path.GetFileNameWithoutExtension(file) + ".f32.gz")), CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        float[] samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes.ToArray(), 0, samples, 0, samples.Length * 4);
        return samples;
    }

    [TestCaseSource(nameof(Layer1Files))]
    public void LayerI_AgreesWithTheYardstick(string file)
    {
        using Mp3Decoder decoder = Mp3Decoder.Open(File.OpenRead(Path("mp3-layer1", file)));
        float[] pcm = Mp3FixtureTests.DecodeAll(decoder);
        float[] reference = Reference("mp3-layer1", file);
        double worst = 0;
        for (int i = 0; i < pcm.Length; i++) worst = Math.Max(worst, Math.Abs(pcm[i] - reference[i]));
        TestContext.Out.WriteLine($"{file}: {pcm.Length} vs {reference.Length}, SNR {Mp3FixtureTests.SnrDb(pcm, reference):F1} dB, largest difference {worst:E2}");
        Assert.That(pcm, Has.Length.EqualTo(reference.Length));
        Assert.That(decoder.TotalFrames, Is.EqualTo(150 * 384));
        // FFmpeg's Layer I/II decoder rounds its output to 16-bit steps (2^-15), so that is the yardstick's resolution; the two must agree to within one step.
        Assert.That(worst, Is.LessThan(3.3e-5));
        Assert.That(Mp3FixtureTests.SnrDb(pcm, reference), Is.GreaterThan(55));
    }

    [TestCaseSource(nameof(Layer2Files))]
    public void LayerII_AgreesWithTheYardstick(string file)
    {
        using Mp3Decoder decoder = Mp3Decoder.Open(File.OpenRead(Path("mp3-layer2", file)));
        float[] pcm = Mp3FixtureTests.DecodeAll(decoder);
        float[] reference = Reference("mp3-layer2", file);
        double worst = 0;
        for (int i = 0; i < Math.Min(pcm.Length, reference.Length); i++) worst = Math.Max(worst, Math.Abs(pcm[i] - reference[i]));
        TestContext.Out.WriteLine($"{file}: {pcm.Length} vs {reference.Length}, SNR {Mp3FixtureTests.SnrDb(pcm, reference):F1} dB, largest difference {worst:E2}");
        Assert.That(pcm, Has.Length.EqualTo(reference.Length));
        // FFmpeg rounds Layer I/II output to 16-bit steps (2^-15); the two decoders must agree to within one step.
        Assert.That(worst, Is.LessThan(3.3e-5));
        Assert.That(Mp3FixtureTests.SnrDb(pcm, reference), Is.GreaterThan(70));
    }

    [TestCaseSource(nameof(FreeFormatFiles))]
    public void FreeFormat_AgreesWithTheConstantBitRateTwin(string file)
    {
        using Mp3Decoder decoder = Mp3Decoder.Open(File.OpenRead(Path("mp3-freeformat", file)));
        float[] pcm = Mp3FixtureTests.DecodeAll(decoder);
        float[] reference = Reference("mp3-freeformat", file);
        TestContext.Out.WriteLine($"{file}: {pcm.Length} vs {reference.Length}, SNR {Mp3FixtureTests.SnrDb(pcm, reference):F1} dB");
        Assert.That(pcm, Has.Length.EqualTo(reference.Length));
        Assert.That(decoder.TotalFrames * decoder.Channels, Is.EqualTo(reference.Length));
        Assert.That(Mp3FixtureTests.SnrDb(pcm, reference), Is.GreaterThan(80));
    }

    [TestCase("layer1_mpeg1_44k_stereo.mp1", "mp3-layer1")]
    [TestCase("layer1_mpeg2_24k_joint8.mp1", "mp3-layer1")]
    [TestCase("random_mpeg1_44k_joint_ext1_256k.mp2", "mp3-layer2")]
    [TestCase("enc_32k_stereo_64k.mp2", "mp3-layer2")]
    [TestCase("free_44k_stereo_256k.mp2", "mp3-layer2")]
    [TestCase("free_mpeg1_44k_stereo_128k.mp3", "mp3-freeformat")]
    [TestCase("free_mpeg2_22k_stereo_64k.mp3", "mp3-freeformat")]
    public void Seek_LandsOnTheSameSamplesAsPlayingStraightThrough(string file, string folder)
    {
        using Mp3Decoder decoder = Mp3Decoder.Open(File.OpenRead(Path(folder, file)));
        float[] all = Mp3FixtureTests.DecodeAll(decoder);
        int channels = decoder.Channels;
        int frames = all.Length / channels;
        Random random = new(3);
        float[] block = new float[channels * 300];
        foreach (long target in new long[] { 0, 1, 383, 384, 385, 1000, frames - 300 }.Concat(Enumerable.Range(0, 12).Select(_ => (long)random.Next(frames - 300))))
        {
            Assert.That(decoder.Seek(target), Is.True);
            int got = decoder.Read(block, 300);
            Assert.That(got, Is.EqualTo(300));
            Assert.That(block.AsSpan(0, got * channels).ToArray(), Is.EqualTo(all.AsSpan((int)(target * channels), got * channels).ToArray()), $"samples at {target}");
        }
    }

    [Test]
    public void ForwardOnlyStreams_AreDecoded()
    {
        foreach ((string folder, string file) in new[] { ("mp3-layer1", "layer1_mpeg1_48k_dual.mp1"), ("mp3-freeformat", "free_mpeg1_48k_stereo_192k.mp3"), ("mp3-layer2", "random_mpeg1_48k_joint_ext3_192k.mp2") })
        {
            float[] expected = Mp3FixtureTests.DecodeAll(Mp3Decoder.Open(File.OpenRead(Path(folder, file))));
            using Mp3Decoder decoder = Mp3Decoder.Open(new Aibell.Audio.Wav.Tests.ForwardOnlyStream(File.ReadAllBytes(Path(folder, file))));
            Assert.That(Mp3FixtureTests.DecodeAll(decoder), Is.EqualTo(expected), file);
            Assert.That(decoder.Seek(5), Is.False);
        }
    }

    [Test]
    public void Sniffing_RecognizesLayerIAndFreeFormatStreams()
    {
        byte[] layer1 = File.ReadAllBytes(Path("mp3-layer1", "layer1_mpeg1_44k_stereo.mp1"));
        byte[] free = File.ReadAllBytes(Path("mp3-freeformat", "free_mpeg1_44k_stereo_128k.mp3"));
        Assert.That(Mp3Decoder.Sniff(layer1.AsSpan(0, 3000)), Is.True);
        Assert.That(Mp3Decoder.StartsWithFrame(layer1), Is.True);
        byte[] layer2 = File.ReadAllBytes(Path("mp3-layer2", "enc_44k_stereo_128k.mp2"));
        Assert.That(Mp3Decoder.Sniff(layer2.AsSpan(0, 3000)), Is.True);
        Assert.That(Mp3Decoder.StartsWithFrame(layer2), Is.True);
        Assert.That(Mp3Decoder.Sniff(free.AsSpan(0, 3000)), Is.True);
        Assert.That(Mp3Decoder.StartsWithFrame(free.AsSpan(0, 3000)), Is.True);
    }

    [Test]
    public void FreeFormat_FrameLengthIsMeasuredFromTheStream()
    {
        byte[] free = File.ReadAllBytes(Path("mp3-freeformat", "free_mpeg1_44k_stereo_128k.mp3"));
        Assert.That(Mp3Header.MeasureFreeFormat(free, dataEnds: false), Is.EqualTo(417));
        Assert.That(Mp3Header.MeasureFreeFormat(free.AsSpan(0, 3), dataEnds: false), Is.Zero);
        Assert.That(Mp3Header.MeasureFreeFormat(new byte[100], dataEnds: true), Is.Zero);
        using Mp3Decoder decoder = Mp3Decoder.Open(new MemoryStream(free));
        Assert.That(decoder.SampleRate, Is.EqualTo(44100));
    }

    [Test]
    public void DamagedFiles_NeverEscapeWithAnotherException()
    {
        foreach ((string folder, string name) in new[] { ("mp3-layer1", "layer1_mpeg1_32k_joint16.mp1"), ("mp3-layer1", "layer1_mpeg25_11k_mono.mp1"), ("mp3-layer2", "random_mpeg1_44k_joint_ext1_256k.mp2"), ("mp3-layer2", "enc_22k_stereo_64k.mp2"), ("mp3-freeformat", "free_mpeg2_22k_stereo_64k.mp3"), ("mp3-layer2", "random_mpeg1_44k_joint_ext2_64k.mp2"), ("mp3-layer2", "enc_22k_stereo_64k.mp2") })
        {
            byte[] good = File.ReadAllBytes(Path(folder, name));
            Random random = new(5);
            for (int round = 0; round < 80; round++)
            {
                byte[] bad = (byte[])good.Clone();
                int flips = 1 + random.Next(12);
                for (int f = 0; f < flips; f++) bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
                if (round % 4 == 0) bad = bad[..random.Next(bad.Length)];
                try
                {
                    using Mp3Decoder decoder = Mp3Decoder.Open(new MemoryStream(bad));
                    float[] block = new float[decoder.Channels * 500];
                    long total = 0;
                    int got;
                    while ((got = decoder.Read(block, 500)) > 0 && total < 3_000_000)
                    {
                        total += got;
                        foreach (float sample in block.AsSpan(0, got * decoder.Channels)) Assert.That(float.IsFinite(sample), Is.True);
                    }
                    decoder.Seek(random.Next(20_000));
                    decoder.Read(block, 500);
                }
                catch (InvalidDataException)
                {
                }
            }
        }
    }

    [Test]
    [TestCase("mp3-layer1", "layer1_mpeg1_44k_stereo.mp1")]
    [TestCase("mp3-layer2", "enc_44k_stereo_128k.mp2")]
    [TestCase("mp3-layer2", "random_mpeg1_48k_joint_ext3_192k.mp2")]
    public void LayersIAndII_DecodingAllocatesNothingOnceRunning(string folder, string file)
    {
        using Mp3Decoder decoder = Mp3Decoder.Open(File.OpenRead(Path(folder, file)));
        float[] block = new float[2 * 256];
        for (int i = 0; i < 5; i++) decoder.Read(block, 256);
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (decoder.Read(block, 256) > 0) { }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
