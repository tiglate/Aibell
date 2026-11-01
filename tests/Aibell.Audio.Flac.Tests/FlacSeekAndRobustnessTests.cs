namespace Aibell.Audio.Flac.Tests;

public class FlacSeekAndRobustnessTests
{
    private static int[][] LongSignal(int frames)
    {
        Random random = new(99);
        int[][] data = [new int[frames], new int[frames]];
        for (int i = 0; i < frames; i++)
        {
            data[0][i] = (int)(Math.Sin(i * 0.013) * 9000 + random.Next(-3000, 3000));
            data[1][i] = (int)(Math.Sin(i * 0.007) * 7000 + random.Next(-3000, 3000));
        }
        return data;
    }

    private static float[] Decode(byte[] file)
    {
        using FlacDecoder decoder = FlacDecoder.Open(new MemoryStream(file));
        return FlacFixtureTests.DecodeAll(decoder);
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    public void Seek_LandsOnTheExactSample(bool seekTable, bool variableBlocks)
    {
        int[][] data = LongSignal(1_300_000);
        byte[] file = FlacTestEncoder.Encode(data, new FlacEncodeOptions { SeekTable = seekTable, VariableBlockSize = variableBlocks, BlockSizes = variableBlocks ? [4096, 3000, 1000, 4608] : [4096], Kind = SubframeKind.Fixed2 });
        Assert.That(file.Length, Is.GreaterThan(1_000_000), "The file must be large enough to need the bisection.");
        float[] all = Decode(file);
        using FlacDecoder decoder = FlacDecoder.Open(new MemoryStream(file));
        Random random = new(7);
        float[] block = new float[2 * 500];
        foreach (long target in new long[] { 0, 1, 4095, 4096, 700_000, 1_299_999, 12345, 1_000_001 }.Concat(Enumerable.Range(0, 12).Select(_ => (long)random.Next(1_300_000))))
        {
            Assert.That(decoder.Seek(target), Is.True);
            int got = decoder.Read(block, 500);
            Assert.That(got, Is.EqualTo((int)Math.Min(500, 1_300_000 - target)), $"target {target}");
            Assert.That(block.AsSpan(0, got * 2).ToArray(), Is.EqualTo(all.AsSpan((int)(target * 2), got * 2).ToArray()), $"target {target}");
        }
    }

    [Test]
    public void Seek_PastTheEnd_YieldsNothing_AndSeekingBackWorks()
    {
        byte[] file = FlacTestEncoder.Encode(LongSignal(20_000), new FlacEncodeOptions());
        float[] all = Decode(file);
        using FlacDecoder decoder = FlacDecoder.Open(new MemoryStream(file));
        Assert.That(decoder.Seek(20_000), Is.True);
        Assert.That(decoder.Read(new float[20], 10), Is.Zero);
        Assert.That(decoder.Seek(0), Is.True);
        Assert.That(FlacFixtureTests.DecodeAll(decoder), Is.EqualTo(all));
        Assert.That(decoder.Seek(-1), Is.False);
    }

    [Test]
    public void ID3Tag_BeforeTheStream_IsNotThisDecodersBusiness_ButTheRestOfTheStreamStartsWhereTheStreamIs()
    {
        byte[] file = FlacTestEncoder.Encode(LongSignal(5000), new FlacEncodeOptions());
        byte[] prefix = new byte[777];
        MemoryStream stream = new([.. prefix, .. file]);
        stream.Position = prefix.Length;
        using FlacDecoder decoder = FlacDecoder.Open(stream);
        Assert.That(decoder.Seek(2500), Is.True);
        Assert.That(FlacFixtureTests.DecodeAll(decoder), Has.Length.EqualTo(5000));
    }

    [Test]
    public void ADamagedFrame_BecomesSilence_AndTheRestIsUntouched()
    {
        byte[] file = FlacTestEncoder.Encode(LongSignal(40_000), new FlacEncodeOptions { BlockSizes = [1000], Kind = SubframeKind.Verbatim });
        float[] good = Decode(file);
        List<int> frames = [.. FlacTestEncoder.LastFrameOffsets];
        byte[] bad = (byte[])file.Clone();
        int victim = 10;
        bad[(frames[victim] + frames[victim + 1]) / 2] ^= 0x55;
        float[] damaged = Decode(bad);
        Assert.That(damaged, Has.Length.EqualTo(good.Length));
        Assert.That(damaged.AsSpan(0, victim * 2000).ToArray(), Is.EqualTo(good.AsSpan(0, victim * 2000).ToArray()));
        Assert.That(damaged.AsSpan(victim * 2000, 2000).ToArray(), Is.All.Zero);
        Assert.That(damaged.AsSpan((victim + 1) * 2000).ToArray(), Is.EqualTo(good.AsSpan((victim + 1) * 2000).ToArray()));
    }

    [Test]
    public void GarbageBetweenFrames_IsSkipped()
    {
        byte[] file = FlacTestEncoder.Encode(LongSignal(30_000), new FlacEncodeOptions { BlockSizes = [1000], Kind = SubframeKind.Verbatim });
        float[] good = Decode(file);
        int at = FlacTestEncoder.LastFrameOffsets[12];
        byte[] noisy = [.. file[..at], .. new byte[] { 1, 2, 3, 0xFF, 0x00, 0xFF, 0xF8, 9, 9, 9 }, .. file[at..]];
        float[] decoded = Decode(noisy);
        Assert.That(decoded, Is.EqualTo(good));
    }

    [Test]
    public void ATruncatedFile_EndsCleanly()
    {
        byte[] file = FlacTestEncoder.Encode(LongSignal(30_000), new FlacEncodeOptions { BlockSizes = [1000], Kind = SubframeKind.Verbatim });
        float[] good = Decode(file);
        int cut = FlacTestEncoder.LastFrameOffsets[20] + 500;
        float[] decoded = Decode(file[..cut]);
        Assert.That(decoded, Has.Length.EqualTo(20 * 2000));
        Assert.That(decoded, Is.EqualTo(good.AsSpan(0, 20 * 2000).ToArray()));
    }

    [Test]
    public void StreamsWithoutAudio_AndNonFlacData_AreRefused()
    {
        byte[] file = FlacTestEncoder.Encode(LongSignal(2000), new FlacEncodeOptions());
        int firstFrame = FlacTestEncoder.LastFrameOffsets[0];
        Assert.Throws<InvalidDataException>(() => FlacDecoder.Open(new MemoryStream(file[..firstFrame])));
        Assert.Throws<InvalidDataException>(() => FlacDecoder.Open(new MemoryStream("OggS and some more bytes"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => FlacDecoder.Open(new MemoryStream("fLaC"u8.ToArray())));
        byte[] noInfo = [.. "fLaC"u8.ToArray(), 0x81, 0, 0, 4, 1, 2, 3, 4];
        Assert.Throws<InvalidDataException>(() => FlacDecoder.Open(new MemoryStream(noInfo)));
    }

    [Test]
    public void Sniff_RecognizesTheMarker()
    {
        Assert.That(FlacDecoder.Sniff("fLaC\0\0\0\"x"u8), Is.True);
        Assert.That(FlacDecoder.Sniff("OggS"u8), Is.False);
        Assert.That(FlacDecoder.Sniff("fLa"u8), Is.False);
    }

    [Test]
    public void RandomDamage_NeverEscapesWithAnotherException()
    {
        byte[] good = File.ReadAllBytes(FlacFixtureTests.Path("s16_surround_48k.flac"))[..40_000];
        byte[] mid = File.ReadAllBytes(FlacFixtureTests.Path("s16_stereo_44k_mid_side.flac"))[..30_000];
        Random random = new(5);
        foreach (byte[] source in new[] { good, mid })
        {
            for (int round = 0; round < 600; round++)
            {
                byte[] bad = (byte[])source.Clone();
                int flips = 1 + random.Next(6);
                for (int f = 0; f < flips; f++) bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
                if (round % 4 == 0) bad = bad[..random.Next(bad.Length)];
                try
                {
                    using FlacDecoder decoder = FlacDecoder.Open(new MemoryStream(bad));
                    float[] block = new float[decoder.Channels * 700];
                    long total = 0;
                    int got;
                    while ((got = decoder.Read(block, 700)) > 0 && total < 5_000_000) total += got;
                    decoder.Seek(random.Next(100_000));
                    decoder.Read(block, 700);
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
                {
                }
            }
        }
    }

    [Test]
    public void RandomBytes_AfterAValidHeader_AreHandled()
    {
        byte[] header = File.ReadAllBytes(FlacFixtureTests.Path("s16_stereo_44k.flac"))[..42];
        Random random = new(8);
        for (int round = 0; round < 200; round++)
        {
            byte[] junk = new byte[random.Next(50_000)];
            random.NextBytes(junk);
            try
            {
                using FlacDecoder decoder = FlacDecoder.Open(new MemoryStream([.. header, .. junk]));
                float[] block = new float[1400];
                while (decoder.Read(block, 700) > 0) { }
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Test]
    public void Decoding_AllocatesNothingOnceRunning()
    {
        byte[] file = File.ReadAllBytes(FlacFixtureTests.Path("s16_stereo_44k_level12.flac"));
        using FlacDecoder decoder = FlacDecoder.Open(new MemoryStream(file));
        float[] block = new float[2 * 1024];
        decoder.Read(block, 1024);
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (decoder.Read(block, 1024) > 0) { }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [TestCase("s16_stereo_44k_level12")]
    [TestCase("s24_stereo_96k")]
    [TestCase("s16_surround_48k")]
    public void Decoding_IsFarFasterThanRealTime(string name)
    {
        byte[] file = File.ReadAllBytes(FlacFixtureTests.Path(name + ".flac"));
        float[] block = new float[8 * 4096];
        double seconds = 0;
        System.Diagnostics.Stopwatch watch = new();
        for (int round = 0; round < 30; round++)
        {
            using FlacDecoder decoder = FlacDecoder.Open(new MemoryStream(file));
            if (round == 0) seconds = (double)decoder.TotalFrames / decoder.SampleRate;
            watch.Start();
            while (decoder.Read(block, 4096) > 0) { }
            watch.Stop();
        }
        double share = watch.Elapsed.TotalSeconds / (30 * seconds);
        TestContext.Out.WriteLine($"{name}: {share * 100:F3} % of real time on one core");
        Assert.That(share, Is.LessThan(0.02));
    }
}
