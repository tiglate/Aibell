using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

[TestFixture]
public sealed class CrcTests
{
    [Test]
    public void Update_MatchesTheCrcOfAnIndependentlyEncodedMpegAudioStream()
    {
        // ISO/IEC 13818-7 refers to the MPEG audio CRC of ISO/IEC 11172-3 without restating it. FFmpeg's layer II encoder (twolame) writes that CRC;
        // it covers the last 16 header bits and the bit allocation data, whose length depends on the frame, so each frame is
        // matched against every plausible length: a wrong polynomial or preset would match none (a chance hit is about 1 in 200 per frame).
        byte[] data = File.ReadAllBytes(ConformanceTests.FixturePath("crc_reference.mp2"));
        int frames = 0;
        for (int at = 0; at + 6 < data.Length && data[at] == 0xff && (data[at + 1] & 0xf1) == 0xf0;)
        {
            Assert.That(data[at + 1] & 1, Is.Zero, "the encoder was asked for CRC protection");
            int stored = data[at + 4] << 8 | data[at + 5];
            bool matched = false;
            for (int bits = 0; bits <= 320 && !matched; bits++)
            {
                ushort crc = AdtsCrc.Update(0xffff, data.AsSpan(at + 2, 2), 0, 16);
                crc = AdtsCrc.Update(crc, data.AsSpan(at + 6), 0, bits);
                matched = crc == stored;
            }
            Assert.That(matched, Is.True, $"frame {frames}");
            frames++;
            at += 417;
        }
        Assert.That(frames, Is.GreaterThanOrEqualTo(3));
    }

    /// <summary>A mono AAC frame with CRC protection built by hand: global_gain only, no bands, so the element is far shorter than 192 bits.</summary>
    private static byte[] ProtectedMonoFrame(bool corrupt = false)
    {
        (int Value, int Bits)[] element =
        [
            (0, 3), (5, 4),                  // SCE, tag
            (100, 8), (0, 1), (0, 2), (0, 1), (0, 6), (0, 1), // global_gain, ics_info with max_sfb 0
            (0, 1), (0, 1), (0, 1),          // no pulse, TNS, gain control
            (7, 3)                           // END
        ];
        byte[] payload = AacTestData.Pack(element);
        byte[] frame = AacTestData.Frame(payload.Length, samplingIndex: 4, channels: 1, crc: true);
        payload.CopyTo(frame, 9);
        // The independent computation: header bits, then the element after its id (tag .. last flag = 4 + 8 + 1 + 2 + 1 + 6 + 1 + 3 = 26 bits),
        // zero padded to 192 bits, with the END id not protected.
        List<int> bits = [];
        for (int i = 0; i < 56; i++) bits.Add(frame[i >> 3] >> (7 - (i & 7)) & 1);
        for (int i = 3; i < 3 + 26; i++) bits.Add(payload[i >> 3] >> (7 - (i & 7)) & 1);
        while (bits.Count < 56 + 192) bits.Add(0);
        int register = 0xffff;
        foreach (int bit in bits)
        {
            int feedback = (register >> 15 & 1) ^ bit;
            register = register << 1 & 0xffff;
            if (feedback != 0) register ^= 0x8005;
        }
        if (corrupt) register ^= 1;
        frame[7] = (byte)(register >> 8);
        frame[8] = (byte)register;
        return frame;
    }

    private static bool? Check(byte[] frame)
    {
        Assert.That(AdtsHeader.TryParse(frame, out AdtsHeader header), Is.True);
        AacDecoder decoder = new();
        decoder.Decode(header, frame.AsSpan(header.HeaderLength));
        return AdtsCrc.Verify(frame, header, decoder);
    }

    [Test]
    public void Verify_AcceptsAFrameWhoseCrcFollowsTheStandard() => Assert.That(Check(ProtectedMonoFrame()), Is.True);

    [Test]
    public void Verify_RejectsAWrongCrcAndAFlippedPayloadBit()
    {
        Assert.That(Check(ProtectedMonoFrame(corrupt: true)), Is.False);
        byte[] flipped = ProtectedMonoFrame();
        flipped[9] ^= 0x08; // the element tag is protected
        Assert.That(Check(flipped), Is.False);
    }

    [Test]
    public void Verify_HasNothingToCheckWithoutACrc()
    {
        byte[] frame = AacTestData.Frame(10);
        Assert.That(AdtsHeader.TryParse(frame, out AdtsHeader header), Is.True);
        Assert.That(AdtsCrc.Verify(frame, header, new AacDecoder()), Is.Null);
    }

    [Test]
    public void Verify_CoversTheFirst192BitsOfALongElement()
    {
        // A real long element from a fixture, given a CRC computed by the library, then damaged inside and beyond the protected 192 bits.
        byte[] data = File.ReadAllBytes(ConformanceTests.FixturePath("long_sweep_44k_stereo.aac"));
        AdtsFrameReader reader = new();
        data.CopyTo(reader.GetWriteBuffer());
        reader.Advance(data.Length);
        reader.Complete();
        Assert.That(reader.TryRead(out AdtsHeader original, out ReadOnlySpan<byte> payload), Is.True);
        byte[] body = payload.ToArray();

        byte[] frame = new byte[9 + body.Length];
        AacTestData.Frame(body.Length, samplingIndex: original.SamplingIndex, channels: original.ChannelConfiguration, crc: true).AsSpan(0, 9).CopyTo(frame);
        body.CopyTo(frame, 9);
        Assert.That(AdtsHeader.TryParse(frame, out AdtsHeader header), Is.True);
        AacDecoder decoder = new();
        decoder.Decode(header, body);
        ushort crc = AdtsCrc.Compute(frame, 9, decoder.ProtectedRanges);
        frame[7] = (byte)(crc >> 8);
        frame[8] = (byte)crc;
        Assert.That(AdtsCrc.Verify(frame, header, decoder), Is.True);

        byte[] inside = (byte[])frame.Clone();
        // The fixture's first frame starts with an unprotected fill element; damage a byte well inside the first protected stretch.
        int target = 9 + (decoder.ProtectedRanges[0].Start + 40) / 8;
        inside[target] ^= 0x10;
        byte[] beyond = (byte[])frame.Clone();
        beyond[^1] ^= 0x01; // alignment bits after END, far beyond the protected stretch
        AacDecoder beyondDecoder = new();
        Assert.That(AdtsHeader.TryParse(beyond, out header), Is.True);
        beyondDecoder.Decode(header, beyond.AsSpan(9));
        Assert.That(AdtsCrc.Verify(beyond, header, beyondDecoder), Is.True, "bits after the first 192 of the element are not protected");
        AacDecoder insideDecoder = new() { };
        Assert.That(AdtsHeader.TryParse(inside, out header), Is.True);
        try { insideDecoder.Decode(header, inside.AsSpan(9)); } catch (AacException) { return; } // damage may make the element unparseable, which is also a rejection
        Assert.That(AdtsCrc.Verify(inside, header, insideDecoder), Is.False);
    }
}
