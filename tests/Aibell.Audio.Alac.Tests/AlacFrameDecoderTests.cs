namespace Aibell.Audio.Alac.Tests;

public class AlacFrameDecoderTests
{
    private static int[] Ramp(int count, int bits, int seed)
    {
        Random random = new(seed);
        int[] samples = new int[count];
        for (int i = 0; i < count; i++) samples[i] = random.Next(-(1 << (bits - 1)), 1 << (bits - 1));
        samples[0] = -(1 << (bits - 1));
        samples[1] = (1 << (bits - 1)) - 1;
        return samples;
    }

    private static float Expected(int sample, int bits) => (float)(sample / Math.Pow(2, bits - 1));

    [TestCase(16)]
    [TestCase(20)]
    [TestCase(24)]
    [TestCase(32)]
    public void Decode_UncompressedStereo_ReturnsTheSamples(int bits)
    {
        int[] left = Ramp(64, bits, 1), right = Ramp(64, bits, 2);
        AlacFrameDecoder decoder = new(AlacPacketBuilder.Cookie(64, bits, 2));
        byte[] packet = new AlacPacketBuilder().EscapePair(left, right, bits).End().ToArray();
        float[] output = new float[128];
        Assert.That(decoder.Decode(packet, output), Is.EqualTo(64));
        for (int i = 0; i < 64; i++)
        {
            Assert.That(output[i * 2], Is.EqualTo(Expected(left[i], bits)), $"left {i}");
            Assert.That(output[i * 2 + 1], Is.EqualTo(Expected(right[i], bits)), $"right {i}");
        }
    }

    [TestCase(16)]
    [TestCase(20)]
    [TestCase(24)]
    [TestCase(32)]
    public void Decode_UncompressedMono_ReturnsTheSamples(int bits)
    {
        int[] samples = Ramp(100, bits, 3);
        AlacFrameDecoder decoder = new(AlacPacketBuilder.Cookie(100, bits, 1));
        float[] output = new float[100];
        Assert.That(decoder.Decode(new AlacPacketBuilder().EscapeSingle(samples, bits).End().ToArray(), output), Is.EqualTo(100));
        for (int i = 0; i < 100; i++) Assert.That(output[i], Is.EqualTo(Expected(samples[i], bits)), $"sample {i}");
    }

    [Test]
    public void Decode_APartialFrame_ReturnsTheShorterLength()
    {
        int[] samples = Ramp(37, 16, 4);
        AlacFrameDecoder decoder = new(AlacPacketBuilder.Cookie(64, 16, 1));
        float[] output = new float[64];
        Assert.That(decoder.Decode(new AlacPacketBuilder().EscapeSingle(samples, 16, partial: 37).End().ToArray(), output), Is.EqualTo(37));
        Assert.That(output[36], Is.EqualTo(Expected(samples[36], 16)));
    }

    [Test]
    public void Decode_SkipsDataStreamAndFillElements()
    {
        int[] samples = Ramp(16, 16, 5);
        AlacFrameDecoder decoder = new(AlacPacketBuilder.Cookie(16, 16, 1));
        AlacPacketBuilder builder = new();
        builder.Bits(4, 3).Bits(2, 4).Bits(1, 1).Bits(3, 8).Bits(0xAABBCC, 24); // data stream: tag 2, byte aligned, 3 bytes
        builder.Bits(6, 3).Bits(2, 4).Bits(0xDDEE, 16);                                      // fill: 2 bytes
        builder.EscapeSingle(samples, 16).End();
        float[] output = new float[16];
        Assert.That(decoder.Decode(builder.ToArray(), output), Is.EqualTo(16));
        Assert.That(output[5], Is.EqualTo(Expected(samples[5], 16)));
    }

    [Test]
    public void Decode_AMissingChannelIsSilent()
    {
        int[] samples = Ramp(8, 16, 6);
        AlacFrameDecoder decoder = new(AlacPacketBuilder.Cookie(8, 16, 2));
        float[] output = new float[16];
        Array.Fill(output, 9f);
        Assert.That(decoder.Decode(new AlacPacketBuilder().EscapeSingle(samples, 16).End().ToArray(), output), Is.EqualTo(8));
        for (int i = 0; i < 8; i++)
        {
            Assert.That(output[i * 2], Is.EqualTo(Expected(samples[i], 16)));
            Assert.That(output[i * 2 + 1], Is.EqualTo(0f));
        }
    }

    [Test]
    public void Decode_FiveOneBringsTheChannelsToWaveOrder()
    {
        // Bitstream order of 5.1: C, L, R, Ls, Rs, LFE; WAVE order: L, R, C, LFE, Ls, Rs.
        int[][] c = [[100], [200], [300], [400], [500], [600]];
        AlacFrameDecoder decoder = new(AlacPacketBuilder.Cookie(1, 16, 6));
        byte[] packet = new AlacPacketBuilder().EscapeSingle(c[0], 16).EscapePair(c[1], c[2], 16).EscapePair(c[3], c[4], 16).EscapeSingle(c[5], 16, tag: 3).End().ToArray();
        float[] output = new float[6];
        Assert.That(decoder.Decode(packet, output), Is.EqualTo(1));
        Assert.That(output.Select(v => (int)Math.Round(v * 32768)), Is.EqualTo(new[] { 200, 300, 100, 600, 400, 500 }));
    }

    [Test]
    public void Decode_DamagedPackets_ThrowInvalidData()
    {
        AlacFrameDecoder decoder = new(AlacPacketBuilder.Cookie(16, 16, 2));
        float[] output = new float[32];
        int[] samples = Ramp(16, 16, 7);
        Assert.Throws<InvalidDataException>(() => decoder.Decode([], output));
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new AlacPacketBuilder().EscapeSingle(samples, 16).ToArray(), output), "no end element");
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new AlacPacketBuilder().EscapePair(samples, samples, 16).End().ToArray().AsSpan(..20), output), "truncated");
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new AlacPacketBuilder().Header(1, 0, 1, unused: 1).End().ToArray(), output), "reserved bits");
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new AlacPacketBuilder().Header(1, 0, 1 | 6).End().ToArray(), output), "three shifted bytes");
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new AlacPacketBuilder().Header(2, 0).End().ToArray(), output), "coupling element");
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new AlacPacketBuilder().Header(5, 0).End().ToArray(), output), "program config element");
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new AlacPacketBuilder().Header(0, 0, 9).Bits(0, 32).End().ToArray(), output), "empty partial frame");
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new AlacPacketBuilder().Header(0, 0, 9).Bits(0, 16).Bits(17, 16).End().ToArray(), output), "partial frame longer than the frame");
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new AlacPacketBuilder().Header(0, 0, 0).Bits(0, 64).ToArray(), output), "compressed frame cut short");
        Assert.Throws<ArgumentException>(() => decoder.Decode(new AlacPacketBuilder().End().ToArray(), new float[31]));
    }

    [Test]
    public void Decode_RandomBytes_NeverEscapesTheContract()
    {
        AlacFrameDecoder decoder = new(AlacPacketBuilder.Cookie(4096, 24, 2));
        float[] output = new float[8192];
        Random random = new(11);
        byte[] packet = new byte[300];
        int decoded = 0;
        for (int round = 0; round < 3000; round++)
        {
            random.NextBytes(packet);
            // Start like a plausible element so that the deeper parsing is exercised too.
            packet[0] = (byte)(packet[0] & 0x1F | (round % 2 == 0 ? 0x20 : 0x00));
            packet[1] &= 0x0F;
            packet[2] = 0;
            packet[3] &= 0xF0;
            try
            {
                int frames = decoder.Decode(packet.AsSpan(0, 40 + round % 260), output);
                Assert.That(frames, Is.InRange(1, 4096));
                decoded++;
            }
            catch (InvalidDataException)
            {
            }
        }
        TestContext.Out.WriteLine($"{decoded} of 3000 random packets decoded");
    }
}

public class AlacConfigTests
{
    [Test]
    public void Parse_ReadsTheFieldsAndSkipsAtomHeaders()
    {
        foreach (bool atoms in new[] { false, true })
        {
            AlacConfig config = AlacConfig.Parse(AlacPacketBuilder.Cookie(4096, 24, 2, 96000, atomHeaders: atoms));
            Assert.That((config.FrameLength, config.BitDepth, config.Pb, config.Mb, config.Kb, config.Channels, config.MaxRun, config.SampleRate), Is.EqualTo((4096, 24, 40, 10, 14, 2, 255, 96000)));
        }
    }

    [Test]
    public void Parse_RefusesWhatItCannotDecode()
    {
        Assert.Throws<InvalidDataException>(() => AlacConfig.Parse(new byte[10]));
        Assert.Throws<NotSupportedException>(() => AlacConfig.Parse(AlacPacketBuilder.Cookie(4096, 16, 2, version: 1)));
        Assert.Throws<NotSupportedException>(() => AlacConfig.Parse(AlacPacketBuilder.Cookie(4096, 12, 2)));
        Assert.Throws<NotSupportedException>(() => AlacConfig.Parse(AlacPacketBuilder.Cookie(4096, 16, 9)));
        Assert.Throws<NotSupportedException>(() => AlacConfig.Parse(AlacPacketBuilder.Cookie(4096, 16, 0)));
        Assert.Throws<InvalidDataException>(() => AlacConfig.Parse(AlacPacketBuilder.Cookie(0, 16, 2)));
        Assert.Throws<InvalidDataException>(() => AlacConfig.Parse(AlacPacketBuilder.Cookie(1 << 20, 16, 2)));
        Assert.Throws<InvalidDataException>(() => AlacConfig.Parse(AlacPacketBuilder.Cookie(4096, 16, 2, rate: 0)));
    }
}

public class Mp4AlacTrackTests
{
    [Test]
    public void Read_FilesThatAreNotAlacMp4_AreRefusedCleanly()
    {
        Assert.Throws<NotSupportedException>(() => Mp4AlacTrack.Read(new MemoryStream()));
        Assert.Throws<NotSupportedException>(() => Mp4AlacTrack.Read(new MemoryStream([0, 0, 0, 16, .. "ftypM4A "u8, 0, 0, 0, 0])));
        Assert.Throws<InvalidDataException>(() => Mp4AlacTrack.Read(new MemoryStream([0, 0, 255, 255, .. "moov12345678"u8])));
        Assert.Throws<NotSupportedException>(() => Mp4AlacTrack.Read(new MemoryStream([0, 0, 0, 8, .. "moov"u8])));
    }

    [Test]
    public void Read_DamagedFixtures_NeverThrowAnythingButTheDocumentedExceptions()
    {
        byte[] file = File.ReadAllBytes(AlacFixtureTests.Path("s16_stereo_44k.m4a"));
        Random random = new(5);
        for (int round = 0; round < 400; round++)
        {
            byte[] copy = (byte[])file.Clone();
            for (int hit = 0; hit < 1 + round % 4; hit++) copy[random.Next(round % 2 == 0 ? copy.Length - 3000 : 100, copy.Length)] ^= (byte)(1 << random.Next(8));
            int length = round % 5 == 0 ? random.Next(copy.Length) : copy.Length;
            try
            {
                using AlacDecoder decoder = AlacDecoder.Open(new MemoryStream(copy, 0, length));
                AlacFixtureTests.DecodeAll(decoder);
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
            }
        }
    }
}
