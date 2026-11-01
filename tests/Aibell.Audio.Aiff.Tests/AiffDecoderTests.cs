using System.Buffers.Binary;

namespace Aibell.Audio.Aiff.Tests;

public class AiffDecoderTests
{
    private static float[] ReadAll(AiffDecoder decoder)
    {
        List<float> all = [];
        float[] block = new float[decoder.Channels * 100];
        int got;
        while ((got = decoder.Read(block, 100)) > 0) all.AddRange(block.AsSpan(0, got * decoder.Channels).ToArray());
        return [.. all];
    }

    [Test]
    public void Pcm16Be_DecodesExactly()
    {
        short[] samples = [0, 1, -1, 32767, -32768, 12345, -12345, 100, 200, 300];
        using AiffDecoder decoder = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(2, 44100, 16, AiffBuilder.Pcm16Be(samples))));
        Assert.That(decoder.SampleRate, Is.EqualTo(44100));
        Assert.That(decoder.Channels, Is.EqualTo(2));
        Assert.That(decoder.TotalFrames, Is.EqualTo(5));
        float[] pcm = ReadAll(decoder);
        Assert.That(pcm, Has.Length.EqualTo(10));
        for (int i = 0; i < samples.Length; i++) Assert.That(pcm[i], Is.EqualTo(samples[i] / 32768f));
    }

    [Test]
    public void Pcm16Be_LongRun_CoversTheVectorizedPath()
    {
        short[] samples = new short[10007 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(i * 7919);
        using AiffDecoder decoder = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(2, 48000, 16, AiffBuilder.Pcm16Be(samples))));
        float[] pcm = ReadAll(decoder);
        Assert.That(pcm, Has.Length.EqualTo(samples.Length));
        for (int i = 0; i < samples.Length; i++) Assert.That(pcm[i], Is.EqualTo(samples[i] / 32768f));
    }

    [Test]
    public void Pcm16Le_Sowt_LongRun_CoversTheVectorizedPath()
    {
        short[] samples = new short[10007 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(i * 6151);
        using AiffDecoder decoder = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(2, 48000, 16, AiffBuilder.Pcm16Le(samples), compression: "sowt")));
        float[] pcm = ReadAll(decoder);
        Assert.That(pcm, Has.Length.EqualTo(samples.Length));
        for (int i = 0; i < samples.Length; i++) Assert.That(pcm[i], Is.EqualTo(samples[i] / 32768f));
    }

    [Test]
    public void Pcm8_IsSignedInAiff_AndUnsignedInRawAifc()
    {
        using AiffDecoder signed = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 8000, 8, [0x80, 0x00, 0x7F])));
        Assert.That(ReadAll(signed), Is.EqualTo(new[] { -1f, 0f, 127f / 128f }));
        using AiffDecoder sowt8 = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 8000, 8, [0x80, 0x00, 0x7F], compression: "sowt")));
        Assert.That(ReadAll(sowt8), Is.EqualTo(new[] { -1f, 0f, 127f / 128f }));
        using AiffDecoder raw = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 8000, 8, [0, 128, 255], compression: "raw ")));
        Assert.That(ReadAll(raw), Is.EqualTo(new[] { -1f, 0f, 127f / 128f }));
    }

    [Test]
    public void Pcm24_AndPcm32_BigAndLittleEndian_DecodeCorrectly()
    {
        byte[] data24Be = [0x80, 0x00, 0x00, 0x7F, 0xFF, 0xFF, 0x40, 0x00, 0x00];
        using AiffDecoder d24Be = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 44100, 24, data24Be)));
        Assert.That(ReadAll(d24Be), Is.EqualTo(new[] { -1f, 8388607f / 8388608f, 0.5f }));
        using AiffDecoder dIn24 = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 44100, 24, data24Be, compression: "in24")));
        Assert.That(ReadAll(dIn24), Is.EqualTo(new[] { -1f, 8388607f / 8388608f, 0.5f }));

        byte[] data24Le = [0x00, 0x00, 0x80, 0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x40];
        using AiffDecoder d24Le = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 44100, 24, data24Le, compression: "sowt")));
        Assert.That(ReadAll(d24Le), Is.EqualTo(new[] { -1f, 8388607f / 8388608f, 0.5f }));

        byte[] data32Be = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(data32Be, int.MinValue);
        BinaryPrimitives.WriteInt32BigEndian(data32Be.AsSpan(4), 1 << 30);
        using AiffDecoder d32Be = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 44100, 32, data32Be)));
        Assert.That(ReadAll(d32Be), Is.EqualTo(new[] { -1f, 0.5f }));
        using AiffDecoder dIn32 = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 44100, 32, data32Be, compression: "in32")));
        Assert.That(ReadAll(dIn32), Is.EqualTo(new[] { -1f, 0.5f }));

        byte[] data32Le = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data32Le, int.MinValue);
        BinaryPrimitives.WriteInt32LittleEndian(data32Le.AsSpan(4), 1 << 30);
        using AiffDecoder d32Le = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 44100, 32, data32Le, compression: "sowt")));
        Assert.That(ReadAll(d32Le), Is.EqualTo(new[] { -1f, 0.5f }));
    }

    [Test]
    public void Float32AndFloat64_ReplaceNonFiniteValuesWithSilence()
    {
        byte[] f32 = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(f32, 0.25f);
        BinaryPrimitives.WriteSingleBigEndian(f32.AsSpan(4), float.NaN);
        BinaryPrimitives.WriteSingleBigEndian(f32.AsSpan(8), float.PositiveInfinity);
        using AiffDecoder a = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 44100, 32, f32, compression: "fl32")));
        Assert.That(ReadAll(a), Is.EqualTo(new[] { 0.25f, 0f, 0f }));
        using AiffDecoder aUpper = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 44100, 32, f32, compression: "FL32")));
        Assert.That(ReadAll(aUpper), Is.EqualTo(new[] { 0.25f, 0f, 0f }));

        byte[] f64 = new byte[16];
        BinaryPrimitives.WriteDoubleBigEndian(f64, -0.75);
        BinaryPrimitives.WriteDoubleBigEndian(f64.AsSpan(8), double.NegativeInfinity);
        using AiffDecoder b = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 44100, 64, f64, compression: "fl64")));
        Assert.That(ReadAll(b), Is.EqualTo(new[] { -0.75f, 0f }));
        using AiffDecoder bUpper = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 44100, 64, f64, compression: "FL64")));
        Assert.That(ReadAll(bUpper), Is.EqualTo(new[] { -0.75f, 0f }));
    }

    [Test]
    public void ALawAndMuLaw_MatchTheCompandingLaws()
    {
        using AiffDecoder mu = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 8000, 16, [0xFF, 0x80, 0x00, 0x7F], compression: "ulaw")));
        Assert.That(ReadAll(mu), Is.EqualTo(new[] { 0f, 32124f / 32768f, -32124f / 32768f, 0f }));
        using AiffDecoder a = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 8000, 8, [0xD5, 0x55, 0xAA, 0x2A], compression: "alaw")));
        Assert.That(ReadAll(a), Is.EqualTo(new[] { 8f / 32768f, -8f / 32768f, 32256f / 32768f, -32256f / 32768f }));
    }

    [Test]
    public void SsndOffset_AndSsndBeforeComm_AreHandled()
    {
        byte[] withOffset = AiffBuilder.Build(1, 44100, 16, AiffBuilder.Pcm16Be(100, -100), ssndOffset: 16);
        using AiffDecoder dOffset = AiffDecoder.Open(new MemoryStream(withOffset));
        Assert.That(ReadAll(dOffset), Is.EqualTo(new[] { 100 / 32768f, -100 / 32768f }));

        byte[] ssndFirst = AiffBuilder.Build(1, 44100, 16, AiffBuilder.Pcm16Be(200, -200), ssndOffset: 4, ssndFirst: true);
        using AiffDecoder dSsndFirst = AiffDecoder.Open(new MemoryStream(ssndFirst));
        Assert.That(ReadAll(dSsndFirst), Is.EqualTo(new[] { 200 / 32768f, -200 / 32768f }));
        Assert.Throws<InvalidDataException>(() => AiffDecoder.Open(new ForwardOnlyStream(ssndFirst)));
    }

    [Test]
    public void OtherChunks_AreSkipped_AndTrailingChunksAreNotAudio()
    {
        byte[] file = AiffBuilder.Build(1, 44100, 16, AiffBuilder.Pcm16Be(5, 6), extraChunk: [1, 2, 3]);
        byte[] withTrailer = [.. file, .. "NAME"u8.ToArray(), 0, 0, 0, 4, 9, 9, 9, 9];
        using AiffDecoder decoder = AiffDecoder.Open(new MemoryStream(withTrailer));
        Assert.That(ReadAll(decoder), Is.EqualTo(new[] { 5 / 32768f, 6 / 32768f }));
    }

    [TestCase(0u)]
    [TestCase(8u)]
    [TestCase(uint.MaxValue)]
    public void UnknownDataSize_RunsToTheEndOfTheStream(uint size)
    {
        byte[] file = AiffBuilder.Build(1, 44100, 16, AiffBuilder.Pcm16Be(1, 2, 3), ssndSize: size, numSampleFrames: 0);
        using AiffDecoder seekable = AiffDecoder.Open(new MemoryStream(file));
        Assert.That(ReadAll(seekable), Has.Length.EqualTo(3));
        Assert.That(seekable.TotalFrames, Is.EqualTo(3));
        using AiffDecoder forward = AiffDecoder.Open(new ForwardOnlyStream(file));
        Assert.That(forward.TotalFrames, Is.EqualTo(-1));
        Assert.That(ReadAll(forward), Has.Length.EqualTo(3));
        Assert.That(forward.Seek(0), Is.False);
    }

    [Test]
    public void TruncatedData_StopsAtTheLastWholeFrame()
    {
        byte[] file = AiffBuilder.Build(2, 44100, 16, AiffBuilder.Pcm16Be(1, 2, 3, 4, 5, 6));
        using AiffDecoder decoder = AiffDecoder.Open(new MemoryStream(file[..^5]));
        Assert.That(ReadAll(decoder), Has.Length.EqualTo(2));
    }

    [Test]
    public void ForwardOnlyStream_DecodesTheSameAsASeekableOne()
    {
        short[] samples = new short[5000];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(i * 31);
        byte[] file = AiffBuilder.Build(2, 44100, 16, AiffBuilder.Pcm16Be(samples), extraChunk: new byte[33]);
        using AiffDecoder a = AiffDecoder.Open(new MemoryStream(file));
        using AiffDecoder b = AiffDecoder.Open(new ForwardOnlyStream(file));
        Assert.That(ReadAll(b), Is.EqualTo(ReadAll(a)));
    }

    [Test]
    public void Seek_IsExact_AndRewindsAfterTheEnd()
    {
        short[] samples = new short[2000];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(i * 3);
        using AiffDecoder decoder = AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(2, 44100, 16, AiffBuilder.Pcm16Be(samples))));
        float[] all = ReadAll(decoder);
        Assert.That(decoder.Read(new float[2], 1), Is.EqualTo(0));
        Assert.That(decoder.Seek(-1), Is.False);
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
        byte[] file = AiffBuilder.Build(1, 44100, 16, AiffBuilder.Pcm16Be(1, 2, 3, 4), extraChunk: new byte[900]);
        using AiffDecoder decoder = AiffDecoder.Open(new NoLengthStream(file));
        Assert.That(decoder.TotalFrames, Is.EqualTo(4));
        Assert.That(ReadAll(decoder), Has.Length.EqualTo(4));
        Assert.That(decoder.Seek(0), Is.False);
    }

    [Test]
    public void Sniff_RecognizesAiffAndAifc()
    {
        Assert.That(AiffDecoder.Sniff(AiffBuilder.Build(1, 8000, 16, AiffBuilder.Pcm16Be(1))), Is.True);
        Assert.That(AiffDecoder.Sniff(AiffBuilder.Build(1, 8000, 16, AiffBuilder.Pcm16Be(1), compression: "twos")), Is.True);
        Assert.That(AiffDecoder.Sniff("FORM00008SVX"u8), Is.False);
        Assert.That(AiffDecoder.Sniff("RIFF0000WAVE"u8), Is.False);
        Assert.That(AiffDecoder.Sniff("FORM"u8), Is.False);
    }

    [Test]
    public void Unsupported_AndInvalidFiles_AreRefused()
    {
        Assert.Throws<NotSupportedException>(() => AiffDecoder.Open(new MemoryStream(AiffBuilder.Build(1, 8000, 16, [0, 0], compression: "ima4"))));
        Assert.Throws<InvalidDataException>(() => AiffDecoder.Open(new MemoryStream("not an aiff file at all"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => AiffDecoder.Open(new MemoryStream("FORM"u8.ToArray())));
        byte[] zeroChannels = AiffBuilder.Build(1, 8000, 16, AiffBuilder.Pcm16Be(1));
        zeroChannels[20] = 0;
        zeroChannels[21] = 0;
        Assert.Throws<InvalidDataException>(() => AiffDecoder.Open(new MemoryStream(zeroChannels)));

        byte[] negativeRate = AiffBuilder.Build(1, 8000, 16, AiffBuilder.Pcm16Be(1));
        negativeRate[28] |= 0x80;
        Assert.Throws<InvalidDataException>(() => AiffDecoder.Open(new MemoryStream(negativeRate)));
    }

    [Test]
    public void DamagedFiles_NeverEscapeWithAnotherException()
    {
        byte[] good = AiffBuilder.Build(2, 44100, 16, AiffBuilder.Pcm16Be(new short[400]), compression: "twos");
        Random random = new(11);
        for (int round = 0; round < 3000; round++)
        {
            byte[] bad = (byte[])good.Clone();
            int flips = 1 + random.Next(4);
            for (int f = 0; f < flips; f++) bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
            if (round % 3 == 0) bad = bad[..random.Next(bad.Length)];
            try
            {
                using AiffDecoder decoder = AiffDecoder.Open(new MemoryStream(bad));
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
        byte[] file = AiffBuilder.Build(2, 44100, 16, AiffBuilder.Pcm16Be(new short[200_000]));
        using AiffDecoder decoder = AiffDecoder.Open(new MemoryStream(file));
        float[] block = new float[2 * 1024];
        decoder.Read(block, 1024);
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (decoder.Read(block, 1024) > 0) { }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void Decoding_IsMuchFasterThanRealTime()
    {
        byte[] file = AiffBuilder.Build(2, 44100, 16, AiffBuilder.Pcm16Be(new short[44100 * 2 * 60]));
        using AiffDecoder decoder = AiffDecoder.Open(new MemoryStream(file));
        float[] block = new float[2 * 4096];
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        while (decoder.Read(block, 4096) > 0) { }
        // A minute of audio: the bound is 1 % of real time, far above what the decoder needs.
        Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(0.6));
    }
}
