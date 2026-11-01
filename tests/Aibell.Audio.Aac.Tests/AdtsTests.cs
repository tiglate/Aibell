using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

[TestFixture]
public sealed class AdtsTests
{
    private static List<int> ReadAll(AdtsFrameReader reader, byte[] input, int chunk)
    {
        List<int> lengths = [];
        for (int offset = 0; offset < input.Length; offset += chunk)
        {
            int count = Math.Min(chunk, input.Length - offset);
            input.AsSpan(offset, count).CopyTo(reader.GetWriteBuffer());
            reader.Advance(count);
            while (reader.TryRead(out AdtsHeader header, out ReadOnlySpan<byte> payload))
            {
                Assert.That(payload.Length, Is.EqualTo(header.PayloadLength));
                lengths.Add(payload.Length);
            }
        }
        reader.Complete();
        while (reader.TryRead(out _, out ReadOnlySpan<byte> payload)) lengths.Add(payload.Length);
        return lengths;
    }

    [Test]
    public void Header_ParsesFields()
    {
        Assert.That(AdtsHeader.TryParse(AacTestData.Frame(100, samplingIndex: 3, channels: 2), out AdtsHeader header), Is.True);
        Assert.That((header.ObjectType, header.SampleRate, header.ChannelConfiguration, header.FrameLength, header.PayloadLength), Is.EqualTo((2, 48000, 2, 107, 100)));
    }

    [Test]
    public void Header_RejectsReservedSamplingIndexAndShortLength()
    {
        byte[] frame = AacTestData.Frame(10, samplingIndex: 14);
        Assert.That(AdtsHeader.TryParse(frame, out _), Is.False);
        Assert.That(AdtsHeader.TryParse(AacTestData.Frame(10).AsSpan(0, 5).ToArray(), out _), Is.False);
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(1000)]
    public void Reader_SplitsFramesRegardlessOfChunking(int chunk)
    {
        byte[] input = [.. AacTestData.Frame(50), .. AacTestData.Frame(120), .. AacTestData.Frame(3), .. AacTestData.Frame(300)];
        Assert.That(ReadAll(new AdtsFrameReader(), input, chunk), Is.EqualTo(new[] { 50, 120, 3, 300 }));
    }

    [Test]
    public void Reader_StartsMidFrameAndSkipsGarbage()
    {
        byte[] input = [.. AacTestData.Frame(80).AsSpan(40).ToArray(), .. AacTestData.Frame(60), .. AacTestData.Frame(61), 1, 2, 3, 0xff, 0x00, .. AacTestData.Frame(70), .. AacTestData.Frame(10)];
        AdtsFrameReader reader = new();
        Assert.That(ReadAll(reader, input, 64), Is.EqualTo(new[] { 60, 61, 70, 10 }));
        Assert.That(reader.SkippedBytes, Is.GreaterThan(0));
    }

    [Test]
    public void Reader_SkipsId3TagsBetweenSegments()
    {
        byte[] tag = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 20, .. new byte[20]];
        byte[] input = [.. tag, .. AacTestData.Frame(40), .. AacTestData.Frame(41), .. tag, .. AacTestData.Frame(42)];
        AdtsFrameReader reader = new();
        Assert.That(ReadAll(reader, input, 13), Is.EqualTo(new[] { 40, 41, 42 }));
        Assert.That(reader.SkippedBytes, Is.Zero);
    }

    [Test]
    public void Reader_ReportsEachFramesOffsetInTheInputEvenAfterGarbageAndCompaction()
    {
        byte[][] frames = [AacTestData.Frame(300), AacTestData.Frame(200), AacTestData.Frame(900)];
        byte[] input = [.. frames[0], .. frames[1], 9, 9, 9, .. frames[2], .. frames[2], .. frames[2], .. frames[2], .. frames[2]];
        long[] expected = [0, 307, 307 + 207 + 3];
        AdtsFrameReader reader = new();
        List<long> offsets = [];
        for (int at = 0; at < input.Length; at += 50)
        {
            int count = Math.Min(50, input.Length - at);
            input.AsSpan(at, count).CopyTo(reader.GetWriteBuffer());
            reader.Advance(count);
            while (reader.TryRead(out _, out _)) offsets.Add(reader.LastFrameOffset);
        }
        Assert.That(offsets.Take(3).ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void Reader_RejectsAStraySyncWordInsidePayload()
    {
        byte[] payloadWithSync = AacTestData.Frame(60);
        payloadWithSync[20] = 0xff;
        payloadWithSync[21] = 0xf1;
        byte[] input = [.. payloadWithSync, .. AacTestData.Frame(30), .. AacTestData.Frame(30)];
        Assert.That(ReadAll(new AdtsFrameReader(), input, 100), Is.EqualTo(new[] { 60, 30, 30 }));
    }

    [Test]
    public void Reader_TruncatedFinalFrameIsDropped()
    {
        byte[] input = [.. AacTestData.Frame(50), .. AacTestData.Frame(50), .. AacTestData.Frame(50).AsSpan(0, 30).ToArray()];
        Assert.That(ReadAll(new AdtsFrameReader(), input, 17), Is.EqualTo(new[] { 50, 50 }));
    }

    [Test]
    public void Reader_RandomBytesNeverThrowOrLoop()
    {
        Random random = new(11);
        for (int round = 0; round < 50; round++)
        {
            byte[] noise = new byte[random.Next(0, 70000)];
            random.NextBytes(noise);
            for (int i = 0; i < noise.Length; i += 97) noise[i] = 0xff;
            AdtsFrameReader reader = new();
            ReadAll(reader, noise, random.Next(1, 5000));
        }
    }

    [Test]
    public void MarkDiscontinuity_DropsPartialFrame()
    {
        AdtsFrameReader reader = new();
        byte[] first = AacTestData.Frame(100);
        first.AsSpan(0, 50).CopyTo(reader.GetWriteBuffer());
        reader.Advance(50);
        Assert.That(reader.TryRead(out _, out _), Is.False);
        reader.MarkDiscontinuity();
        Assert.That(reader.TakeGap(), Is.True);
        byte[] rest = [.. AacTestData.Frame(20), .. AacTestData.Frame(20)];
        rest.CopyTo(reader.GetWriteBuffer());
        reader.Advance(rest.Length);
        Assert.That(reader.TryRead(out AdtsHeader header, out _), Is.True);
        Assert.That(header.PayloadLength, Is.EqualTo(20));
    }
}
