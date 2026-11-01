namespace Aibell.Audio;

public enum AudioContainer { Unknown, Wav, Flac, OggVorbis, OggOpus, OggOther, Mp4, Midi, Spc }

/// <summary>Recognizes the managed decoders' formats from their first bytes.</summary>
public static class AudioSniffer
{
    /// <summary>Bytes needed to tell the formats apart.</summary>
    public const int HeadLength = 64;

    /// <summary>The size of an ID3v2 tag at the start of <paramref name="head"/> (header and footer included), or 0 when there is none.</summary>
    public static int Id3v2Length(ReadOnlySpan<byte> head)
    {
        if (head.Length < 10 || !head[..3].SequenceEqual("ID3"u8) || head[3] == 0xFF || head[4] == 0xFF) return 0;
        for (int i = 6; i < 10; i++)
        {
            if ((head[i] & 0x80) != 0) return 0;
        }
        int size = head[6] << 21 | head[7] << 14 | head[8] << 7 | head[9];
        return 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);
    }

    public static AudioContainer Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[..4].SequenceEqual("MThd"u8)) return AudioContainer.Midi;
        if (head.Length >= 27 && head[..27].SequenceEqual("SNES-SPC700 Sound File Data"u8)) return AudioContainer.Spc;
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("RMID"u8)) return AudioContainer.Midi;
        if (head.Length >= 12 && (head[..4].SequenceEqual("RIFF"u8) || head[..4].SequenceEqual("RF64"u8)) && head.Slice(8, 4).SequenceEqual("WAVE"u8)) return AudioContainer.Wav;
        if (head.Length >= 4 && head[..4].SequenceEqual("fLaC"u8)) return AudioContainer.Flac;
        if (head.Length >= 4 && head[..4].SequenceEqual("OggS"u8)) return DetectOgg(head);
        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8)) return AudioContainer.Mp4;
        return AudioContainer.Unknown;
    }

    // The first page of an Ogg stream holds the codec's identification packet alone: 27 header bytes, the lacing table, then the packet.
    private static AudioContainer DetectOgg(ReadOnlySpan<byte> head)
    {
        if (head.Length < 28) return AudioContainer.OggOther;
        int start = 27 + head[26];
        if (start >= head.Length) return AudioContainer.OggOther;
        ReadOnlySpan<byte> packet = head[start..];
        if (packet.Length >= 7 && packet[0] == 1 && packet.Slice(1, 6).SequenceEqual("vorbis"u8)) return AudioContainer.OggVorbis;
        if (packet.Length >= 8 && packet[..8].SequenceEqual("OpusHead"u8)) return AudioContainer.OggOpus;
        return AudioContainer.OggOther;
    }
}
