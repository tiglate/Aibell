using System.Buffers.Binary;

namespace Aibell.Audio.Mp3;

/// <summary>The Xing/Info header (and the LAME extension) found in the first frame of many files: frame count and the encoder delay and padding for gapless playback.</summary>
internal readonly record struct Mp3InfoTag(int Frames, int EncoderDelay, int EncoderPadding, bool HasGaplessInfo)
{
    /// <summary>The samples every decoder adds before the first sample of the signal.</summary>
    public const int DecoderDelay = 529;

    /// <summary>Looks for the tag in a frame. Returns false when the frame is an ordinary audio frame.</summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, in Mp3Header header, out Mp3InfoTag tag)
    {
        tag = default;
        int offset = header.HeaderAndSideLength;
        if (frame.Length >= 36 + 4 && frame.Slice(36, 4).SequenceEqual("VBRI"u8))
        {
            // The Fraunhofer variant: a frame without audio; only its frame count is used.
            tag = new Mp3InfoTag(frame.Length >= 36 + 18 ? BinaryPrimitives.ReadInt32BigEndian(frame[(36 + 14)..]) : 0, 0, 0, false);
            return true;
        }
        if (frame.Length < offset + 8) return false;
        ReadOnlySpan<byte> id = frame.Slice(offset, 4);
        if (!id.SequenceEqual("Xing"u8) && !id.SequenceEqual("Info"u8)) return false;
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(frame[(offset + 4)..]);
        int position = offset + 8;
        int frames = 0;
        if ((flags & 1) != 0 && frame.Length >= position + 4)
        {
            frames = BinaryPrimitives.ReadInt32BigEndian(frame[position..]);
            position += 4;
        }
        if ((flags & 2) != 0) position += 4;
        if ((flags & 4) != 0) position += 100;
        if ((flags & 8) != 0) position += 4;
        // The LAME extension: a 9-byte encoder name, 12 bytes of settings, then delay and padding as two 12-bit values.
        int delay = 0, padding = 0;
        bool gapless = false;
        if (frame.Length >= position + 24)
        {
            ReadOnlySpan<byte> name = frame.Slice(position, 4);
            if (name.SequenceEqual("LAME"u8) || name.SequenceEqual("Lavf"u8) || name.SequenceEqual("Lavc"u8))
            {
                ReadOnlySpan<byte> packed = frame.Slice(position + 21, 3);
                delay = packed[0] << 4 | packed[1] >> 4;
                padding = (packed[1] & 15) << 8 | packed[2];
                gapless = delay < 4608 && padding < 4608;
            }
        }
        tag = new Mp3InfoTag(frames, delay, padding, gapless);
        return true;
    }
}
