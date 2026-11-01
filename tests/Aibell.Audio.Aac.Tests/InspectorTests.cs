using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

[TestFixture]
public sealed class InspectorTests
{
    [Test]
    public void Sniff_RecognizesTheContainersAndRejectsMp3()
    {
        Assert.That(AacStreamInspector.Sniff(File.ReadAllBytes(ConformanceTests.FixturePath("ms_22k_stereo.aac"))), Is.EqualTo(AacContainer.Adts));
        Assert.That(AacStreamInspector.Sniff(File.ReadAllBytes(ConformanceTests.FixturePath("ms_22k_stereo.ts"))), Is.EqualTo(AacContainer.MpegTs));
        Assert.That(AacStreamInspector.Sniff("\0\0\0 ftypisom\0\0\0\0isomiso2"u8), Is.EqualTo(AacContainer.Mp4));
        byte[] frames = [.. Enumerable.Range(0, 20).SelectMany(_ => new byte[] { 0xff, 0xfb, 0x90, 0x64 }.Concat(new byte[413]))];
        Assert.That(AacStreamInspector.Sniff(frames), Is.EqualTo(AacContainer.Unknown));
        Assert.That(AacStreamInspector.Sniff([]), Is.EqualTo(AacContainer.Unknown));
    }

    [Test]
    public void Sniff_AcceptsAStreamThatStartsMidFrameAndAnId3Prefix()
    {
        byte[] data = File.ReadAllBytes(ConformanceTests.FixturePath("long_sweep_44k_stereo.aac"));
        Assert.That(AacStreamInspector.Sniff(data.AsSpan(77)), Is.EqualTo(AacContainer.Adts));
        byte[] tagged = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 10, .. new byte[10], .. data];
        Assert.That(AacStreamInspector.Sniff(tagged), Is.EqualTo(AacContainer.Adts));
    }

    [Test]
    public void Inspect_ReportsAProfileAndNoSbrForLcStreams()
    {
        AacStreamInfo adts = AacStreamInspector.Inspect(File.ReadAllBytes(ConformanceTests.FixturePath("ms_22k_stereo.aac")));
        Assert.That((adts.Container, adts.ObjectType, adts.SampleRate, adts.Channels, adts.Sbr, adts.IsDecodable), Is.EqualTo((AacContainer.Adts, 2, 22050, 2, (bool?)false, true)));
        Assert.That(adts.ToString(), Does.Contain("AAC-LC"));
        AacStreamInfo ts = AacStreamInspector.Inspect(File.ReadAllBytes(ConformanceTests.FixturePath("ms_22k_stereo.ts")));
        Assert.That((ts.Container, ts.SampleRate, ts.Sbr, ts.IsDecodable), Is.EqualTo((AacContainer.MpegTs, 22050, (bool?)false, true)));
    }

    [Test]
    public void Inspect_FlagsSbrFromTheFillElementEvenWhenTheHeaderSaysLc()
    {
        byte[] stream = [.. Enumerable.Range(0, 6).SelectMany(_ => AacTestData.FillFrame(13, samplingIndex: 6))];
        AacStreamInfo info = AacStreamInspector.Inspect(stream);
        Assert.That(info.Sbr, Is.True);
        Assert.That(info.IsDecodable, Is.False);
        Assert.That(info.ToString(), Does.Contain("SBR"));
    }

    [Test]
    public void Inspect_ReportsOtherTransportCodecs()
    {
        List<byte[]> frames = [AacTestData.Frame(30), AacTestData.Frame(30), AacTestData.Frame(30)];
        Assert.That(AacStreamInspector.Inspect(TestMpegTs.Mux(frames, stream: 0x03)).Codec, Is.EqualTo("MP3"));
        Assert.That(AacStreamInspector.Inspect(TestMpegTs.Mux(frames, stream: 0x81)).Codec, Is.EqualTo("AC-3"));
    }

    [Test]
    public void Decoder_RejectsSbrByDefaultAndDecodesTheCoreWhenAsked()
    {
        byte[] frame = AacTestData.FillFrame(13);
        Assert.That(AdtsHeader.TryParse(frame, out AdtsHeader header), Is.True);
        AacDecoder strict = new();
        Assert.Throws<AacUnsupportedException>(() => strict.Decode(header, frame.AsSpan(7)));
        Assert.That(strict.SbrSignaled, Is.True);
        AacDecoder lenient = new() { RejectSbr = false };
        Assert.That(lenient.Decode(header, frame.AsSpan(7)), Is.EqualTo(1024));
        // A plain fill element is fine.
        byte[] plain = AacTestData.FillFrame(0);
        Assert.That(AdtsHeader.TryParse(plain, out AdtsHeader plainHeader), Is.True);
        Assert.That(new AacDecoder().Decode(plainHeader, plain.AsSpan(7)), Is.EqualTo(1024));
    }
}
