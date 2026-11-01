namespace Aibell.Audio.Aac;

/// <summary>A stretch of a raw data block that the ADTS CRC covers: <see cref="Available"/> bits at <see cref="Start"/>, zero-padded or cut to <see cref="Required"/>.</summary>
internal readonly record struct CrcRange(int Start, int Available, int Required);

/// <summary>
/// The ADTS <c>crc_check</c> (ISO/IEC 13818-7:2004, subclause 8.1.1.1). It is the MPEG audio CRC of ISO/IEC 11172-3: generator
/// x^16 + x^15 + x^2 + 1, register preset to 0xFFFF, bits fed most significant first. It covers all bits of the fixed and variable header,
/// then the first 192 bits of each SCE, CPE, CCE and LFE (after the id_syn_ele bits) and the first 128 bits of the second channel stream of
/// a CPE, each zero-padded when shorter, and the whole of every PCE and data stream element. Only frames with one raw data block
/// (<c>number_of_raw_data_blocks_in_frame</c> = 0) are supported, matching the decoder.
/// </summary>
public static class AdtsCrc
{
    /// <summary>Advances the CRC register by <paramref name="count"/> bits of <paramref name="data"/> starting at bit <paramref name="start"/>; bits past the end read as zero.</summary>
    internal static ushort Update(ushort register, ReadOnlySpan<byte> data, int start, int count)
    {
        int crc = register;
        for (int i = 0; i < count; i++)
        {
            int at = start + i;
            int bit = at >> 3 < data.Length ? data[at >> 3] >> (7 - (at & 7)) & 1 : 0;
            int feedback = (crc >> 15 & 1) ^ bit;
            crc = crc << 1 & 0xffff;
            if (feedback != 0) crc ^= 0x8005;
        }
        return (ushort)crc;
    }

    internal static ushort Compute(ReadOnlySpan<byte> frame, int headerLength, ReadOnlySpan<CrcRange> ranges) =>
        ComputeBlock(frame[headerLength..], ranges, Update(0xffff, frame, 0, 56));

    /// <summary>The CRC of one block's ranges (bit offsets within <paramref name="payload"/>), continuing from <paramref name="preset"/>.</summary>
    internal static ushort ComputeBlock(ReadOnlySpan<byte> payload, ReadOnlySpan<CrcRange> ranges, ushort preset = 0xffff)
    {
        ushort crc = preset;
        foreach (CrcRange range in ranges)
        {
            int real = Math.Min(range.Available, range.Required);
            crc = Update(crc, payload, range.Start, real);
            for (int padding = real; padding < range.Required; padding++) crc = Update(crc, [], 0, 1);
        }
        return crc;
    }

    /// <summary>
    /// Checks the CRC of a decoded frame. <paramref name="frame"/> is the whole ADTS frame (header, the two CRC bytes, raw data block) and
    /// <paramref name="decoder"/> the decoder that has just decoded its payload. Null when there is nothing to check (no CRC in the frame,
    /// several raw data blocks, or a block whose extent was not fully parsed); otherwise whether the stored CRC equals the computed one.
    /// </summary>
    public static bool? Verify(ReadOnlySpan<byte> frame, in AdtsHeader header, AacDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (!header.HasCrc || header.RawBlocks != 1 || frame.Length < header.FrameLength || header.HeaderLength != 9 || !decoder.CrcRangesKnown) return null;
        ushort stored = (ushort)(frame[7] << 8 | frame[8]);
        return Compute(frame[..header.FrameLength], header.HeaderLength, decoder.ProtectedRanges) == stored;
    }

    /// <summary>
    /// Checks the CRC over the headers and block positions of a frame with several raw data blocks. Null for frames without CRC or with a single block
    /// (whose one CRC <see cref="Verify"/> checks together with the block).
    /// </summary>
    public static bool? VerifyHeader(ReadOnlySpan<byte> frame, in AdtsHeader header)
    {
        if (!header.HasCrc || header.RawBlocks < 2 || frame.Length < header.HeaderLength) return null;
        int crcAt = header.HeaderLength - 2;
        ushort stored = (ushort)(frame[crcAt] << 8 | frame[crcAt + 1]);
        return Update(0xffff, frame, 0, crcAt * 8) == stored;
    }
}
