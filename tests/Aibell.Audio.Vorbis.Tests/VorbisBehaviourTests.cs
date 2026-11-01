namespace Aibell.Audio.Vorbis.Tests;

public class VorbisBehaviourTests
{
    private static VorbisDecoder OpenFixture(string name) => VorbisDecoder.Open(File.OpenRead(VorbisFixtureTests.Path(name)));

    private static byte[] Bytes(string name) => File.ReadAllBytes(VorbisFixtureTests.Path(name));

    [Test]
    public void Properties_DescribeTheStream()
    {
        using VorbisDecoder decoder = OpenFixture("surround_48k_q4.ogg");
        Assert.That(decoder.SampleRate, Is.EqualTo(48000));
        Assert.That(decoder.Channels, Is.EqualTo(6));
        Assert.That(decoder.TotalFrames, Is.EqualTo(14400));
    }

    [Test]
    public void Seek_LandsOnTheSameSamplesAsPlayingStraightThrough()
    {
        using VorbisDecoder decoder = OpenFixture("long_44k_q1.ogg");
        float[] all = VorbisFixtureTests.DecodeAll(decoder);
        int frames = all.Length / 2;
        Assert.That(frames, Is.EqualTo(decoder.TotalFrames));
        Assert.That(frames, Is.GreaterThan(600_000));
        Random random = new(4);
        float[] block = new float[2 * 700];
        foreach (long target in new long[] { 0, 1, 100, 2047, 2048, 4096, 300_000, frames - 700, frames - 1 }.Concat(Enumerable.Range(0, 15).Select(_ => (long)random.Next(frames))))
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
        using VorbisDecoder decoder = OpenFixture("stereo_44k_q3.ogg");
        float[] all = VorbisFixtureTests.DecodeAll(decoder);
        Assert.That(decoder.Seek(decoder.TotalFrames + 10), Is.True);
        Assert.That(decoder.Read(new float[20], 10), Is.Zero);
        Assert.That(decoder.Seek(0), Is.True);
        Assert.That(VorbisFixtureTests.DecodeAll(decoder), Is.EqualTo(all));
        Assert.That(decoder.Seek(-1), Is.False);
    }

    [Test]
    public void ChainedStreams_OfTheSameFormat_PlayOneAfterTheOther()
    {
        float[] first = VorbisFixtureTests.DecodeAll(OpenFixture("stereo_44k_q3.ogg"));
        float[] second = VorbisFixtureTests.DecodeAll(OpenFixture("stereo_44k_clicks_q5.ogg"));
        byte[] chained = [.. Bytes("stereo_44k_q3.ogg"), .. Bytes("stereo_44k_clicks_q5.ogg")];
        using VorbisDecoder decoder = VorbisDecoder.Open(new MemoryStream(chained));
        Assert.That(decoder.TotalFrames, Is.EqualTo(-1), "the length of a chain is not known");
        float[] decoded = VorbisFixtureTests.DecodeAll(decoder);
        Assert.That(decoded, Has.Length.EqualTo(first.Length + second.Length));
        Assert.That(decoded[..first.Length], Is.EqualTo(first));
        Assert.That(decoded[first.Length..], Is.EqualTo(second));
    }

    [Test]
    public void ChainedStreams_OfAnotherFormat_EndPlayback()
    {
        float[] first = VorbisFixtureTests.DecodeAll(OpenFixture("stereo_44k_q3.ogg"));
        byte[] chained = [.. Bytes("stereo_44k_q3.ogg"), .. Bytes("mono_22k_q4.ogg")];
        using VorbisDecoder decoder = VorbisDecoder.Open(new MemoryStream(chained));
        Assert.That(VorbisFixtureTests.DecodeAll(decoder), Has.Length.EqualTo(first.Length));
    }

    [Test]
    public void ForwardOnlyStreams_AreDecoded_ButCannotSeek()
    {
        float[] expected = VorbisFixtureTests.DecodeAll(OpenFixture("stereo_8k_q2.ogg"));
        using VorbisDecoder decoder = VorbisDecoder.Open(new Aibell.Audio.Wav.Tests.ForwardOnlyStream(Bytes("stereo_8k_q2.ogg")));
        Assert.That(decoder.TotalFrames, Is.EqualTo(-1));
        Assert.That(VorbisFixtureTests.DecodeAll(decoder), Is.EqualTo(expected));
        Assert.That(decoder.Seek(5), Is.False);
    }

    [Test]
    public void StreamsWithoutALength_AreTreatedAsForwardOnly()
    {
        float[] expected = VorbisFixtureTests.DecodeAll(OpenFixture("stereo_8k_q2.ogg"));
        using VorbisDecoder decoder = VorbisDecoder.Open(new Aibell.Audio.Wav.Tests.NoLengthStream(Bytes("stereo_8k_q2.ogg")));
        Assert.That(decoder.TotalFrames, Is.EqualTo(-1));
        Assert.That(VorbisFixtureTests.DecodeAll(decoder), Is.EqualTo(expected));
        Assert.That(decoder.Seek(5), Is.False);
    }

    [Test]
    public void Sniff_RecognizesVorbisInOgg()
    {
        Assert.That(VorbisDecoder.Sniff(Bytes("stereo_44k_q3.ogg").AsSpan(0, 64)), Is.True);
        Assert.That(VorbisDecoder.Sniff("OggS\0\x02\0\0\0\0\0\0\0\0"u8), Is.False);
        Assert.That(VorbisDecoder.Sniff("RIFF"u8), Is.False);
    }

    [Test]
    public void NonVorbisData_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => VorbisDecoder.Open(new MemoryStream("this is just text, not an ogg file at all"u8.ToArray())));
        byte[] good = Bytes("stereo_44k_q3.ogg");
        Assert.Throws<InvalidDataException>(() => VorbisDecoder.Open(new MemoryStream(good[..200])));
    }

    [Test]
    public void DamagedFiles_NeverEscapeWithAnotherException()
    {
        byte[] good = Bytes("surround_48k_q4.ogg");
        Random random = new(21);
        for (int round = 0; round < 400; round++)
        {
            byte[] bad = (byte[])good.Clone();
            int flips = 1 + random.Next(8);
            for (int f = 0; f < flips; f++) bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
            if (round % 4 == 0) bad = bad[..random.Next(bad.Length)];
            try
            {
                using VorbisDecoder decoder = VorbisDecoder.Open(new MemoryStream(bad));
                float[] block = new float[decoder.Channels * 500];
                long total = 0;
                int got;
                while ((got = decoder.Read(block, 500)) > 0 && total < 3_000_000) total += got;
                decoder.Seek(random.Next(20_000));
                decoder.Read(block, 500);
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
            }
        }
    }

    [Test]
    public void RandomDamageInsideThePackets_IsSurvived()
    {
        // Pages carry a CRC, so damage that reaches the decoder is made by rewriting pages: here the audio packets themselves are corrupted and re-paged.
        byte[] good = Bytes("stereo_44k_clicks_q5.ogg");
        List<byte[]> packets = [];
        Ogg.OggPacketReader reader = new(new MemoryStream(good), VorbisIdentification.IsIdentification);
        while (reader.ReadPacket(out Ogg.OggPacket packet)) packets.Add(packet.Data.ToArray());
        Random random = new(8);
        for (int round = 0; round < 150; round++)
        {
            List<byte[]> damaged = [];
            for (int i = 0; i < packets.Count; i++)
            {
                byte[] copy = (byte[])packets[i].Clone();
                if (i >= 3 && random.Next(3) == 0 && copy.Length > 0) copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
                if (i >= 3 && random.Next(15) == 0) copy = copy[..random.Next(copy.Length)];
                damaged.Add(copy);
            }
            Ogg.Tests.OggTestWriter writer = new(1);
            writer.Page([damaged[0]], 0, Ogg.OggPageKind.BeginOfStream);
            writer.Page([damaged[1], damaged[2]], 0);
            for (int i = 3; i < damaged.Count; i += 6) writer.Page([.. damaged.Skip(i).Take(6)], 2000L * (i + 6), i + 6 >= damaged.Count ? Ogg.OggPageKind.EndOfStream : Ogg.OggPageKind.None);
            try
            {
                using VorbisDecoder decoder = VorbisDecoder.Open(new MemoryStream(writer.ToArray()));
                float[] block = new float[2 * 500];
                while (decoder.Read(block, 500) > 0) { }
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
            }
        }
    }

    [Test]
    public void Decoding_AllocatesNothingOnceRunning()
    {
        using VorbisDecoder decoder = OpenFixture("long_44k_q1.ogg");
        float[] block = new float[2 * 1024];
        for (int i = 0; i < 20; i++) decoder.Read(block, 1024);
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (decoder.Read(block, 1024) > 0) { }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [TestCase("long_44k_q1.ogg")]
    [TestCase("stereo_44k_clicks_q5.ogg")]
    [TestCase("surround_48k_q4.ogg")]
    public void Decoding_IsFarFasterThanRealTime(string name)
    {
        byte[] file = Bytes(name);
        float[] block = new float[8 * 4096];
        double seconds = 0;
        System.Diagnostics.Stopwatch watch = new();
        int rounds = name.StartsWith("long", StringComparison.Ordinal) ? 3 : 20;
        for (int round = 0; round < rounds; round++)
        {
            using VorbisDecoder decoder = VorbisDecoder.Open(new MemoryStream(file));
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
