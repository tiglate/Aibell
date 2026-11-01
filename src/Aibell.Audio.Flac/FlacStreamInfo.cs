namespace Aibell.Audio.Flac;

/// <summary>The STREAMINFO block (RFC 9639 section 8.2).</summary>
internal readonly record struct FlacStreamInfo(
    int MinBlockSize, int MaxBlockSize, int MinFrameSize, int MaxFrameSize, int SampleRate, int Channels, int BitsPerSample, long TotalSamples, byte[] Md5)
{
    public const int Length = 34;

    public static FlacStreamInfo Parse(ReadOnlySpan<byte> block)
    {
        if (block.Length < Length) throw new InvalidDataException("The FLAC STREAMINFO block is too short.");
        int minBlock = block[0] << 8 | block[1];
        int maxBlock = block[2] << 8 | block[3];
        int minFrame = block[4] << 16 | block[5] << 8 | block[6];
        int maxFrame = block[7] << 16 | block[8] << 8 | block[9];
        // 20 bits sample rate, 3 bits channels - 1, 5 bits bits per sample - 1, 36 bits total samples.
        int rate = block[10] << 12 | block[11] << 4 | block[12] >> 4;
        int channels = (block[12] >> 1 & 7) + 1;
        int bits = ((block[12] & 1) << 4 | block[13] >> 4) + 1;
        long total = (long)(block[13] & 15) << 32 | (long)block[14] << 24 | (long)block[15] << 16 | (long)block[16] << 8 | block[17];
        if (rate == 0 || rate > 655350 || bits < 4) throw new InvalidDataException("The FLAC STREAMINFO block is invalid.");
        return new FlacStreamInfo(minBlock, maxBlock, minFrame, maxFrame, rate, channels, bits, total, block.Slice(18, 16).ToArray());
    }
}
