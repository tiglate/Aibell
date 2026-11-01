using System.Text;

namespace Aibell.Audio.Spc;

/// <summary>Text from the ID666 tag and the extended (xid6) chunk of an SPC file; unset fields are null.</summary>
public sealed record SpcTags(
    string? Title, string? Game, string? Artist, string? Dumper, string? Comments, string? Publisher, string? OstTitle,
    int Year, int DiscNumber, int TrackNumber);

/// <summary>
/// An SPC700 sound file: the saved CPU registers, the 64 KB of RAM and the DSP registers of a SNES sound unit, plus the optional ID666
/// tag and the xid6 extension that carry the title and the play length. See https://wiki.superfamicom.org/spc-and-rsn-file-format.
/// </summary>
public sealed class SpcFile
{
    public const int HeaderLength = 0x100;

    /// <summary>The smallest valid file: header, RAM, DSP registers, unused bytes and the extra RAM.</summary>
    public const int MinimumLength = 0x10200;

    public const int MaxFileBytes = 1 << 20;

    /// <summary>Ticks per second in xid6 lengths.</summary>
    public const int TicksPerSecond = 64000;

    /// <summary>Played when the file gives no length.</summary>
    public const int DefaultSeconds = 180;

    public const int DefaultFadeMilliseconds = 10000;

    private static readonly byte[] Magic = "SNES-SPC700 Sound File Data"u8.ToArray();

    private SpcFile(byte[] data) => Data = data;

    internal byte[] Data { get; }

    public int Pc => Data[0x25] | Data[0x26] << 8;

    public byte A => Data[0x27];

    public byte X => Data[0x28];

    public byte Y => Data[0x29];

    public byte Psw => Data[0x2A];

    public byte Sp => Data[0x2B];

    /// <summary>The 64 KB of sound RAM as saved.</summary>
    public ReadOnlySpan<byte> Ram => Data.AsSpan(0x100, 0x10000);

    public ReadOnlySpan<byte> DspRegisters => Data.AsSpan(0x10100, 128);

    /// <summary>The 64 bytes of RAM hidden under the boot ROM at FFC0h.</summary>
    public ReadOnlySpan<byte> ExtraRam => Data.AsSpan(0x101C0, 64);

    public bool HasId666 => Data[0x23] == 26;

    public SpcTags Tags { get; private set; } = new(null, null, null, null, null, null, null, 0, 0, 0);

    /// <summary>Length of the part before the fade, or zero when the file does not say.</summary>
    public TimeSpan PlayLength { get; private set; }

    public TimeSpan FadeLength { get; private set; }

    /// <summary>The play and fade lengths to use: those of the tag, or the defaults when the tag has no play length.</summary>
    public (TimeSpan Play, TimeSpan Fade) ResolveLengths()
    {
        if (PlayLength > TimeSpan.Zero) return (PlayLength, FadeLength);
        return (TimeSpan.FromSeconds(DefaultSeconds), FadeLength > TimeSpan.Zero ? FadeLength : TimeSpan.FromMilliseconds(DefaultFadeMilliseconds));
    }

    /// <summary>True when the bytes start an SPC file.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head) => head.Length >= Magic.Length && head[..Magic.Length].SequenceEqual(Magic);

    /// <summary>Parses a whole file. Throws <see cref="InvalidDataException"/> when it is not an SPC file or is cut short.</summary>
    public static SpcFile Parse(ReadOnlySpan<byte> file)
    {
        if (!Sniff(file)) throw new InvalidDataException("This is not an SPC file.");
        if (file.Length < MinimumLength) throw new InvalidDataException("The SPC file is too short.");
        var spc = new SpcFile(file.ToArray());
        spc.ReadTags();
        return spc;
    }

    public static SpcFile Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes) throw new InvalidDataException("The SPC file is too large.");
            buffer.Write(chunk, 0, read);
        }
        return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private void ReadTags()
    {
        string? title = null, game = null, artist = null, dumper = null, comments = null;
        TimeSpan play = TimeSpan.Zero, fade = TimeSpan.Zero;
        if (HasId666)
        {
            bool binary = IsBinaryTag();
            title = Text(0x2E, 32);
            game = Text(0x4E, 32);
            dumper = Text(0x6E, 16);
            comments = Text(0x7E, 32);
            artist = Text(binary ? 0xB0 : 0xB1, 32);
            long seconds, fadeMs;
            if (binary)
            {
                seconds = Data[0xA9] | Data[0xAA] << 8 | Data[0xAB] << 16;
                fadeMs = (uint)(Data[0xAC] | Data[0xAD] << 8 | Data[0xAE] << 16 | Data[0xAF] << 24);
            }
            else
            {
                seconds = Number(0xA9, 3);
                fadeMs = Number(0xAC, 5);
            }
            if (seconds is > 0 and < 100000) play = TimeSpan.FromSeconds(seconds);
            if (fadeMs is > 0 and < 10_000_000) fade = TimeSpan.FromMilliseconds(fadeMs);
        }

        string? publisher = null, ost = null;
        int year = 0, disc = 0, track = 0;
        if (Data.Length >= 0x10208 && Data.AsSpan(0x10200, 4).SequenceEqual("xid6"u8))
        {
            ReadExtended(ref title, ref game, ref artist, ref dumper, ref comments, ref publisher, ref ost, ref year, ref disc, ref track, ref play, ref fade);
        }
        Tags = new SpcTags(title, game, artist, dumper, comments, publisher, ost, year, disc, track);
        PlayLength = play;
        FadeLength = fade;
    }

    // The two tag variants share their offsets up to the length fields; text files hold digits there and binary files integers.
    private bool IsBinaryTag()
    {
        for (int i = 0xA9; i < 0xAC; i++)
        {
            if (Data[i] != 0 && Data[i] != (byte)' ' && (Data[i] < (byte)'0' || Data[i] > (byte)'9')) return true;
        }
        for (int i = 0xAC; i < 0xB1; i++)
        {
            if (Data[i] != 0 && Data[i] != (byte)' ' && (Data[i] < (byte)'0' || Data[i] > (byte)'9')) return true;
        }
        return false;
    }

    private long Number(int offset, int length)
    {
        long value = 0;
        for (int i = 0; i < length; i++)
        {
            byte b = Data[offset + i];
            if (b is < (byte)'0' or > (byte)'9') break;
            value = value * 10 + (b - '0');
        }
        return value;
    }

    private string? Text(int offset, int length) => Decode(Data.AsSpan(offset, length));

    private static string? Decode(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        if (bytes.IsEmpty) return null;
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }
        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    private void ReadExtended(ref string? title, ref string? game, ref string? artist, ref string? dumper, ref string? comments,
        ref string? publisher, ref string? ost, ref int year, ref int disc, ref int track, ref TimeSpan play, ref TimeSpan fade)
    {
        int size = BitConverter.ToInt32(Data, 0x10204);
        int end = (int)Math.Min((long)Data.Length, 0x10208L + Math.Max(0, size));
        int p = 0x10208;
        long intro = -1, loop = -1, tail = 0, fadeTicks = -1;
        int loops = 1;
        while (p + 4 <= end)
        {
            int id = Data[p], type = Data[p + 1];
            int value = Data[p + 2] | Data[p + 3] << 8;
            p += 4;
            ReadOnlySpan<byte> payload = default;
            if (type != 0)
            {
                int length = value;
                if (p + length > end) break;
                payload = Data.AsSpan(p, length);
                p += (length + 3) & ~3;
            }
            switch (id)
            {
                case 0x01: if (type == 1) title = Decode(payload) ?? title; break;
                case 0x02: if (type == 1) game = Decode(payload) ?? game; break;
                case 0x03: if (type == 1) artist = Decode(payload) ?? artist; break;
                case 0x04: if (type == 1) dumper = Decode(payload) ?? dumper; break;
                case 0x07: if (type == 1) comments = Decode(payload) ?? comments; break;
                case 0x10: if (type == 1) ost = Decode(payload); break;
                case 0x11: if (type == 0) disc = value & 0xFF; break;
                case 0x12: if (type == 0) track = value >> 8; break;
                case 0x13: if (type == 1) publisher = Decode(payload); break;
                case 0x14: if (type == 0) year = value; break;
                case 0x30: intro = Ticks(type, payload); break;
                case 0x31: loop = Ticks(type, payload); break;
                case 0x32: tail = Ticks(type, payload); break;
                case 0x33: fadeTicks = Ticks(type, payload); break;
                case 0x35: if (type == 0) loops = Math.Max(1, value & 0xFF); break;
            }
        }
        if (intro >= 0 || loop >= 0)
        {
            long total = Math.Max(0, intro) + Math.Max(0, loop) * loops + tail;
            if (total > 0) play = TimeSpan.FromSeconds((double)total / TicksPerSecond);
        }
        if (fadeTicks >= 0) fade = TimeSpan.FromSeconds((double)fadeTicks / TicksPerSecond);
    }

    private static long Ticks(int type, ReadOnlySpan<byte> payload)
    {
        if (type != 4 || payload.Length < 4) return -1;
        long ticks = BitConverter.ToInt32(payload);
        return Math.Clamp(ticks, -383_999_999, 383_999_999);
    }
}
