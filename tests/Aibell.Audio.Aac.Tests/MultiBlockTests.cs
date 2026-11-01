using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

[TestFixture]
public sealed class MultiBlockTests
{
    private static List<byte[]> SingleFrames(string name)
    {
        byte[] data = File.ReadAllBytes(ConformanceTests.FixturePath(name + ".aac"));
        List<byte[]> frames = [];
        for (int offset = 0; offset < data.Length && AdtsHeader.TryParse(data.AsSpan(offset), out AdtsHeader header); offset += header.FrameLength)
            frames.Add(data.AsSpan(offset, header.FrameLength).ToArray());
        return frames;
    }

    private static List<float[]> DecodeBlocks(List<byte[]> frames, List<bool?>? verdicts = null)
    {
        AacDecoder decoder = new();
        List<float[]> blocks = [];
        foreach (byte[] frame in frames)
        {
            Assert.That(AdtsHeader.TryParse(frame, out AdtsHeader header), Is.True);
            Assert.That(header.FrameLength, Is.EqualTo(frame.Length));
            int offset = header.HeaderLength;
            for (int block = 0; block < header.RawBlocks; block++)
            {
                decoder.DecodeBlock(frame, header, block, ref offset, out bool? crc);
                verdicts?.Add(crc);
                float[] samples = new float[AacDecoder.FrameLength * decoder.Channels];
                for (int n = 0; n < AacDecoder.FrameLength; n++)
                {
                    for (int c = 0; c < decoder.Channels; c++) samples[n * decoder.Channels + c] = decoder.GetChannel(c)[n];
                }
                blocks.Add(samples);
            }
            Assert.That(offset, Is.EqualTo(frame.Length), "every byte of the frame belongs to a block or a CRC");
        }
        return blocks;
    }

    [TestCase(2, false)]
    [TestCase(3, false)]
    [TestCase(4, false)]
    [TestCase(2, true)]
    [TestCase(4, true)]
    public void MultiBlockFrames_DecodeLikeTheSameBlocksInSeparateFrames(int perFrame, bool crc)
    {
        List<byte[]> singles = SingleFrames("long_noise_48k_stereo");
        List<float[]> expected = DecodeBlocks(singles);
        List<bool?> verdicts = [];
        List<float[]> actual = DecodeBlocks(AacTestData.Regroup(singles, perFrame, crc), verdicts);

        Assert.That(actual, Has.Count.EqualTo(expected.Count));
        for (int i = 0; i < expected.Count; i++) Assert.That(actual[i], Is.EqualTo(expected[i]), $"block {i}");
        Assert.That(verdicts.All(verdict => verdict == (crc ? true : null)), Is.True);
    }

    [Test]
    public void HeaderParsing_CountsTheBlockPositionsInTheHeaderLength()
    {
        List<byte[]> multi = AacTestData.Regroup(SingleFrames("long_noise_48k_stereo"), 4, crc: true);
        Assert.That(AdtsHeader.TryParse(multi[0], out AdtsHeader header), Is.True);
        Assert.That((header.RawBlocks, header.HasCrc, header.HeaderLength), Is.EqualTo((4, true, 7 + 3 * 2 + 2)));
    }

    [Test]
    public void Crc_DamageInOneBlockOrTheHeaderIsReportedWhereItHappened()
    {
        List<byte[]> multi = AacTestData.Regroup(SingleFrames("long_noise_48k_stereo"), 3, crc: true);
        byte[] frame = (byte[])multi[0].Clone();
        Assert.That(AdtsHeader.TryParse(frame, out AdtsHeader header), Is.True);
        Assert.That(AdtsCrc.VerifyHeader(frame, header), Is.True);

        // Damage a protected bit of the second block (its first byte holds the element id and tag).
        int secondBlock = header.HeaderLength + (frame[7] << 8 | frame[8]);
        frame[secondBlock] ^= 0x08;
        AacDecoder decoder = new();
        int offset = header.HeaderLength;
        List<bool?> verdicts = [];
        for (int block = 0; block < 3; block++)
        {
            try
            {
                decoder.DecodeBlock(frame, header, block, ref offset, out bool? crc);
                verdicts.Add(crc);
            }
            catch (AacException)
            {
                verdicts.Add(false); // the damage may also make the block undecodable
                break;
            }
        }
        Assert.That(verdicts[0], Is.True);
        Assert.That(verdicts[1], Is.Not.EqualTo(true));

        byte[] damagedHeader = (byte[])multi[0].Clone();
        damagedHeader[8] ^= 0x01; // a block position
        Assert.That(AdtsCrc.VerifyHeader(damagedHeader, header), Is.False);
    }

    [Test]
    public void FramesAnnouncingMoreBlocksThanTheyHold_AreRejected()
    {
        byte[] frame = AacTestData.Regroup(SingleFrames("long_noise_48k_stereo"), 2, crc: false)[0];
        frame[6] |= 0x03; // claims four blocks
        Assert.That(AdtsHeader.TryParse(frame, out AdtsHeader header), Is.True);
        AacDecoder decoder = new();
        int offset = header.HeaderLength;
        Assert.Throws<AacException>(() =>
        {
            for (int block = 0; block < header.RawBlocks; block++) decoder.DecodeBlock(frame, header, block, ref offset, out _);
        });
    }
}
