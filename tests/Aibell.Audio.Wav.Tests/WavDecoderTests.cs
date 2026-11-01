using System.Buffers.Binary;

namespace Aibell.Audio.Wav.Tests;

public class WavDecoderTests
{
    private static float[] ReadAll(WavDecoder decoder)
    {
        List<float> all = [];
        float[] block = new float[decoder.Channels * 100];
        int got;
        while ((got = decoder.Read(block, 100)) > 0) all.AddRange(block.AsSpan(0, got * decoder.Channels).ToArray());
        return [.. all];
    }

    [Test]
    public void Pcm16_DecodesExactly()
    {
        short[] samples = [0, 1, -1, 32767, -32768, 12345, -12345, 100, 200, 300];
        using WavDecoder decoder = WavDecoder.Open(new MemoryStream(WavBuilder.Build(1, 2, 44100, 16, WavBuilder.Pcm16(samples))));
        Assert.That(decoder.SampleRate, Is.EqualTo(44100));
        Assert.That(decoder.Channels, Is.EqualTo(2));
        Assert.That(decoder.TotalFrames, Is.EqualTo(5));
        float[] pcm = ReadAll(decoder);
        Assert.That(pcm, Has.Length.EqualTo(10));
        for (int i = 0; i < samples.Length; i++) Assert.That(pcm[i], Is.EqualTo(samples[i] / 32768f));
    }

    [Test]
    public void Pcm16_LongRun_CoversTheVectorizedPath()
    {
        short[] samples = new short[10007 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(i * 7919);
        using WavDecoder decoder = WavDecoder.Open(new MemoryStream(WavBuilder.Build(1, 2, 48000, 16, WavBuilder.Pcm16(samples))));
        float[] pcm = ReadAll(decoder);
        Assert.That(pcm, Has.Length.EqualTo(samples.Length));
        for (int i = 0; i < samples.Length; i++) Assert.That(pcm[i], Is.EqualTo(samples[i] / 32768f));
    }

    [Test]
    public void Pcm8_IsUnsigned()
    {
        using WavDecoder decoder = WavDecoder.Open(new MemoryStream(WavBuilder.Build(1, 1, 8000, 8, [0, 128, 255])));
        Assert.That(ReadAll(decoder), Is.EqualTo(new[] { -1f, 0f, 127f / 128f }));
    }

    [Test]
    public void Pcm24_AndPcm32_AreSigned()
    {
        byte[] data24 = [0x00, 0x00, 0x80, 0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x40];
        using WavDecoder d24 = WavDecoder.Open(new MemoryStream(WavBuilder.Build(1, 1, 44100, 24, data24)));
        Assert.That(ReadAll(d24), Is.EqualTo(new[] { -1f, 8388607f / 8388608f, 0.5f }));
        byte[] data32 = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data32, int.MinValue);
        BinaryPrimitives.WriteInt32LittleEndian(data32.AsSpan(4), 1 << 30);
        using WavDecoder d32 = WavDecoder.Open(new MemoryStream(WavBuilder.Build(1, 1, 44100, 32, data32)));
        Assert.That(ReadAll(d32), Is.EqualTo(new[] { -1f, 0.5f }));
    }

    [Test]
    public void Float32AndFloat64_ReplaceNonFiniteValuesWithSilence()
    {
        byte[] f32 = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(f32, 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(f32.AsSpan(4), float.NaN);
        BinaryPrimitives.WriteSingleLittleEndian(f32.AsSpan(8), float.PositiveInfinity);
        using WavDecoder a = WavDecoder.Open(new MemoryStream(WavBuilder.Build(3, 1, 44100, 32, f32)));
        Assert.That(ReadAll(a), Is.EqualTo(new[] { 0.25f, 0f, 0f }));
        byte[] f64 = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(f64, -0.75);
        using WavDecoder b = WavDecoder.Open(new MemoryStream(WavBuilder.Build(3, 1, 44100, 64, f64)));
        Assert.That(ReadAll(b), Is.EqualTo(new[] { -0.75f }));
    }

    [Test]
    public void ALawAndMuLaw_MatchTheCompandingLaws()
    {
        // G.711: mu-law 0xFF is zero, 0x80 and 0x00 are the positive and negative extremes (+/-32124); A-law 0xD5 is +8 and 0x55 is -8, 0xAA/0x2A are +/-32256.
        using WavDecoder mu = WavDecoder.Open(new MemoryStream(WavBuilder.Build(7, 1, 8000, 8, [0xFF, 0x80, 0x00, 0x7F])));
        Assert.That(ReadAll(mu), Is.EqualTo(new[] { 0f, 32124f / 32768f, -32124f / 32768f, 0f }));
        using WavDecoder a = WavDecoder.Open(new MemoryStream(WavBuilder.Build(6, 1, 8000, 8, [0xD5, 0x55, 0xAA, 0x2A])));
        Assert.That(ReadAll(a), Is.EqualTo(new[] { 8f / 32768f, -8f / 32768f, 32256f / 32768f, -32256f / 32768f }));
    }

    [Test]
    public void Extensible_UsesTheSubformat()
    {
        using WavDecoder decoder = WavDecoder.Open(new MemoryStream(WavBuilder.Build(1, 2, 96000, 16, WavBuilder.Pcm16(100, -100), extensible: true)));
        Assert.That(decoder.SampleRate, Is.EqualTo(96000));
        Assert.That(ReadAll(decoder), Is.EqualTo(new[] { 100 / 32768f, -100 / 32768f }));
    }

    [Test]
    public void Rf64_TakesTheDataSizeFromDs64()
    {
        using WavDecoder decoder = WavDecoder.Open(new MemoryStream(WavBuilder.Build(1, 1, 44100, 16, WavBuilder.Pcm16(1, 2, 3, 4), rf64: true)));
        Assert.That(decoder.TotalFrames, Is.EqualTo(4));
        Assert.That(ReadAll(decoder), Has.Length.EqualTo(4));
    }

    [Test]
    public void OtherChunks_AreSkipped_AndTrailingChunksAreNotAudio()
    {
        byte[] file = WavBuilder.Build(1, 1, 44100, 16, WavBuilder.Pcm16(5, 6), extraChunk: [1, 2, 3]);
        byte[] withTrailer = [.. file, .. "LIST"u8.ToArray(), 4, 0, 0, 0, 9, 9, 9, 9];
        using WavDecoder decoder = WavDecoder.Open(new MemoryStream(withTrailer));
        Assert.That(ReadAll(decoder), Is.EqualTo(new[] { 5 / 32768f, 6 / 32768f }));
    }

    [TestCase(0u)]
    [TestCase(uint.MaxValue)]
    public void UnknownDataSize_RunsToTheEndOfTheStream(uint size)
    {
        byte[] file = WavBuilder.Build(1, 1, 44100, 16, WavBuilder.Pcm16(1, 2, 3), dataSize: size);
        using WavDecoder seekable = WavDecoder.Open(new MemoryStream(file));
        Assert.That(ReadAll(seekable), Has.Length.EqualTo(3));
        Assert.That(seekable.TotalFrames, Is.EqualTo(3));
        using WavDecoder forward = WavDecoder.Open(new ForwardOnlyStream(file));
        Assert.That(forward.TotalFrames, Is.EqualTo(-1));
        Assert.That(ReadAll(forward), Has.Length.EqualTo(3));
        Assert.That(forward.Seek(0), Is.False);
    }

    [Test]
    public void TruncatedData_StopsAtTheLastWholeFrame()
    {
        byte[] file = WavBuilder.Build(1, 2, 44100, 16, WavBuilder.Pcm16(1, 2, 3, 4, 5, 6));
        using WavDecoder decoder = WavDecoder.Open(new MemoryStream(file[..^5]));
        Assert.That(ReadAll(decoder), Has.Length.EqualTo(2));
    }

    [Test]
    public void ForwardOnlyStream_DecodesTheSameAsASeekableOne()
    {
        short[] samples = new short[5000];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(i * 31);
        byte[] file = WavBuilder.Build(1, 2, 44100, 16, WavBuilder.Pcm16(samples), extraChunk: new byte[33]);
        using WavDecoder a = WavDecoder.Open(new MemoryStream(file));
        using WavDecoder b = WavDecoder.Open(new ForwardOnlyStream(file));
        Assert.That(ReadAll(b), Is.EqualTo(ReadAll(a)));
    }

    [Test]
    public void Seek_IsExact_AndRewindsAfterTheEnd()
    {
        short[] samples = new short[2000];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(i * 3);
        using WavDecoder decoder = WavDecoder.Open(new MemoryStream(WavBuilder.Build(1, 2, 44100, 16, WavBuilder.Pcm16(samples))));
        float[] all = ReadAll(decoder);
        Assert.That(decoder.Read(new float[2], 1), Is.EqualTo(0));
        Assert.That(decoder.Seek(700), Is.True);
        float[] tail = ReadAll(decoder);
        Assert.That(tail, Is.EqualTo(all[1400..]));
        Assert.That(decoder.Seek(1_000_000), Is.True);
        Assert.That(ReadAll(decoder), Is.Empty);
        Assert.That(decoder.Seek(0), Is.True);
        Assert.That(ReadAll(decoder), Is.EqualTo(all));
    }

    [Test]
    public void StreamsWithoutALength_AreTreatedAsForwardOnly()
    {
        byte[] file = WavBuilder.Build(1, 1, 44100, 16, WavBuilder.Pcm16(1, 2, 3, 4), extraChunk: new byte[900]);
        using WavDecoder decoder = WavDecoder.Open(new NoLengthStream(file));
        Assert.That(decoder.TotalFrames, Is.EqualTo(4));
        Assert.That(ReadAll(decoder), Has.Length.EqualTo(4));
        Assert.That(decoder.Seek(0), Is.False);
    }

    [Test]
    public void Sniff_RecognizesWaveAndRf64()
    {
        Assert.That(WavDecoder.Sniff(WavBuilder.Build(1, 1, 8000, 16, WavBuilder.Pcm16(1))), Is.True);
        Assert.That(WavDecoder.Sniff(WavBuilder.Build(1, 1, 8000, 16, WavBuilder.Pcm16(1), rf64: true)), Is.True);
        Assert.That(WavDecoder.Sniff("RIFF0000AVI LIST"u8), Is.False);
        Assert.That(WavDecoder.Sniff("fLaC"u8), Is.False);
    }

    [Test]
    public void Unsupported_AndInvalidFiles_AreRefused()
    {
        Assert.Throws<NotSupportedException>(() => WavDecoder.Open(new MemoryStream(WavBuilder.Build(2, 1, 8000, 4, [0, 0]))));
        Assert.Throws<InvalidDataException>(() => WavDecoder.Open(new MemoryStream("not a wave file at all"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => WavDecoder.Open(new MemoryStream("RIFF"u8.ToArray())));
        byte[] zeroChannels = WavBuilder.Build(1, 1, 8000, 16, WavBuilder.Pcm16(1));
        zeroChannels[22] = 0;
        Assert.Throws<InvalidDataException>(() => WavDecoder.Open(new MemoryStream(zeroChannels)));
    }

    [Test]
    public void DamagedFiles_NeverEscapeWithAnotherException()
    {
        byte[] good = WavBuilder.Build(1, 2, 44100, 16, WavBuilder.Pcm16(new short[400]), extensible: true);
        Random random = new(11);
        for (int round = 0; round < 3000; round++)
        {
            byte[] bad = (byte[])good.Clone();
            int flips = 1 + random.Next(4);
            for (int f = 0; f < flips; f++) bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
            if (round % 3 == 0) bad = bad[..random.Next(bad.Length)];
            try
            {
                using WavDecoder decoder = WavDecoder.Open(new MemoryStream(bad));
                float[] block = new float[decoder.Channels * 64];
                while (decoder.Read(block, 64) > 0) { }
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
            }
        }
    }

    [Test]
    public void Decoding_AllocatesNothing()
    {
        byte[] file = WavBuilder.Build(1, 2, 44100, 16, WavBuilder.Pcm16(new short[200_000]));
        using WavDecoder decoder = WavDecoder.Open(new MemoryStream(file));
        float[] block = new float[2 * 1024];
        decoder.Read(block, 1024);
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (decoder.Read(block, 1024) > 0) { }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void Decoding_IsMuchFasterThanRealTime()
    {
        byte[] file = WavBuilder.Build(1, 2, 44100, 16, WavBuilder.Pcm16(new short[44100 * 2 * 60]));
        using WavDecoder decoder = WavDecoder.Open(new MemoryStream(file));
        float[] block = new float[2 * 4096];
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        while (decoder.Read(block, 4096) > 0) { }
        // A minute of audio: the bound is 1 % of real time, far above what the decoder needs.
        Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(0.6));
    }
}
