using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

/// <summary>
/// Writes valid AAC-LC raw data blocks with the repository's codebooks: long windows, every band in the escape codebook, scalefactors all equal to
/// the global gain. Enough to build streams with coupling channel elements, which FFmpeg's encoder never writes, so that FFmpeg's decoder
/// can serve as the reference for them.
/// </summary>
internal sealed class SyntheticStream
{
    private readonly List<(int Value, int Bits)> bits = [];
    public const int SamplingIndex = 4;

    public void Add(int value, int count) => bits.Add((value, count));

    public void Flag(bool value) => Add(value ? 1 : 0, 1);

    public void Huffman((byte Length, uint Code)[] book, int index) => Add((int)book[index].Code, book[index].Length);

    public byte[] ToBytes() => AacTestData.Pack([.. bits]);

    /// <summary>Writes individual_channel_stream() (long window, shape 0) for the coefficient array (at most 1024 values, |value| at most 15).</summary>
    public void IndividualChannelStream(int globalGain, int maxSfb, int[] coefficients, bool kaiser = false, bool commonWindow = false, bool tns = false)
    {
        int[] offsets = ScaleFactorBandTables.Long[SamplingIndex];
        Add(globalGain, 8);
        if (!commonWindow)
        {
            Add(0, 1); Add(0, 2); Add(kaiser ? 1 : 0, 1); Add(maxSfb, 6); Add(0, 1);
        }
        Add(11, 4); Add(maxSfb, 5);                         // one section, escape codebook
        for (int sfb = 0; sfb < maxSfb; sfb++) Huffman(HuffmanTables.ScaleFactor, 60);
        Add(0, 1);                                          // no pulse
        Flag(tns);
        if (tns)
        {
            Add(1, 2); Add(0, 1);                           // one filter, 3-bit coefficients
            Add(20, 6); Add(4, 5); Add(0, 1); Add(0, 1);    // 20 bands, order 4, upward, not compressed
            foreach (int coefficient in new[] { 2, 5, 1, 6 }) Add(coefficient, 3);
        }
        Add(0, 1);                                          // no gain control
        for (int k = 0; k < offsets[maxSfb]; k += 2)
        {
            int y = coefficients[k], z = coefficients[k + 1];
            Huffman(HuffmanTables.Spectrum11, Math.Abs(y) * 17 + Math.Abs(z));
            if (y != 0) Add(y < 0 ? 1 : 0, 1);
            if (z != 0) Add(z < 0 ? 1 : 0, 1);
        }
    }

    public void SingleChannel(int tag, int globalGain, int maxSfb, int[] coefficients, bool tns = false)
    {
        Add(0, 3); Add(tag, 4);
        IndividualChannelStream(globalGain, maxSfb, coefficients, tns: tns);
    }

    public void ChannelPair(int tag, int gainLeft, int[] left, int gainRight, int[] right, int maxSfb, bool tns = false)
    {
        Add(1, 3); Add(tag, 4); Add(0, 1);                  // CPE without a common window
        IndividualChannelStream(gainLeft, maxSfb, left, tns: tns);
        IndividualChannelStream(gainRight, maxSfb, right, tns: tns);
    }

    public void End() => Add(7, 3);

    /// <summary>A program_config_element() for one SCE and one CPE (tag 0 each) and one coupling channel (tag 0), as FFmpeg requires before it accepts coupling.</summary>
    public void ProgramConfig(bool independentCoupling)
    {
        Add(5, 3);                                          // id_syn_ele: PCE
        Add(0, 4); Add(1, 2); Add(SamplingIndex, 4);        // tag, object type (LC - 1), sampling index
        Add(2, 4); Add(0, 4); Add(0, 4); Add(0, 2); Add(0, 3); Add(1, 4); // front 2, side 0, back 0, lfe 0, assoc 0, cc 1
        Add(0, 1); Add(0, 1); Add(0, 1);                    // no mixdowns
        Add(0, 1); Add(0, 4);                               // front: SCE tag 0
        Add(1, 1); Add(0, 4);                               // front: CPE tag 0
        Flag(independentCoupling); Add(0, 4);               // coupling channel tag 0
        int used = bits.Sum(entry => entry.Bits);
        if (used % 8 != 0) Add(0, 8 - used % 8);            // byte_alignment from the start of the block (the PCE is its first element)
        Add(0, 8);                                          // no comment
    }

    public readonly record struct Target(bool IsPair, int Tag, bool Left = false, bool Right = false);

    /// <summary>
    /// Writes coupling_channel_element(). <paramref name="gains"/> holds one entry per gain list after the first: a single value (a common gain
    /// element) or one value per band, each written as given (the differential element, whose lowest bit is the phase when gains are signed).
    /// </summary>
    public void Coupling(int tag, bool independent, Target[] targets, int domain, bool signedGains, int scale, int globalGain, int maxSfb, int[] coefficients, int[][] gains)
    {
        Add(2, 3); Add(tag, 4);
        Flag(independent);
        Add(targets.Length - 1, 3);
        foreach (Target target in targets)
        {
            Flag(target.IsPair); Add(target.Tag, 4);
            if (target.IsPair) { Flag(target.Left); Flag(target.Right); }
        }
        Add(domain, 1); Flag(signedGains); Add(scale, 2);
        IndividualChannelStream(globalGain, maxSfb, coefficients);
        foreach (int[] list in gains)
        {
            bool common = list.Length == 1;
            if (!independent) Flag(common);
            if (common)
            {
                Huffman(HuffmanTables.ScaleFactor, list[0] + 60);
                continue;
            }
            foreach (int value in list) Huffman(HuffmanTables.ScaleFactor, value + 60);
        }
    }

    /// <summary>Sparse, repeatable test spectrum: a few strong low bands and some higher ones.</summary>
    public static int[] Spectrum(int seed, int bandLimit = 40)
    {
        Random random = new(seed);
        int[] values = new int[1024];
        for (int k = 0; k < ScaleFactorBandTables.Long[SamplingIndex][bandLimit]; k++)
            values[k] = random.Next(0, 4) == 0 ? random.Next(-9, 10) : 0;
        return values;
    }

    /// <summary>Wraps a raw data block (or several, back to back) in an ADTS frame without CRC.</summary>
    public static byte[] AdtsFrame(int channelConfiguration, params byte[][] blocks)
    {
        int payload = blocks.Sum(block => block.Length);
        byte[] frame = AacTestData.Frame(payload, SamplingIndex, channelConfiguration);
        int at = 7;
        foreach (byte[] block in blocks)
        {
            block.CopyTo(frame, at);
            at += block.Length;
        }
        frame[6] = (byte)(0xfc & 0xfc | blocks.Length - 1);
        return frame;
    }
}
