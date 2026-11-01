using System.Buffers.Binary;

namespace Aibell.Audio.Mp3.Tests;

public class Mp3UnitTests
{
    private static uint Header(int version, int bitrateIndex, int rateIndex, bool padding = false, int mode = 0, bool crc = false, int modeExtension = 0)
    {
        // version: 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5
        return 0xFFE00000u | (uint)version << 19 | 1u << 17 | (crc ? 0u : 1u << 16) | (uint)bitrateIndex << 12 | (uint)rateIndex << 10 | (padding ? 1u << 9 : 0u) | (uint)mode << 6 | (uint)modeExtension << 4;
    }

    private static Mp3Header Parse(uint value)
    {
        Assert.That(Mp3Header.TryParse(value, out Mp3Header header), Is.True);
        return header;
    }

    [TestCase(3, 9, 0, 417)]
    [TestCase(3, 9, 1, 384)]
    [TestCase(3, 14, 2, 1440)]
    [TestCase(2, 8, 0, 208)]
    [TestCase(0, 8, 2, 576)]
    public void FrameLength_FollowsTheBitRateAndSampleRate(int version, int bitrateIndex, int rateIndex, int expected)
    {
        Assert.That(Mp3Header.TryParse(Header(version, bitrateIndex, rateIndex), out Mp3Header header), Is.True);
        Assert.That(header.FrameLength, Is.EqualTo(expected));
        Mp3Header padded = Parse(Header(version, bitrateIndex, rateIndex, padding: true));
        Assert.That(padded.FrameLength, Is.EqualTo(expected + 1));
    }

    [Test]
    public void Header_DescribesTheFrame()
    {
        Mp3Header joint = Parse(Header(3, 9, 0, mode: 1, crc: true, modeExtension: 2));
        Assert.Multiple(() =>
        {
            Assert.That(joint.IsMpeg1, Is.True);
            Assert.That(joint.Version, Is.EqualTo(1));
            Assert.That(joint.SampleRate, Is.EqualTo(44100));
            Assert.That(joint.BitRate, Is.EqualTo(128000));
            Assert.That(joint.Channels, Is.EqualTo(2));
            Assert.That(joint.SamplesPerFrame, Is.EqualTo(1152));
            Assert.That(joint.Granules, Is.EqualTo(2));
            Assert.That(joint.SideInfoLength, Is.EqualTo(32));
            Assert.That(joint.HasCrc, Is.True);
            Assert.That(joint.HeaderAndSideLength, Is.EqualTo(38));
            Assert.That(joint.ModeExtension, Is.EqualTo(2));
        });
        Mp3Header mono = Parse(Header(0, 5, 2, mode: 3));
        Assert.Multiple(() =>
        {
            Assert.That(mono.Version, Is.EqualTo(25));
            Assert.That(mono.SampleRate, Is.EqualTo(8000));
            Assert.That(mono.Channels, Is.EqualTo(1));
            Assert.That(mono.SamplesPerFrame, Is.EqualTo(576));
            Assert.That(mono.SideInfoLength, Is.EqualTo(9));
            Assert.That(mono.IsCompatibleWith(joint), Is.False);
        });
    }

    [Test]
    public void Header_RefusesWhatIsNotAnMpegAudioHeader()
    {
        Assert.That(Mp3Header.TryParse(0x00000000, out _), Is.False, "no sync");
        Assert.That(Mp3Header.TryParse(Header(1, 9, 0), out _), Is.False, "reserved version");
        Assert.That(Mp3Header.TryParse(Header(3, 9, 0) & ~(3u << 17), out _), Is.False, "reserved layer");
        Assert.That(Mp3Header.TryParse(Header(3, 9, 0) & ~(3u << 17) | 2u << 17, out Mp3Header layer2), Is.True, "layer II");
        Assert.That(layer2.Layer, Is.EqualTo(2));
        Assert.That(layer2.SamplesPerFrame, Is.EqualTo(1152));
        Assert.That(Mp3Header.TryParse(Header(3, 0, 0), out _), Is.False, "free format");
        Assert.That(Mp3Header.TryParse(Header(3, 15, 0), out _), Is.False, "bad bit rate");
        Assert.That(Mp3Header.TryParse(Header(3, 9, 3), out _), Is.False, "reserved sample rate");
    }

    private static byte[] Frame(int version, int mode, Action<byte[], int> fill)
    {
        Mp3Header header = Parse(Header(version, 9, 0, mode: mode));
        byte[] frame = new byte[header.FrameLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame, Header(version, 9, 0, mode: mode));
        fill(frame, header.HeaderAndSideLength);
        return frame;
    }

    [Test]
    public void InfoTag_ReadsTheFrameCountAndTheGaplessValues()
    {
        byte[] frame = Frame(3, 0, (data, offset) =>
        {
            "Info"u8.CopyTo(data.AsSpan(offset));
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 4), 0xF);
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset + 8), 1234);
            "LAME3.100"u8.CopyTo(data.AsSpan(offset + 8 + 4 + 4 + 100 + 4));
            int lame = offset + 8 + 4 + 4 + 100 + 4;
            data[lame + 21] = 0x12;
            data[lame + 22] = 0x34;
            data[lame + 23] = 0x56;
        });
        Mp3Header header = Parse(BinaryPrimitives.ReadUInt32BigEndian(frame));
        Assert.That(Mp3InfoTag.TryParse(frame, header, out Mp3InfoTag tag), Is.True);
        Assert.That(tag, Is.EqualTo(new Mp3InfoTag(1234, 0x123, 0x456, true)));
    }

    [Test]
    public void InfoTag_WithoutTheLameExtension_HasNoGaplessInformation()
    {
        byte[] frame = Frame(2, 3, (data, offset) =>
        {
            "Xing"u8.CopyTo(data.AsSpan(offset));
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 4), 1);
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset + 8), 99);
        });
        Mp3Header header = Parse(BinaryPrimitives.ReadUInt32BigEndian(frame));
        Assert.That(Mp3InfoTag.TryParse(frame, header, out Mp3InfoTag tag), Is.True);
        Assert.That(tag, Is.EqualTo(new Mp3InfoTag(99, 0, 0, false)));
    }

    [Test]
    public void InfoTag_IsNotFoundInAnAudioFrame_AndTheVbriVariantIsRecognized()
    {
        byte[] audio = Frame(3, 0, (data, offset) => data.AsSpan(offset, 100).Fill(0x55));
        Mp3Header header = Parse(BinaryPrimitives.ReadUInt32BigEndian(audio));
        Assert.That(Mp3InfoTag.TryParse(audio, header, out _), Is.False);
        byte[] vbri = Frame(3, 0, (data, offset) =>
        {
            "VBRI"u8.CopyTo(data.AsSpan(36));
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(36 + 14), 777);
        });
        header = Parse(BinaryPrimitives.ReadUInt32BigEndian(vbri));
        Assert.That(Mp3InfoTag.TryParse(vbri, header, out Mp3InfoTag tag), Is.True);
        Assert.That(tag.Frames, Is.EqualTo(777));
    }

    [Test]
    public void GaplessTags_DecideTheLength()
    {
        byte[] file = File.ReadAllBytes(Mp3FixtureTests.Path("mpeg1_44k_stereo_128k.mp3"));
        int info = file.AsSpan().IndexOf("Info"u8);
        Assert.That(info, Is.GreaterThan(0));
        int lame = info + 8 + 4 + 4 + 100 + 4;
        // Encoder delay 576 and padding 1000 as the fixture says: change them and the length follows.
        int frames = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(info + 8));
        foreach ((int delay, int padding) in new[] { (576, 1000), (1000, 2000), (0, 0), (100, 300) })
        {
            byte[] changed = (byte[])file.Clone();
            changed[lame + 21] = (byte)(delay >> 4);
            changed[lame + 22] = (byte)((delay & 15) << 4 | padding >> 8);
            changed[lame + 23] = (byte)(padding & 255);
            using Mp3Decoder decoder = Mp3Decoder.Open(new MemoryStream(changed));
            long samples = (long)frames * 1152;
            long expected = padding >= 529 ? samples - padding - delay : samples - delay - 529;
            Assert.That(decoder.TotalFrames, Is.EqualTo(expected), $"delay {delay} padding {padding}");
            Assert.That(Mp3FixtureTests.DecodeAll(decoder), Has.Length.EqualTo(expected * 2));
        }
    }
}
