namespace Aibell.Audio.Aac;

/// <summary>A parsed ADTS fixed + variable header (ISO/IEC 14496-3 subclause 1.A.2.2).</summary>
public readonly record struct AdtsHeader(
    int ObjectType, int SamplingIndex, int ChannelConfiguration, int FrameLength, int HeaderLength, bool HasCrc, int RawBlocks)
{
    public const int MinimumHeaderLength = 7;
    public const int MaximumFrameLength = 8191;

    public int SampleRate => SamplingRates.FromIndex(SamplingIndex);

    /// <summary>Everything after the header; with several blocks, also the CRCs that follow each block.</summary>
    public int PayloadLength => FrameLength - HeaderLength;

    /// <summary>Parses and plausibility-checks a header at the start of <paramref name="data"/>. False when it is not a believable header.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out AdtsHeader header)
    {
        header = default;
        if (data.Length < MinimumHeaderLength) return false;
        if (data[0] != 0xff || (data[1] & 0xf6) != 0xf0) return false; // sync, layer 00 (ID bit may be MPEG-2 or MPEG-4)
        bool protectionAbsent = (data[1] & 1) != 0;
        int profile = data[2] >> 6;
        int samplingIndex = data[2] >> 2 & 15;
        int channels = (data[2] & 1) << 2 | data[3] >> 6;
        int length = (data[3] & 3) << 11 | data[4] << 3 | data[5] >> 5;
        int blocks = (data[6] & 3) + 1;
        // With protection: the CRC, preceded by the start positions of the 2nd.. blocks when the frame holds several.
        int headerLength = protectionAbsent ? 7 : 9 + 2 * (blocks - 1);
        if (samplingIndex >= SamplingRates.Count || length < headerLength) return false;
        if (data.Length < headerLength) return false;
        header = new AdtsHeader(profile + 1, samplingIndex, channels, length, headerLength, !protectionAbsent, blocks);
        return true;
    }
}
