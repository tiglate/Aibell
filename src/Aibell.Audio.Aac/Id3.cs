namespace Aibell.Audio.Aac;

/// <summary>Recognizes ID3v2 tags, which packed-audio HLS segments put in front of every segment.</summary>
public static class Id3
{
    /// <summary>The whole length of the ID3v2 tag at the start of <paramref name="data"/> (header, body, footer), or 0 when there is none.</summary>
    public static int TagLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 || data[0] != 'I' || data[1] != 'D' || data[2] != '3' || data[3] == 0xff || data[4] == 0xff) return 0;
        if ((data[6] | data[7] | data[8] | data[9]) >= 0x80) return 0; // sizes are 7 bits per byte
        int size = data[6] << 21 | data[7] << 14 | data[8] << 7 | data[9];
        return 10 + size + ((data[5] & 0x10) != 0 ? 10 : 0);
    }
}
