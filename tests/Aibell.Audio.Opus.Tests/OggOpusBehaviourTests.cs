using System.Buffers.Binary;
using Aibell.Audio.Ogg;
using Aibell.Audio.Ogg.Tests;

namespace Aibell.Audio.Opus.Tests;

public class OggOpusBehaviourTests
{
    private static OggOpusDecoder OpenFixture(string name) => OggOpusDecoder.Open(File.OpenRead(OpusFixtureTests.Path(name)));

    private static byte[] Bytes(string name) => File.ReadAllBytes(OpusFixtureTests.Path(name));

    private static bool IsHead(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual("OpusHead"u8);

    private static List<byte[]> Packets(string name)
    {
        List<byte[]> packets = [];
        OggPacketReader reader = new(new MemoryStream(Bytes(name)), IsHead);
        while (reader.ReadPacket(out OggPacket packet)) packets.Add(packet.Data.ToArray());
        return packets;
    }

    private static byte[] Head(int channels, int preSkip, short gainQ8 = 0, byte family = 0, byte version = 1)
    {
        byte[] head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = version;
        head[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48000);
        BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(16), gainQ8);
        head[18] = family;
        return head;
    }

    private static readonly byte[] Tags = [.. "OpusTags"u8, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>Re-muxes audio packets behind a new header; the granule position of each page counts the samples decoded so far (pre-skip included).</summary>
    private static byte[] Mux(byte[] head, List<byte[]> audio, int packetsPerPage = 8, long? lastGranule = null, uint serial = 1, bool beginOfStream = true)
    {
        OggTestWriter writer = new(serial);
        if (beginOfStream)
        {
            writer.Page([head], 0, OggPageKind.BeginOfStream);
            writer.Page([Tags], 0);
        }
        long position = 0;
        for (int i = 0; i < audio.Count; i += packetsPerPage)
        {
            List<byte[]> group = [.. audio.Skip(i).Take(packetsPerPage)];
            foreach (byte[] packet in group) position += OpusDecoder.PacketSamples(packet, 0, packet.Length);
            bool last = i + packetsPerPage >= audio.Count;
            writer.Page(group, last && lastGranule is not null ? lastGranule.Value : position, last ? OggPageKind.EndOfStream : OggPageKind.None);
        }
        return writer.ToArray();
    }

    [Test]
    public void Properties_DescribeTheStream()
    {
        using OggOpusDecoder decoder = OpenFixture("celt_stereo_20ms.opus");
        Assert.That(decoder.SampleRate, Is.EqualTo(48000));
        Assert.That(decoder.Channels, Is.EqualTo(2));
        Assert.That(decoder.TotalFrames, Is.EqualTo(28800));
        using OggOpusDecoder mono = OpenFixture("silk_mono_nb.opus");
        Assert.That(mono.Channels, Is.EqualTo(1));
    }

    [Test]
    public void Seek_LandsCloseToPlayingStraightThrough()
    {
        using OggOpusDecoder decoder = OpenFixture("long_48k.opus");
        float[] all = OpusFixtureTests.DecodeAll(decoder);
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
            float[] expected = all.AsSpan((int)(target * 2), got * 2).ToArray();
            // The decoder restarts 80 ms before the target, so its internal state only has to be close, not identical.
            double snr = OpusFixtureTests.SnrDb(block.AsSpan(0, got * 2).ToArray(), expected);
            Assert.That(snr, Is.GreaterThan(35), $"samples at {target}");
        }
    }

    [Test]
    public void Seek_PastTheEnd_YieldsNothing_AndSeekingBackWorks()
    {
        using OggOpusDecoder decoder = OpenFixture("celt_stereo_20ms.opus");
        float[] all = OpusFixtureTests.DecodeAll(decoder);
        Assert.That(decoder.Seek(decoder.TotalFrames + 10), Is.True);
        Assert.That(decoder.Read(new float[20], 10), Is.Zero);
        Assert.That(decoder.Seek(0), Is.True);
        Assert.That(OpusFixtureTests.DecodeAll(decoder), Is.EqualTo(all));
        Assert.That(decoder.Seek(-1), Is.False);
    }

    [Test]
    public void ChainedStreams_WithTheSameChannelCount_PlayOneAfterTheOther()
    {
        float[] first = OpusFixtureTests.DecodeAll(OpenFixture("celt_stereo_20ms.opus"));
        float[] second = OpusFixtureTests.DecodeAll(OpenFixture("hybrid_stereo_swb.opus"));
        byte[] chained = [.. Bytes("celt_stereo_20ms.opus"), .. Bytes("hybrid_stereo_swb.opus")];
        using OggOpusDecoder decoder = OggOpusDecoder.Open(new MemoryStream(chained));
        Assert.That(decoder.TotalFrames, Is.EqualTo(-1), "the length of a chain is not known");
        float[] decoded = OpusFixtureTests.DecodeAll(decoder);
        Assert.That(decoded, Has.Length.EqualTo(first.Length + second.Length));
        Assert.That(decoded[..first.Length], Is.EqualTo(first));
        // The second link starts from a fresh decoder, exactly as when it is played alone.
        Assert.That(decoded[first.Length..], Is.EqualTo(second));
    }

    [Test]
    public void ChainedStreams_WithAnotherChannelCount_EndPlayback()
    {
        float[] first = OpusFixtureTests.DecodeAll(OpenFixture("celt_stereo_20ms.opus"));
        byte[] chained = [.. Bytes("celt_stereo_20ms.opus"), .. Bytes("silk_mono_nb.opus")];
        using OggOpusDecoder decoder = OggOpusDecoder.Open(new MemoryStream(chained));
        Assert.That(OpusFixtureTests.DecodeAll(decoder), Has.Length.EqualTo(first.Length));
    }

    [Test]
    public void ForwardOnlyStreams_AreDecoded_ButCannotSeek()
    {
        float[] expected = OpusFixtureTests.DecodeAll(OpenFixture("silk_stereo_mb.opus"));
        using OggOpusDecoder decoder = OggOpusDecoder.Open(new Aibell.Audio.Wav.Tests.ForwardOnlyStream(Bytes("silk_stereo_mb.opus")));
        Assert.That(decoder.TotalFrames, Is.EqualTo(-1));
        Assert.That(OpusFixtureTests.DecodeAll(decoder), Is.EqualTo(expected));
        Assert.That(decoder.Seek(5), Is.False);
    }

    [Test]
    public void StreamsWithoutALength_AreTreatedAsForwardOnly()
    {
        float[] expected = OpusFixtureTests.DecodeAll(OpenFixture("silk_stereo_mb.opus"));
        using OggOpusDecoder decoder = OggOpusDecoder.Open(new Aibell.Audio.Wav.Tests.NoLengthStream(Bytes("silk_stereo_mb.opus")));
        Assert.That(decoder.TotalFrames, Is.EqualTo(-1));
        Assert.That(OpusFixtureTests.DecodeAll(decoder), Is.EqualTo(expected));
        Assert.That(decoder.Seek(5), Is.False);
    }

    [Test]
    public void Sniff_RecognizesOpusInOgg()
    {
        Assert.That(OggOpusDecoder.Sniff(Bytes("celt_stereo_20ms.opus").AsSpan(0, 64)), Is.True);
        Assert.That(OggOpusDecoder.Sniff("OggS\0\x02\0\0\0\0\0\0\0\0"u8), Is.False);
        Assert.That(OggOpusDecoder.Sniff("RIFF"u8), Is.False);
    }

    [Test]
    public void NonOpusData_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream("this is just text, not an ogg file at all"u8.ToArray())));
        byte[] good = Bytes("celt_stereo_20ms.opus");
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream(good[..40])));
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream(good[..30])));
    }

    [Test]
    public void TheCommentHeader_IsRequired()
    {
        OggTestWriter writer = new(1);
        writer.Page([Head(2, 312)], 0, OggPageKind.BeginOfStream);
        writer.Page([Packets("celt_stereo_20ms.opus")[2]], 960, OggPageKind.EndOfStream);
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream(writer.ToArray())));
    }

    [Test]
    public void TheHeader_IsChecked()
    {
        List<byte[]> audio = Packets("celt_stereo_20ms.opus").Skip(2).ToList();
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 312, family: 1), audio))), "mapping family 1 without its table");
        Assert.Throws<NotSupportedException>(() => OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 312, family: 2), audio))), "mapping family 2");
        Assert.Throws<NotSupportedException>(() => OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 312, version: 16), audio))), "version");
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream(Mux(Head(3, 312), audio))), "three channels in family 0");
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream(Mux(Head(0, 312), audio))), "no channels");
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 312)[..12], audio))), "truncated");
    }

    [Test]
    public void PreSkip_And_EndTrimming_DecideTheLength()
    {
        List<byte[]> audio = Packets("celt_stereo_20ms.opus").Skip(2).ToList();
        long decoded = audio.Sum(p => (long)OpusDecoder.PacketSamples(p, 0, p.Length));
        using OggOpusDecoder plain = OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 312), audio)));
        Assert.That(plain.TotalFrames, Is.EqualTo(decoded - 312));
        Assert.That(OpusFixtureTests.DecodeAll(plain).Length / 2, Is.EqualTo(decoded - 312));

        using OggOpusDecoder longer = OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 1000), audio)));
        Assert.That(OpusFixtureTests.DecodeAll(longer).Length / 2, Is.EqualTo(decoded - 1000));

        // A final page that stops short of the decoded samples cuts the end of the stream.
        using OggOpusDecoder trimmed = OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 312), audio, lastGranule: decoded - 700)));
        Assert.That(trimmed.TotalFrames, Is.EqualTo(decoded - 700 - 312));
        Assert.That(OpusFixtureTests.DecodeAll(trimmed).Length / 2, Is.EqualTo(decoded - 700 - 312));
    }

    /// <summary>Like <see cref="Mux"/>, but the pages in <paramref name="dropped"/> are left out (their sequence numbers are skipped), as if they were lost.</summary>
    private static byte[] MuxWithLostPages(byte[] head, List<byte[]> audio, int packetsPerPage, params int[] dropped)
    {
        OggTestWriter writer = new(1);
        writer.Page([head], 0, OggPageKind.BeginOfStream);
        writer.Page([Tags], 0);
        long position = 0;
        int index = 0;
        for (int i = 0; i < audio.Count; i += packetsPerPage, index++)
        {
            List<byte[]> group = [.. audio.Skip(i).Take(packetsPerPage)];
            foreach (byte[] packet in group) position += OpusDecoder.PacketSamples(packet, 0, packet.Length);
            if (dropped.Contains(index)) continue;
            bool last = i + packetsPerPage >= audio.Count;
            writer.Page(group, position, last ? OggPageKind.EndOfStream : OggPageKind.None, sequenceOverride: (uint)(2 + index));
        }
        return writer.ToArray();
    }

    private static List<byte[]> SurroundPackets(string name)
    {
        List<byte[]> packets = [];
        OggPacketReader reader = new(File.OpenRead(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "opus-surround", name)), IsHead);
        while (reader.ReadPacket(out OggPacket packet)) packets.Add(packet.Data.ToArray());
        return packets;
    }

    [Test]
    public void MappingFamily255_PlaysTheChannelsInTheirCodedOrder()
    {
        List<byte[]> packets = SurroundPackets("surround_6ch.opus");
        byte[] wave = Mux(packets[0], packets.Skip(2).ToList());
        byte[] discrete = (byte[])packets[0].Clone();
        discrete[18] = 255;
        using OggOpusDecoder family1 = OggOpusDecoder.Open(new MemoryStream(wave));
        using OggOpusDecoder family255 = OggOpusDecoder.Open(new MemoryStream(Mux(discrete, packets.Skip(2).ToList())));
        float[] ordered = OpusFixtureTests.DecodeAll(family1);
        float[] coded = OpusFixtureTests.DecodeAll(family255);
        Assert.That(coded, Has.Length.EqualTo(ordered.Length));
        // Vorbis order FL C FR RL RR LFE becomes WAVE order FL FR C LFE RL RR.
        int[] where = [0, 2, 1, 4, 5, 3];
        for (int i = 0; i < ordered.Length / 6; i += 97)
        {
            for (int c = 0; c < 6; c++) Assert.That(coded[i * 6 + c], Is.EqualTo(ordered[i * 6 + where[c]]), $"frame {i} channel {c}");
        }
    }

    [Test]
    public void SurroundHeaders_AreChecked()
    {
        List<byte[]> packets = SurroundPackets("surround_6ch.opus");
        byte[] head = packets[0];
        byte[] badMapping = (byte[])head.Clone();
        badMapping[21] = 40; // names a stream that does not exist
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream(Mux(badMapping, packets.Skip(2).ToList()))));
        byte[] tooManyCoupled = (byte[])head.Clone();
        tooManyCoupled[20] = 9;
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream(Mux(tooManyCoupled, packets.Skip(2).ToList()))));
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream(Mux(head[..24], packets.Skip(2).ToList()))), "truncated table");
        byte[] ambisonics = (byte[])head.Clone();
        ambisonics[18] = 2;
        Assert.Throws<NotSupportedException>(() => OggOpusDecoder.Open(new MemoryStream(Mux(ambisonics, packets.Skip(2).ToList()))));
        byte[] tooWide = (byte[])head.Clone();
        tooWide[9] = 9;
        Assert.Throws<InvalidDataException>(() => OggOpusDecoder.Open(new MemoryStream(Mux(tooWide, packets.Skip(2).ToList()))));
    }

    [Test]
    public void Surround_LostPagesAreConcealed_AndDamageIsSurvived()
    {
        List<byte[]> packets = SurroundPackets("surround_6ch.opus");
        List<byte[]> audio = packets.Skip(2).ToList();
        using OggOpusDecoder intact = OggOpusDecoder.Open(new MemoryStream(Mux(packets[0], audio)));
        float[] expected = OpusFixtureTests.DecodeAll(intact);
        using OggOpusDecoder lost = OggOpusDecoder.Open(new MemoryStream(MuxWithLostPages(packets[0], audio, 8, 1)));
        float[] concealed = OpusFixtureTests.DecodeAll(lost);
        Assert.That(concealed, Has.Length.EqualTo(expected.Length));
        Assert.That(concealed.All(float.IsFinite), Is.True);

        Random random = new(8);
        for (int round = 0; round < 60; round++)
        {
            List<byte[]> damaged = [.. audio.Select(p => (byte[])p.Clone())];
            for (int k = 0; k < 6; k++)
            {
                byte[] packet = damaged[random.Next(damaged.Count)];
                packet[random.Next(packet.Length)] ^= (byte)(1 << random.Next(8));
            }
            using OggOpusDecoder decoder = OggOpusDecoder.Open(new MemoryStream(Mux(packets[0], damaged)));
            Assert.That(OpusFixtureTests.DecodeAll(decoder).All(float.IsFinite), Is.True);
        }
    }

    [Test]
    public void Surround_DecodingAllocatesNothingOnceRunning()
    {
        using OggOpusDecoder decoder = OggOpusDecoder.Open(File.OpenRead(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "opus-surround", "surround_6ch.opus")));
        float[] block = new float[6 * 960];
        for (int i = 0; i < 3; i++) decoder.Read(block, 960);
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (decoder.Read(block, 960) > 0) { }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void LostPages_AreConcealed_AndTheLengthIsKept()
    {
        List<byte[]> audio = Packets("celt_stereo_20ms.opus").Skip(2).ToList();
        using OggOpusDecoder intact = OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 312), audio)));
        float[] expected = OpusFixtureTests.DecodeAll(intact);

        foreach (int[] lost in new[] { new[] { 1 }, [2], [1, 2] })
        {
            using OggOpusDecoder damaged = OggOpusDecoder.Open(new MemoryStream(MuxWithLostPages(Head(2, 312), audio, 8, lost)));
            float[] decoded = OpusFixtureTests.DecodeAll(damaged);
            Assert.That(decoded, Has.Length.EqualTo(expected.Length), $"length after losing pages {string.Join(",", lost)}");
            Assert.That(decoded.All(float.IsFinite), Is.True);
            // Everything before the first lost page is untouched.
            int untouched = (lost[0] * 8 * 960 - 312) * 2;
            Assert.That(decoded.AsSpan(0, untouched).ToArray(), Is.EqualTo(expected.AsSpan(0, untouched).ToArray()));
            // The gap is not silent: the concealment carries the sound on and fades it out.
            int gapStart = untouched;
            float peak = 0;
            for (int i = gapStart; i < gapStart + 960 * 2; i++) peak = Math.Max(peak, Math.Abs(decoded[i]));
            Assert.That(peak, Is.GreaterThan(0.001f), "concealed audio");
        }
    }

    [Test]
    public void PreSkip_RemovesTheFirstSamples()
    {
        List<byte[]> audio = Packets("celt_stereo_20ms.opus").Skip(2).ToList();
        float[] all = OpusFixtureTests.DecodeAll(OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 0), audio))));
        float[] skipped = OpusFixtureTests.DecodeAll(OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 500), audio))));
        Assert.That(skipped, Is.EqualTo(all[(500 * 2)..]));
    }

    [Test]
    public void OutputGain_ScalesTheSamples()
    {
        List<byte[]> audio = Packets("celt_stereo_20ms.opus").Skip(2).ToList();
        float[] plain = OpusFixtureTests.DecodeAll(OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 312), audio))));
        float[] louder = OpusFixtureTests.DecodeAll(OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 312, gainQ8: 6 * 256), audio))));
        float[] quieter = OpusFixtureTests.DecodeAll(OggOpusDecoder.Open(new MemoryStream(Mux(Head(2, 312, gainQ8: -6 * 256), audio))));
        double up = Math.Pow(10, 6 / 20.0);
        for (int i = 0; i < plain.Length; i += 37)
        {
            Assert.That(louder[i], Is.EqualTo(plain[i] * up).Within(1e-6));
            Assert.That(quieter[i], Is.EqualTo(plain[i] / up).Within(1e-6));
        }
    }

    [Test]
    public void ReadingInSmallPieces_GivesTheSameSamples()
    {
        using OggOpusDecoder whole = OpenFixture("hybrid_mono_fb.opus");
        float[] all = OpusFixtureTests.DecodeAll(whole);
        using OggOpusDecoder pieces = OpenFixture("hybrid_mono_fb.opus");
        List<float> collected = [];
        float[] block = new float[7];
        int got;
        while ((got = pieces.Read(block, 7)) > 0) collected.AddRange(block.AsSpan(0, got).ToArray());
        Assert.That(collected, Is.EqualTo(all));
    }

    [Test]
    public void DamagedFiles_NeverEscapeWithAnotherException()
    {
        byte[] good = Bytes("hybrid_stereo_swb.opus");
        Random random = new(21);
        for (int round = 0; round < 300; round++)
        {
            byte[] bad = (byte[])good.Clone();
            int flips = 1 + random.Next(8);
            for (int f = 0; f < flips; f++) bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
            if (round % 4 == 0) bad = bad[..random.Next(bad.Length)];
            try
            {
                using OggOpusDecoder decoder = OggOpusDecoder.Open(new MemoryStream(bad));
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

    [TestCase("celt_stereo_10ms.opus")]
    [TestCase("silk_stereo_mb.opus")]
    [TestCase("hybrid_stereo_swb.opus")]
    public void RandomDamageInsideThePackets_IsSurvived(string name)
    {
        // Pages carry a CRC, so damage that reaches the decoder is made by rewriting pages: here the audio packets themselves are corrupted and re-paged.
        List<byte[]> packets = Packets(name);
        Random random = new(8);
        for (int round = 0; round < 150; round++)
        {
            List<byte[]> damaged = [];
            for (int i = 2; i < packets.Count; i++)
            {
                byte[] copy = (byte[])packets[i].Clone();
                if (random.Next(3) == 0 && copy.Length > 0) copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
                if (random.Next(15) == 0) copy = copy[..random.Next(copy.Length)];
                if (copy.Length > 0) damaged.Add(copy);
            }
            using OggOpusDecoder decoder = OggOpusDecoder.Open(new MemoryStream(Mux(packets[0], damaged)));
            float[] block = new float[2 * 500];
            int got;
            while ((got = decoder.Read(block, 500)) > 0)
            {
                foreach (float sample in block.AsSpan(0, got * 2)) Assert.That(float.IsFinite(sample), Is.True);
            }
        }
    }

    [Test]
    public void Decoding_AllocatesNothingOnceRunning()
    {
        using OggOpusDecoder decoder = OpenFixture("long_48k.opus");
        float[] block = new float[2 * 1024];
        for (int i = 0; i < 20; i++) decoder.Read(block, 1024);
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (decoder.Read(block, 1024) > 0) { }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [TestCase("long_48k.opus")]
    [TestCase("celt_stereo_2_5ms.opus")]
    [TestCase("hybrid_stereo_swb.opus")]
    [TestCase("silk_stereo_mb.opus")]
    public void Decoding_IsFarFasterThanRealTime(string name)
    {
        byte[] file = Bytes(name);
        float[] block = new float[8 * 4096];
        double seconds = 0;
        System.Diagnostics.Stopwatch watch = new();
        int rounds = name.StartsWith("long", StringComparison.Ordinal) ? 3 : 20;
        for (int round = 0; round < rounds; round++)
        {
            using OggOpusDecoder decoder = OggOpusDecoder.Open(new MemoryStream(file));
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
