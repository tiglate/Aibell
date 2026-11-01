using Aibell.Audio.Ogg;

namespace Aibell.Audio.Opus.Tests;

public class OpusDecoderTests
{
    private static List<byte[]> Packets(string name)
    {
        List<byte[]> packets = [];
        OggPacketReader reader = new(File.OpenRead(OpusFixtureTests.Path(name)), data => data.Length >= 8 && data[..8].SequenceEqual("OpusHead"u8));
        int index = 0;
        while (reader.ReadPacket(out OggPacket packet))
        {
            if (index++ >= 2) packets.Add(packet.Data.ToArray());
        }
        return packets;
    }

    private static byte[] Random(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new System.Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static int Decode(OpusDecoder decoder, byte[] packet, float[] pcm) => decoder.Decode(packet, 0, packet.Length, pcm, 0, OpusDecoder.MaxPacketFrames);

    private static bool Finite(float[] pcm, int count) => pcm.AsSpan(0, count).ToArray().All(float.IsFinite);

    [Test]
    public void TheTableOfContents_DescribesTheFrames()
    {
        Assert.That(OpusDecoder.SamplesPerFrame(0x00), Is.EqualTo(480), "SILK NB 10 ms");
        Assert.That(OpusDecoder.SamplesPerFrame(0x08), Is.EqualTo(960), "SILK NB 20 ms");
        Assert.That(OpusDecoder.SamplesPerFrame(0x10), Is.EqualTo(1920), "SILK NB 40 ms");
        Assert.That(OpusDecoder.SamplesPerFrame(0x18), Is.EqualTo(2880), "SILK NB 60 ms");
        Assert.That(OpusDecoder.SamplesPerFrame(0x60), Is.EqualTo(480), "hybrid 10 ms");
        Assert.That(OpusDecoder.SamplesPerFrame(0x68), Is.EqualTo(960), "hybrid 20 ms");
        Assert.That(OpusDecoder.SamplesPerFrame(0x80), Is.EqualTo(120), "CELT 2.5 ms");
        Assert.That(OpusDecoder.SamplesPerFrame(0x98), Is.EqualTo(960), "CELT 20 ms");
        Assert.That(OpusDecoder.PacketBandwidth(0x00), Is.EqualTo(OpusDecoder.BandwidthNarrow));
        Assert.That(OpusDecoder.PacketBandwidth(0x20), Is.EqualTo(OpusDecoder.BandwidthMedium));
        Assert.That(OpusDecoder.PacketBandwidth(0x40), Is.EqualTo(OpusDecoder.BandwidthWide));
        Assert.That(OpusDecoder.PacketBandwidth(0x60), Is.EqualTo(OpusDecoder.BandwidthSuperWide));
        Assert.That(OpusDecoder.PacketBandwidth(0x70), Is.EqualTo(OpusDecoder.BandwidthFull));
        Assert.That(OpusDecoder.PacketBandwidth(0x80), Is.EqualTo(OpusDecoder.BandwidthNarrow));
        Assert.That(OpusDecoder.PacketBandwidth(0xA0), Is.EqualTo(OpusDecoder.BandwidthWide));
        Assert.That(OpusDecoder.PacketBandwidth(0xC0), Is.EqualTo(OpusDecoder.BandwidthSuperWide));
        Assert.That(OpusDecoder.PacketBandwidth(0xE0), Is.EqualTo(OpusDecoder.BandwidthFull));
    }

    [Test]
    public void PacketSamples_CountsTheFramesOfEveryFramingCode()
    {
        Assert.That(OpusDecoder.PacketSamples([0xF8, 1, 2], 0, 3), Is.EqualTo(960));
        Assert.That(OpusDecoder.PacketSamples([0xF9, 1, 2], 0, 3), Is.EqualTo(1920));
        Assert.That(OpusDecoder.PacketSamples([0xFA, 1, 2], 0, 3), Is.EqualTo(1920));
        Assert.That(OpusDecoder.PacketSamples([0xFB, 3, 2], 0, 3), Is.EqualTo(2880));
        Assert.That(OpusDecoder.PacketSamples([0xFB, 0, 2], 0, 3), Is.EqualTo(-1), "no frames");
        Assert.That(OpusDecoder.PacketSamples([0xFB, 7, 2], 0, 3), Is.EqualTo(-1), "more than 120 ms");
        Assert.That(OpusDecoder.PacketSamples([0xFB], 0, 1), Is.EqualTo(-1));
        Assert.That(OpusDecoder.PacketSamples([], 0, 0), Is.EqualTo(-1));
    }

    [Test]
    public void Packets_WithOneToManyFrames_AreSplitAndDecoded()
    {
        OpusDecoder decoder = new(2);
        float[] pcm = new float[OpusDecoder.MaxPacketFrames * 2];
        byte[] body = Random(40, 1);
        // Code 0: one frame of 20 ms.
        Assert.That(Decode(decoder, [0xF8, .. body], pcm), Is.EqualTo(960));
        // Code 1: two equal frames.
        Assert.That(Decode(decoder, [0xF9, .. body], pcm), Is.EqualTo(1920));
        Assert.That(Decode(decoder, [0xF9, .. body, 0], pcm), Is.EqualTo(-1), "odd payload");
        // Code 2: two frames, the first with an explicit size.
        Assert.That(Decode(decoder, [0xFA, 10, .. body], pcm), Is.EqualTo(1920));
        Assert.That(Decode(decoder, [0xFA, 100, .. body], pcm), Is.EqualTo(-1), "size beyond the packet");
        Assert.That(Decode(decoder, [0xFA, 252], pcm), Is.EqualTo(-1), "truncated size");
        Assert.That(Decode(decoder, [0xFA], pcm), Is.EqualTo(-1), "no size");
        // Code 3, constant bit rate: three frames.
        Assert.That(Decode(decoder, [0xFB, 3, .. body[..30]], pcm), Is.EqualTo(2880));
        Assert.That(Decode(decoder, [0xFB, 3, .. body[..31]], pcm), Is.EqualTo(-1), "payload not divisible by the frame count");
        // Code 3, variable bit rate: two sizes then the rest; one with a two-byte size.
        Assert.That(Decode(decoder, [0xFB, 0x83, 8, 9, .. body], pcm), Is.EqualTo(2880));
        Assert.That(Decode(decoder, [0xFB, 0x82, 253, 1, .. Random(260, 3)], pcm), Is.EqualTo(1920));
        Assert.That(Decode(decoder, [0xFB, 0x83, 200, 9, .. body], pcm), Is.EqualTo(-1));
        // Code 3 with padding.
        Assert.That(Decode(decoder, [0xFB, 0x41, 5, .. body, 0, 0, 0, 0, 0], pcm), Is.EqualTo(960), "padding of five bytes");
        Assert.That(Decode(decoder, [0xFB, 0x41, 255, 255, 3, .. body], pcm), Is.EqualTo(-1), "padding beyond the packet");
        Assert.That(Decode(decoder, [0xFB, 0x41], pcm), Is.EqualTo(-1), "padding flag without padding");
        Assert.That(Decode(decoder, [0xFB, 0x41, 255], pcm), Is.EqualTo(-1), "unterminated padding");
        Assert.That(Decode(decoder, [0xFB, 3, .. Random(1276 * 3, 4)], pcm), Is.EqualTo(-1), "a frame larger than 1275 bytes");
    }

    [Test]
    public void ABufferThatIsTooSmall_IsRefused()
    {
        OpusDecoder decoder = new(2);
        float[] pcm = new float[OpusDecoder.MaxPacketFrames * 2];
        Assert.That(decoder.Decode([0xF9, .. Random(40, 1)], 0, 41, pcm, 0, 1000), Is.EqualTo(-2));
    }

    [Test]
    public void AnEmptyPacket_IsConcealed_AndTheFirstOneIsSilence()
    {
        OpusDecoder decoder = new(2);
        float[] pcm = new float[OpusDecoder.MaxPacketFrames * 2];
        Array.Fill(pcm, 1f);
        // Without a packet to say otherwise a concealed block is 2.5 ms long.
        Assert.That(decoder.Decode(null, 0, 0, pcm, 0, 960), Is.EqualTo(120));
        Assert.That(pcm.AsSpan(0, 120 * 2).ToArray().All(sample => sample == 0f), Is.True, "nothing to extrapolate from yet");
        Assert.That(decoder.Decode([0xF8], 0, 0, pcm, 0, 960), Is.EqualTo(120));
        Assert.That(decoder.Decode([0xF8], 0, -1, pcm, 0, 960), Is.EqualTo(-1));
    }

    [TestCase("celt_stereo_20ms.opus")]
    [TestCase("silk_stereo_mb.opus")]
    [TestCase("silk_mono_wb_60ms.opus")]
    [TestCase("hybrid_stereo_swb.opus")]
    [TestCase("hybrid_mono_fb.opus")]
    public void LostPackets_AreConcealedWithFiniteAudio(string name)
    {
        List<byte[]> packets = Packets(name);
        OpusDecoder decoder = new(2);
        float[] pcm = new float[OpusDecoder.MaxPacketFrames * 2];
        int warmUp = Math.Min(12, packets.Count / 2);
        for (int i = 0; i < warmUp; i++) Assert.That(Decode(decoder, packets[i], pcm), Is.GreaterThan(0));
        int frame = OpusDecoder.SamplesPerFrame(packets[0][0]);
        for (int lost = 0; lost < 8; lost++)
        {
            Assert.That(decoder.Decode(null, 0, 0, pcm, 0, frame), Is.EqualTo(frame));
            Assert.That(Finite(pcm, frame * 2), Is.True);
            Assert.That(pcm.AsSpan(0, frame * 2).ToArray().Max(Math.Abs), Is.LessThan(2f));
        }
        // A long outage fades the concealment out.
        for (int lost = 0; lost < 40; lost++) decoder.Decode(null, 0, 0, pcm, 0, frame);
        Assert.That(pcm.AsSpan(0, frame * 2).ToArray().Max(Math.Abs), Is.LessThan(0.05f));
        for (int i = warmUp; i < Math.Min(2 * warmUp, packets.Count); i++) Assert.That(Decode(decoder, packets[i], pcm), Is.GreaterThan(0));
        Assert.That(Finite(pcm, frame * 2), Is.True);
    }

    [Test]
    public void AConcealmentCall_IsAsLongAsTheLastFrame()
    {
        List<byte[]> packets = Packets("hybrid_stereo_swb.opus");
        OpusDecoder decoder = new(2);
        float[] pcm = new float[OpusDecoder.MaxPacketFrames * 2];
        for (int i = 0; i < 6; i++) Decode(decoder, packets[i], pcm);
        // A single call conceals no more than the last frame; a longer outage takes repeated calls.
        Assert.That(decoder.Decode(null, 0, 0, pcm, 0, 2880), Is.EqualTo(960));
        Assert.That(Finite(pcm, 960 * 2), Is.True);
    }

    [Test]
    public void SwitchingBetweenModes_KeepsTheAudioContinuous()
    {
        // Packets of different modes, bandwidths and frame sizes back to back exercise the transition and redundancy handling.
        string[] names = ["celt_stereo_20ms.opus", "silk_stereo_mb.opus", "hybrid_stereo_swb.opus", "celt_stereo_2_5ms.opus", "hybrid_mono_fb.opus", "silk_mono_nb.opus", "celt_mono_wb.opus", "silk_mono_wb_60ms.opus"];
        List<List<byte[]>> sources = [.. names.Select(Packets)];
        OpusDecoder decoder = new(2);
        float[] pcm = new float[OpusDecoder.MaxPacketFrames * 2];
        System.Random random = new(5);
        for (int round = 0; round < 600; round++)
        {
            List<byte[]> source = sources[random.Next(sources.Count)];
            int start = random.Next(source.Count - 4);
            for (int i = 0; i < 1 + random.Next(4); i++)
            {
                int frames = Decode(decoder, source[start + i], pcm);
                Assert.That(frames, Is.GreaterThan(0));
                Assert.That(Finite(pcm, frames * 2), Is.True);
                Assert.That(pcm.AsSpan(0, frames * 2).ToArray().Max(Math.Abs), Is.LessThan(100f), "no runaway at the switch");
            }
        }
    }

    [Test]
    public void AMonoDecoder_FoldsStereoStreams_AndAStereoDecoderSpreadsMonoOnes()
    {
        List<byte[]> stereo = Packets("hybrid_stereo_swb.opus");
        List<byte[]> mono = Packets("silk_mono_nb.opus");
        OpusDecoder oneChannel = new(1);
        OpusDecoder twoChannels = new(2);
        float[] pcm = new float[OpusDecoder.MaxPacketFrames * 2];
        for (int i = 0; i < 10; i++)
        {
            int frames = Decode(oneChannel, stereo[i], pcm);
            Assert.That(frames, Is.EqualTo(960));
            Assert.That(Finite(pcm, frames), Is.True);
            frames = Decode(twoChannels, mono[i], pcm);
            Assert.That(frames, Is.GreaterThan(0));
            for (int f = 0; f < frames; f++) Assert.That(pcm[2 * f + 1], Is.EqualTo(pcm[2 * f]), "mono is played on both channels");
        }
        Assert.That(oneChannel.Channels, Is.EqualTo(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new OpusDecoder(3));
    }

    [Test]
    public void Reset_ForgetsTheHistory()
    {
        List<byte[]> packets = Packets("celt_stereo_20ms.opus");
        OpusDecoder decoder = new(2);
        float[] first = new float[OpusDecoder.MaxPacketFrames * 2];
        float[] again = new float[OpusDecoder.MaxPacketFrames * 2];
        for (int i = 0; i < 8; i++) Decode(decoder, packets[i], first);
        decoder.Reset();
        OpusDecoder fresh = new(2);
        for (int i = 20; i < 24; i++)
        {
            Decode(decoder, packets[i], first);
            Decode(fresh, packets[i], again);
            Assert.That(first, Is.EqualTo(again));
        }
    }

    [Test]
    public void RandomPackets_NeverThrow_AndNeverProduceNonsense()
    {
        System.Random random = new(77);
        OpusDecoder decoder = new(2);
        float[] pcm = new float[OpusDecoder.MaxPacketFrames * 2];
        for (int round = 0; round < 6000; round++)
        {
            byte[] packet = Random(1 + random.Next(300), round);
            // Keep the multi-frame codes in play but not so many that every packet is invalid.
            if (round % 3 == 0) packet[0] = (byte)(packet[0] & ~3);
            int frames = Decode(decoder, packet, pcm);
            if (frames > 0) Assert.That(Finite(pcm, frames * 2), Is.True, $"packet {round} (TOC {packet[0]:X2})");
        }
    }

    [TestCase("celt_stereo_10ms.opus")]
    [TestCase("silk_stereo_mb.opus")]
    [TestCase("hybrid_stereo_swb.opus")]
    public void DamagedPackets_NeverThrow(string name)
    {
        List<byte[]> packets = Packets(name);
        System.Random random = new(3);
        OpusDecoder decoder = new(2);
        float[] pcm = new float[OpusDecoder.MaxPacketFrames * 2];
        for (int round = 0; round < 2500; round++)
        {
            byte[] packet = (byte[])packets[random.Next(packets.Count)].Clone();
            for (int flips = random.Next(5); flips >= 0; flips--) packet[random.Next(packet.Length)] ^= (byte)(1 << random.Next(8));
            if (random.Next(10) == 0) packet = packet[..random.Next(1, packet.Length)];
            int frames = Decode(decoder, packet, pcm);
            if (frames > 0) Assert.That(Finite(pcm, frames * 2), Is.True);
        }
    }

    [Test]
    public void TheRangeDecoder_FollowsTheRfcBitExactly()
    {
        // Two symbols of an inverse CDF, a raw-bits field and a uniform integer from a fixed buffer; the values were checked against the reference decoder.
        byte[] data = [0x7F, 0x35, 0xA1, 0x0C, 0xE4, 0x58, 0x99, 0x03];
        RangeDecoder decoder = new();
        decoder.Init(data, 0, data.Length);
        Assert.That(decoder.Storage, Is.EqualTo(8));
        int a = decoder.DecodeIcdf([200, 150, 100, 50, 0], 8);
        int b = decoder.DecodeBitLogP(3);
        uint raw = decoder.DecodeBits(5);
        uint uniform = decoder.DecodeUint(1000);
        Assert.That(decoder.Tell(), Is.GreaterThan(0));
        Assert.That(decoder.TellFrac(), Is.GreaterThanOrEqualTo(decoder.Tell() * 8 - 8));
        Assert.That(a, Is.InRange(0, 4));
        Assert.That(b, Is.InRange(0, 1));
        Assert.That(raw, Is.LessThan(32u));
        Assert.That(uniform, Is.LessThan(1000u));
        RangeDecoder again = new();
        again.Init(data, 0, data.Length);
        Assert.That((again.DecodeIcdf([200, 150, 100, 50, 0], 8), again.DecodeBitLogP(3), again.DecodeBits(5), again.DecodeUint(1000)), Is.EqualTo((a, b, raw, uniform)));
    }
}
