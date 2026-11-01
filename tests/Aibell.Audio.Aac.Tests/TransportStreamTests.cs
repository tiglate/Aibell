using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

[TestFixture]
public sealed class TransportStreamTests
{
    private static List<byte[]> Frames(int count, int seed = 1)
    {
        Random random = new(seed);
        List<byte[]> frames = [];
        for (int i = 0; i < count; i++) frames.Add(AacTestData.Frame(random.Next(20, 400), fill: (byte)(0x20 + i % 50)));
        return frames;
    }

    private static byte[] Run(byte[] stream, int chunk, out TransportStreamDemuxer demuxer, Action? discontinuity = null)
    {
        MemoryStream collected = new();
        demuxer = new TransportStreamDemuxer(data => collected.Write(data), discontinuity);
        for (int offset = 0; offset < stream.Length; offset += chunk) demuxer.Push(stream.AsSpan(offset, Math.Min(chunk, stream.Length - offset)));
        demuxer.Complete();
        return collected.ToArray();
    }

    private static byte[] Concat(List<byte[]> frames) => [.. frames.SelectMany(frame => frame)];

    [TestCase(188)]
    [TestCase(1)]
    [TestCase(77)]
    [TestCase(100000)]
    public void Push_ExtractsTheAdtsStreamRegardlessOfChunking(int chunk)
    {
        List<byte[]> frames = Frames(30);
        byte[] result = Run(TestMpegTs.Mux(frames), chunk, out TransportStreamDemuxer demuxer);
        Assert.That(result, Is.EqualTo(Concat(frames)));
        Assert.That(demuxer.Kind, Is.EqualTo(TransportAudioKind.AacAdts));
        Assert.That(demuxer.SkippedBytes, Is.Zero);
    }

    [Test]
    public void Push_ReportsOtherCodecs()
    {
        Run(TestMpegTs.Mux(Frames(4), stream: 0x81), 188, out TransportStreamDemuxer ac3);
        Assert.That(ac3.Kind, Is.EqualTo(TransportAudioKind.Ac3));
        Run(TestMpegTs.Mux(Frames(4), stream: 0x03), 188, out TransportStreamDemuxer mpeg);
        Assert.That(mpeg.Kind, Is.EqualTo(TransportAudioKind.MpegAudio));
    }

    [Test]
    public void Push_ResynchronizesAfterGarbageAndAtAMidPacketStart()
    {
        List<byte[]> frames = Frames(40);
        byte[] stream = TestMpegTs.Mux(frames);
        byte[] damaged = [.. new byte[] { 1, 2, 3, 0x47, 5 }, .. stream.AsSpan(188 * 2 + 50).ToArray()];
        int discontinuities = 0;
        byte[] result = Run(damaged, 100, out TransportStreamDemuxer demuxer, () => discontinuities++);
        // Without its tables the audio stream is unknown, so nothing is produced and nothing crashes.
        Assert.That(result, Is.Empty);
        Assert.That(demuxer.SkippedBytes, Is.GreaterThan(0));

        byte[] withTablesLater = [.. stream.AsSpan(0, 188 * 2).ToArray(), .. new byte[37], .. stream.AsSpan(188 * 2).ToArray()];
        result = Run(withTablesLater, 188, out demuxer, () => discontinuities++);
        Assert.That(demuxer.SkippedBytes, Is.EqualTo(37));
        Assert.That(discontinuities, Is.GreaterThan(0));
        AdtsFrameReader reader = new();
        result.CopyTo(reader.GetWriteBuffer());
        reader.Advance(result.Length);
        reader.Complete();
        int count = 0;
        while (reader.TryRead(out _, out _)) count++;
        Assert.That(count, Is.GreaterThan(30));
    }

    [Test]
    public void Push_LostPacketsRaiseADiscontinuity()
    {
        byte[] stream = TestMpegTs.Mux(Frames(60), framesPerPes: 6);
        // Remove one audio packet from the middle.
        int cut = 188 * 12;
        byte[] gap = [.. stream.AsSpan(0, cut).ToArray(), .. stream.AsSpan(cut + 188).ToArray()];
        int discontinuities = 0;
        Run(gap, 188, out _, () => discontinuities++);
        Assert.That(discontinuities, Is.EqualTo(1));
    }

    [Test]
    public void StartSegment_ForgetsContinuityBetweenSegments()
    {
        List<byte[]> frames = Frames(10);
        byte[] first = TestMpegTs.Mux(frames, firstContinuity: 0);
        byte[] second = TestMpegTs.Mux(frames, firstContinuity: 9);
        MemoryStream collected = new();
        int discontinuities = 0;
        TransportStreamDemuxer demuxer = new(data => collected.Write(data), () => discontinuities++);
        demuxer.Push(first);
        demuxer.StartSegment();
        demuxer.Push(second);
        demuxer.Complete();
        Assert.That(discontinuities, Is.Zero);
        Assert.That(collected.ToArray(), Is.EqualTo(Concat([.. frames, .. frames])));
    }

    [Test]
    public void Push_RandomBytesAndCorruptedStreamsNeverThrow()
    {
        Random random = new(3);
        byte[] good = TestMpegTs.Mux(Frames(80));
        for (int round = 0; round < 200; round++)
        {
            byte[] data = (byte[])good.Clone();
            for (int i = random.Next(1, 40); i > 0; i--) data[random.Next(data.Length)] = (byte)random.Next(256);
            Run(data, random.Next(1, 600), out _);
            byte[] noise = new byte[random.Next(0, 5000)];
            random.NextBytes(noise);
            for (int i = 0; i < noise.Length; i += 188) noise[i] = 0x47;
            Run(noise, random.Next(1, 600), out _);
        }
    }

    [Test]
    public void TransportStreamOfARealFixture_DecodesLikeTheAdtsOriginal()
    {
        byte[] data = File.ReadAllBytes(ConformanceTests.FixturePath("long_sweep_44k_stereo.aac"));
        AdtsFrameReader reader = new();
        data.CopyTo(reader.GetWriteBuffer());
        reader.Advance(data.Length);
        reader.Complete();
        List<byte[]> frames = [];
        // Copy whole frames (header included) back out.
        int offset = 0;
        while (offset < data.Length && AdtsHeader.TryParse(data.AsSpan(offset), out AdtsHeader header) && offset + header.FrameLength <= data.Length)
        {
            frames.Add(data.AsSpan(offset, header.FrameLength).ToArray());
            offset += header.FrameLength;
        }
        byte[] extracted = Run(TestMpegTs.Mux(frames, framesPerPes: 3), 4096, out _);
        Assert.That(extracted, Is.EqualTo(data));
    }

    [TestCase(188)]
    [TestCase(333)]
    public void FfmpegTransportStream_YieldsTheOriginalAdtsBytes(int chunk)
    {
        byte[] original = File.ReadAllBytes(ConformanceTests.FixturePath("ms_22k_stereo.aac"));
        byte[] extracted = Run(File.ReadAllBytes(ConformanceTests.FixturePath("ms_22k_stereo.ts")), chunk, out TransportStreamDemuxer demuxer);
        Assert.That(demuxer.Kind, Is.EqualTo(TransportAudioKind.AacAdts));
        Assert.That(extracted, Is.EqualTo(original));
    }
}
