namespace Aibell.Audio.Flac.Tests;

public class FlacRoundTripTests
{
    private static int[][] Signal(int channels, int frames, int bits, int seed, int lowZeroBits = 0)
    {
        Random random = new(seed);
        long limit = 1L << (bits - 1);
        int[][] data = new int[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new int[frames];
            double phase = random.NextDouble();
            for (int i = 0; i < frames; i++)
            {
                double tone = Math.Sin(2 * Math.PI * (0.01 + 0.003 * c) * i + phase) * 0.6;
                double noise = (random.NextDouble() * 2 - 1) * (i % 700 < 300 ? 0.3 : 0.001);
                long v = (long)Math.Round((tone + noise) * (limit - 1));
                v = Math.Clamp(v, -limit, limit - 1);
                data[c][i] = (int)((v >> lowZeroBits) << lowZeroBits);
            }
        }
        return data;
    }

    private static void AssertRoundTrip(int[][] data, FlacEncodeOptions options)
    {
        byte[] file = FlacTestEncoder.Encode(data, options);
        using FlacDecoder decoder = FlacDecoder.Open(new MemoryStream(file));
        Assert.That(decoder.Channels, Is.EqualTo(data.Length));
        Assert.That(decoder.BitsPerSample, Is.EqualTo(options.BitsPerSample));
        Assert.That(decoder.SampleRate, Is.EqualTo(options.SampleRate));
        float[] pcm = FlacFixtureTests.DecodeAll(decoder);
        int frames = data[0].Length;
        Assert.That(pcm, Has.Length.EqualTo(frames * data.Length));
        double scale = 1.0 / (1L << (options.BitsPerSample - 1));
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < data.Length; c++)
            {
                if (pcm[i * data.Length + c] != (float)(data[c][i] * scale)) Assert.Fail($"Frame {i} channel {c}: {pcm[i * data.Length + c]} instead of {data[c][i] * scale}");
            }
        }
    }

    [TestCase(8)]
    [TestCase(12)]
    [TestCase(16)]
    [TestCase(20)]
    [TestCase(24)]
    public void EveryBitDepth_WithEveryStereoMode(int bits)
    {
        foreach (StereoMode mode in Enum.GetValues<StereoMode>())
            AssertRoundTrip(Signal(2, 5000, bits, bits), new FlacEncodeOptions { BitsPerSample = bits, Stereo = mode, BlockSizes = [1152, 4608, 777] });
    }

    [Test]
    public void ThirtyTwoBitSamples_AreSupportedWithoutDecorrelation()
    {
        AssertRoundTrip(Signal(1, 3000, 32, 3), new FlacEncodeOptions { BitsPerSample = 32, SampleRate = 192000 });
        AssertRoundTrip(Signal(2, 3000, 32, 4), new FlacEncodeOptions { BitsPerSample = 32, Kind = SubframeKind.Verbatim });
    }

    [Test]
    public void ThirtyTwoBitStereoDecorrelation_IsRefusedClearly()
    {
        byte[] file = FlacTestEncoder.Encode(Signal(2, 2000, 32, 5), new FlacEncodeOptions { BitsPerSample = 32, Stereo = StereoMode.MidSide, OmitHeaderRateAndDepth = false });
        Assert.Throws<NotSupportedException>(() => FlacDecoder.Open(new MemoryStream(file)));
    }

    [TestCase(SubframeKind.Constant)]
    [TestCase(SubframeKind.Verbatim)]
    [TestCase(SubframeKind.Fixed0)]
    [TestCase(SubframeKind.Fixed1)]
    [TestCase(SubframeKind.Fixed2)]
    [TestCase(SubframeKind.Fixed3)]
    [TestCase(SubframeKind.Fixed4)]
    [TestCase(SubframeKind.Auto)]
    public void EverySubframeKind(SubframeKind kind) =>
        AssertRoundTrip(Signal(2, 6000, 16, 7), new FlacEncodeOptions { Kind = kind, BlockSizes = [4096, 1000] });

    [Test]
    public void ConstantSubframes_FillTheBlock()
    {
        int[][] silence = [new int[3000], Enumerable.Repeat(-1234, 3000).ToArray()];
        AssertRoundTrip(silence, new FlacEncodeOptions { Kind = SubframeKind.Constant });
    }

    [TestCase(1, new[] { 2, -1 }, 0, 4)]
    [TestCase(2, new[] { 3, -3, 1 }, 0, 4)]
    [TestCase(3, new[] { 1 }, 0, 2)]
    [TestCase(4, new[] { 1500, -700, 200, -50, 10 }, 10, 12)]
    [TestCase(5, new[] { 4000, -6000, 4000, -1600, 400, -64, 4, 0 }, 11, 15)]
    [TestCase(6, new[] { 9, -2, 1, 1, -1, 1, 0, 0, 1, 0, -1, 1, 0, 1, 1, -1, 0, 0, 1, 0, 0, 1, -1, 0, 0, 0, 0, 1, 0, 0, 0, 1 }, 5, 6)]
    public void LpcSubframes_IncludingTheMaximumOrder(int seed, int[] coefficients, int shift, int precision) =>
        AssertRoundTrip(Signal(2, 5000, 16, seed), new FlacEncodeOptions { Kind = SubframeKind.Lpc, LpcCoefficients = coefficients, LpcShift = shift, LpcPrecision = precision, BlockSizes = [4096, 904] });

    [Test]
    public void LpcWith24BitAudio_UsesWideArithmetic() =>
        AssertRoundTrip(Signal(2, 5000, 24, 8), new FlacEncodeOptions { BitsPerSample = 24, Kind = SubframeKind.Lpc, LpcCoefficients = [15000, -12000, 4500, -1000], LpcShift = 13, LpcPrecision = 15 });

    [TestCase(0, 0)]
    [TestCase(0, 5)]
    [TestCase(1, 0)]
    [TestCase(1, 8)]
    public void RiceMethodsAndPartitionOrders(int method, int partitionOrder) =>
        AssertRoundTrip(Signal(2, 8192, 16, 9), new FlacEncodeOptions { RiceMethod = method, PartitionOrder = partitionOrder, Kind = SubframeKind.Fixed2, BlockSizes = [4096] });

    [Test]
    public void EscapedPartitions_ReadRawResiduals()
    {
        AssertRoundTrip(Signal(2, 4096, 16, 10), new FlacEncodeOptions { ForceEscape = true, Kind = SubframeKind.Fixed1, PartitionOrder = 3, BlockSizes = [2048] });
        AssertRoundTrip(Signal(2, 4096, 16, 10), new FlacEncodeOptions { ForceEscape = true, Kind = SubframeKind.Fixed3, RiceMethod = 1, PartitionOrder = 2, BlockSizes = [1024] });
        AssertRoundTrip([new int[2048], new int[2048]], new FlacEncodeOptions { ForceEscape = true, Kind = SubframeKind.Fixed0, BlockSizes = [1024] });
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(12)]
    public void WastedBits_AreShiftedBackIn(int wasted)
    {
        AssertRoundTrip(Signal(2, 4000, 16, 11, wasted), new FlacEncodeOptions { WastedBits = wasted, Kind = SubframeKind.Verbatim });
        AssertRoundTrip(Signal(2, 4000, 16, 11, wasted), new FlacEncodeOptions { WastedBits = wasted });
        AssertRoundTrip(Signal(2, 4000, 16, 12, wasted + 1), new FlacEncodeOptions { WastedBits = wasted, Stereo = StereoMode.MidSide, Kind = SubframeKind.Verbatim });
    }

    [Test]
    public void EveryBlockSizeCode()
    {
        int[] sizes = [192, 576, 1152, 2304, 4608, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 100, 1000, 5000, 65535, 17, 1];
        foreach (int size in sizes)
            AssertRoundTrip(Signal(2, Math.Min(size * 2 + 1, 70000), 16, size, 0), new FlacEncodeOptions { BlockSizes = [size] });
    }

    [Test]
    public void VariableBlockSizes_UseSampleNumbersInTheHeaders()
    {
        AssertRoundTrip(Signal(2, 300_000, 16, 13), new FlacEncodeOptions { VariableBlockSize = true, BlockSizes = [4096, 1000, 333, 2048, 16000, 90], Kind = SubframeKind.Fixed1 });
    }

    [TestCase(8000)]
    [TestCase(11025)]
    [TestCase(44100)]
    [TestCase(12000)]
    [TestCase(37800)]
    [TestCase(65530)]
    [TestCase(655350)]
    [TestCase(1000)]
    public void SampleRatesAndBitDepthsCanBeStatedInTheFrameHeaders(int rate) =>
        AssertRoundTrip(Signal(2, 3000, 20, 14), new FlacEncodeOptions { SampleRate = rate, BitsPerSample = 20, OmitHeaderRateAndDepth = false });

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    public void ManyChannels(int channels) =>
        AssertRoundTrip(Signal(channels, 3000, 16, channels), new FlacEncodeOptions { SampleRate = 48000 });

    [Test]
    public void StreamsOfUnknownLength_AreStillDecoded()
    {
        byte[] file = FlacTestEncoder.Encode(Signal(2, 5000, 16, 15), new FlacEncodeOptions { KnownLength = false, Md5 = false });
        using FlacDecoder decoder = FlacDecoder.Open(new MemoryStream(file));
        Assert.That(decoder.TotalFrames, Is.EqualTo(-1));
        Assert.That(FlacFixtureTests.DecodeAll(decoder), Has.Length.EqualTo(10000));
    }

    [Test]
    public void OtherMetadataBlocks_AreSkipped()
    {
        byte[] picture = [6, .. new byte[70_000]];
        byte[] padding = [1, .. new byte[1000]];
        byte[] comment = [4, .. "\x07\0\0\0vendor!\0\0\0\0"u8.ToArray()];
        AssertRoundTrip(Signal(2, 3000, 16, 16), new FlacEncodeOptions { ExtraMetadata = [comment, picture, padding], SeekTable = true });
    }

    [Test]
    public void ForwardOnlyStreams_AreDecoded()
    {
        int[][] data = Signal(2, 20_000, 16, 17);
        byte[] file = FlacTestEncoder.Encode(data, new FlacEncodeOptions { ExtraMetadata = [[6, .. new byte[5000]]] });
        using FlacDecoder decoder = FlacDecoder.Open(new Aibell.Audio.Wav.Tests.ForwardOnlyStream(file));
        float[] pcm = FlacFixtureTests.DecodeAll(decoder);
        Assert.That(pcm, Has.Length.EqualTo(40_000));
        Assert.That(decoder.Seek(5), Is.False);
    }

    [Test]
    public void StreamsWithoutALength_AreTreatedAsForwardOnly()
    {
        int[][] data = Signal(2, 20_000, 16, 18);
        byte[] file = FlacTestEncoder.Encode(data, new FlacEncodeOptions { ExtraMetadata = [[6, .. new byte[5000]]], SeekTable = true });
        using FlacDecoder decoder = FlacDecoder.Open(new Aibell.Audio.Wav.Tests.NoLengthStream(file));
        Assert.That(FlacFixtureTests.DecodeAll(decoder), Has.Length.EqualTo(40_000));
        Assert.That(decoder.Seek(5), Is.False);
    }
}
