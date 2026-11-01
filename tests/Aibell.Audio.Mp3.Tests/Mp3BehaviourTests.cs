
namespace Aibell.Audio.Mp3.Tests;

public class Mp3BehaviourTests
{
    private static Mp3Decoder OpenFixture(string name) => Mp3Decoder.Open(File.OpenRead(Mp3FixtureTests.Path(name)));

    private static byte[] Bytes(string name) => File.ReadAllBytes(Mp3FixtureTests.Path(name));

    private static byte[] Id3v2(int payload)
    {
        byte[] tag = new byte[10 + payload];
        "ID3"u8.CopyTo(tag);
        tag[3] = 3;
        tag[6] = (byte)(payload >> 21 & 0x7F);
        tag[7] = (byte)(payload >> 14 & 0x7F);
        tag[8] = (byte)(payload >> 7 & 0x7F);
        tag[9] = (byte)(payload & 0x7F);
        return tag;
    }

    [Test]
    public void Properties_DescribeTheStream()
    {
        using Mp3Decoder decoder = OpenFixture("mpeg1_44k_stereo_128k.mp3");
        Assert.That(decoder.SampleRate, Is.EqualTo(44100));
        Assert.That(decoder.Channels, Is.EqualTo(2));
        Assert.That(decoder.TotalFrames, Is.EqualTo(30869));
        using Mp3Decoder mono = OpenFixture("mpeg25_8k_mono_16k.mp3");
        Assert.That(mono.SampleRate, Is.EqualTo(8000));
        Assert.That(mono.Channels, Is.EqualTo(1));
    }

    [Test]
    public void Seek_LandsOnTheSameSamplesAsPlayingStraightThrough()
    {
        using Mp3Decoder decoder = OpenFixture("long_44k.mp3");
        float[] all = Mp3FixtureTests.DecodeAll(decoder);
        int frames = all.Length / 2;
        Assert.That(frames, Is.EqualTo(decoder.TotalFrames));
        Assert.That(frames, Is.GreaterThan(600_000));
        Random random = new(4);
        float[] block = new float[2 * 700];
        foreach (long target in new long[] { 0, 1, 100, 1151, 1152, 4096, 300_000, frames - 700, frames - 1 }.Concat(Enumerable.Range(0, 20).Select(_ => (long)random.Next(frames))))
        {
            Assert.That(decoder.Seek(target), Is.True, $"seek {target}");
            int got = decoder.Read(block, 700);
            Assert.That(got, Is.EqualTo((int)Math.Min(700, frames - target)), $"count at {target}");
            Assert.That(block.AsSpan(0, got * 2).ToArray(), Is.EqualTo(all.AsSpan((int)(target * 2), got * 2).ToArray()), $"samples at {target}");
        }
    }

    [Test]
    public void Seek_PastTheEnd_YieldsNothing_AndSeekingBackWorks()
    {
        using Mp3Decoder decoder = OpenFixture("mpeg1_44k_stereo_128k.mp3");
        float[] all = Mp3FixtureTests.DecodeAll(decoder);
        Assert.That(decoder.Seek(decoder.TotalFrames + 10), Is.True);
        Assert.That(decoder.Read(new float[20], 10), Is.Zero);
        Assert.That(decoder.Seek(0), Is.True);
        Assert.That(Mp3FixtureTests.DecodeAll(decoder), Is.EqualTo(all));
        Assert.That(decoder.Seek(-1), Is.False);
    }

    [Test]
    public void Tags_AroundTheFrames_AreSkipped()
    {
        byte[] plain = Bytes("mpeg1_44k_stereo_128k.mp3");
        float[] expected = Mp3FixtureTests.DecodeAll(Mp3Decoder.Open(new MemoryStream(plain)));
        byte[] id3v1 = new byte[128];
        "TAG"u8.CopyTo(id3v1);
        byte[] tagged = [.. Id3v2(3000), .. new byte[3000], .. plain, .. id3v1];
        using Mp3Decoder decoder = Mp3Decoder.Open(new MemoryStream(tagged));
        Assert.That(decoder.TotalFrames, Is.EqualTo(30869));
        Assert.That(Mp3FixtureTests.DecodeAll(decoder), Is.EqualTo(expected));
    }

    [Test]
    public void ForwardOnlyStreams_AreDecoded_ButCannotSeek()
    {
        float[] expected = Mp3FixtureTests.DecodeAll(OpenFixture("mpeg2_22k_stereo_64k.mp3"));
        using Mp3Decoder decoder = Mp3Decoder.Open(new Aibell.Audio.Wav.Tests.ForwardOnlyStream(Bytes("mpeg2_22k_stereo_64k.mp3")));
        Assert.That(decoder.TotalFrames, Is.EqualTo(15434), "from the tag");
        Assert.That(Mp3FixtureTests.DecodeAll(decoder), Is.EqualTo(expected));
        Assert.That(decoder.Seek(5), Is.False);
    }

    [Test]
    public void StreamsWithoutALength_AreTreatedAsForwardOnly()
    {
        float[] expected = Mp3FixtureTests.DecodeAll(OpenFixture("mpeg1_44k_no_tag.mp3"));
        using Mp3Decoder decoder = Mp3Decoder.Open(new Aibell.Audio.Wav.Tests.NoLengthStream(Bytes("mpeg1_44k_no_tag.mp3")));
        Assert.That(decoder.TotalFrames, Is.EqualTo(-1), "no tag and no length");
        Assert.That(Mp3FixtureTests.DecodeAll(decoder), Is.EqualTo(expected));
        Assert.That(decoder.Seek(5), Is.False);
    }

    [Test]
    public void Garbage_BetweenFrames_IsSkipped()
    {
        byte[] good = Bytes("mpeg1_44k_stereo_128k.mp3");
        float[] expected = Mp3FixtureTests.DecodeAll(Mp3Decoder.Open(new MemoryStream(good)));
        // Insert junk (without anything that looks like a frame header) in the middle of the stream.
        int cut = 417 * 12 + 417;
        byte[] bad = [.. good[..cut], .. Enumerable.Repeat((byte)0x12, 700), .. good[cut..]];
        using Mp3Decoder decoder = Mp3Decoder.Open(new MemoryStream(bad));
        float[] decoded = Mp3FixtureTests.DecodeAll(decoder);
        // A frame hit by the cut is lost, the rest plays on.
        Assert.That(decoded.Length, Is.GreaterThan(expected.Length - 5 * 2304));
        Assert.That(decoded, Has.Length.LessThanOrEqualTo(expected.Length));
    }

    [Test]
    public void Sniff_RecognizesMp3Frames()
    {
        byte[] file = Bytes("mpeg1_44k_stereo_128k.mp3");
        Assert.That(Mp3Decoder.Sniff(file.AsSpan(0, 2000)), Is.True);
        Assert.That(Mp3Decoder.Sniff([.. Id3v2(10), .. new byte[10]]), Is.True);
        Assert.That(Mp3Decoder.Sniff("RIFF this is not mpeg audio at all, just text"u8), Is.False);
        Assert.That(Mp3Decoder.StartsWithFrame(file), Is.False, "the file starts with a tag");
        Assert.That(Mp3Decoder.StartsWithFrame(file.AsSpan(AudioSniffer.Id3v2Length(file))), Is.True);
        Assert.That(Mp3Decoder.StartsWithFrame("OggS"u8), Is.False);
        Assert.That(Mp3Decoder.StartsWithFrame([]), Is.False);
    }

    [Test]
    public void NonMp3Data_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => Mp3Decoder.Open(new MemoryStream("this is just text, not an mp3 file at all"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => Mp3Decoder.Open(new MemoryStream(Bytes("mpeg1_44k_stereo_128k.mp3")[..100])));
        Assert.Throws<InvalidDataException>(() => Mp3Decoder.Open(new MemoryStream([])));
    }

    [Test]
    public void DamagedFiles_NeverEscapeWithAnotherException()
    {
        foreach (string name in new[] { "mpeg1_44k_joint_96k.mp3", "mpeg2_22k_stereo_64k.mp3", "mpeg25_8k_mono_16k.mp3" })
        {
            byte[] good = Bytes(name);
            Random random = new(21);
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
    public void Decoding_AllocatesNothingOnceRunning()
    {
        using Mp3Decoder decoder = OpenFixture("long_44k.mp3");
        float[] block = new float[2 * 1024];
        for (int i = 0; i < 20; i++) decoder.Read(block, 1024);
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (decoder.Read(block, 1024) > 0) { }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [TestCase("long_44k.mp3")]
    [TestCase("mpeg1_44k_320k.mp3")]
    [TestCase("mpeg2_22k_stereo_64k.mp3")]
    public void Decoding_IsFarFasterThanRealTime(string name)
    {
        byte[] file = Bytes(name);
        float[] block = new float[8 * 4096];
        double seconds = 0;
        System.Diagnostics.Stopwatch watch = new();
        int rounds = name.StartsWith("long", StringComparison.Ordinal) ? 3 : 20;
        for (int round = 0; round < rounds; round++)
        {
            using Mp3Decoder decoder = Mp3Decoder.Open(new MemoryStream(file));
            if (round == 0) seconds = (double)decoder.TotalFrames / decoder.SampleRate;
            watch.Start();
            while (decoder.Read(block, 4096) > 0) { }
            watch.Stop();
        }
        double share = watch.Elapsed.TotalSeconds / (rounds * seconds);
        TestContext.Out.WriteLine($"{name}: {share * 100:F3} % of real time on one core");
        Assert.That(share, Is.LessThan(0.08));
    }
}
