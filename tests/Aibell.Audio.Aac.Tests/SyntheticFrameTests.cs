using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

/// <summary>
/// Frames written by hand with the codebooks of this repository. They check syntax paths FFmpeg's encoder never produces (pulse data,
/// escape sequences) against the standard's pseudo-code; they cannot catch a wrong table, which the FFmpeg fixtures do.
/// </summary>
[TestFixture]
public sealed class SyntheticFrameTests
{
    private static (int Value, int Bits) Code((byte Length, uint Code)[] book, int index) => ((int)book[index].Code, book[index].Length);

    [Test]
    public void Decode_PulseDataAndEscapeSequences_ReconstructTheQuantizedSpectrum()
    {
        List<(int Value, int Bits)> bits =
        [
            (0, 3), (0, 4),                 // SCE, tag
            (100, 8),                       // global_gain
            (0, 1), (0, 2), (0, 1), (4, 6), (0, 1), // ics_info: long window, sine, max_sfb 4, no prediction
            (11, 4), (4, 5),                // one section of 4 bands in the escape codebook
        ];
        for (int sfb = 0; sfb < 4; sfb++) bits.Add(Code(HuffmanTables.ScaleFactor, 60)); // scalefactor differences of zero
        bits.AddRange([(1, 1), (1, 2), (1, 6), (2, 5), (5, 4), (3, 5), (1, 4)]); // pulse: 2 pulses from band 1; k = 4 + 2, amp 5; k + 3, amp 1
        bits.AddRange([(0, 1), (0, 1)]);    // no TNS, no gain control
        // Spectrum: pair (20, -3) escapes on the first value; the 7 other pairs are zero.
        bits.Add(Code(HuffmanTables.Spectrum11, 16 * 17 + 3));
        bits.AddRange([(0, 1), (1, 1)]);    // signs: +20, -3
        bits.AddRange([(0, 1), (4, 4)]);    // escape sequence for 20: no prefix ones, separator, 4-bit word -> 16 + 4
        for (int pair = 1; pair < 8; pair++) bits.Add(Code(HuffmanTables.Spectrum11, 0));
        bits.Add((7, 3));                   // END

        byte[] payload = AacTestData.Pack([.. bits]);
        AacDecoder decoder = new();
        Assert.That(decoder.Decode(2, 4, 1, payload), Is.EqualTo(1024));

        ReadOnlySpan<int> quantized = decoder.FirstQuantized;
        Assert.That((quantized[0], quantized[1]), Is.EqualTo((20, -3)));
        Assert.That(quantized[6], Is.EqualTo(-5), "a pulse on a zero coefficient subtracts its amplitude");
        Assert.That(quantized[9], Is.EqualTo(-1));
        Assert.That(quantized[2..6].ToArray().Concat(quantized[7..9].ToArray()).Concat(quantized[10..16].ToArray()), Is.All.Zero);
        Assert.That(quantized[16..].ToArray(), Is.All.Zero);
        Assert.That(decoder.Statistics.PulseChannels, Is.EqualTo(1));
    }

    [Test]
    public void Decode_OutOfRangeCodebookOrScalefactor_IsRejected()
    {
        // Same frame prefix, but codebook 12 is reserved.
        byte[] reserved = AacTestData.Pack([(0, 3), (0, 4), (100, 8), (0, 1), (0, 2), (0, 1), (4, 6), (0, 1), (12, 4), (4, 5)]);
        Assert.Throws<AacException>(() => new AacDecoder().Decode(2, 4, 1, reserved));
        // A section longer than max_sfb.
        byte[] tooLong = AacTestData.Pack([(0, 3), (0, 4), (100, 8), (0, 1), (0, 2), (0, 1), (4, 6), (0, 1), (1, 4), (5, 5)]);
        Assert.Throws<AacException>(() => new AacDecoder().Decode(2, 4, 1, tooLong));
    }

    [Test]
    public void Decode_FrameWithTooManyChannelsForItsConfiguration_IsRejected()
    {
        // Two empty-channel elements cannot fit a mono configuration; build two SCEs by repeating a minimal one.
        List<(int Value, int Bits)> one = [(0, 3), (0, 4), (100, 8), (0, 1), (0, 2), (0, 1), (0, 6), (0, 1), (0, 1), (0, 1), (0, 1)];
        List<(int Value, int Bits)> bits = [.. one, .. one, (7, 3)];
        byte[] payload = AacTestData.Pack([.. bits]);
        Assert.Throws<AacException>(() => new AacDecoder().Decode(2, 4, 1, payload));
    }
}
