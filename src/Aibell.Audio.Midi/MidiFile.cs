using System.Buffers.Binary;

namespace Aibell.Audio.Midi;

/// <summary>One channel message (or, with status 0xFF, a General MIDI reset) at a tick of its track.</summary>
internal readonly record struct RawEvent(long Tick, byte Status, byte Data1, byte Data2);

/// <summary>A message placed on the sample timeline.</summary>
internal readonly record struct TimedEvent(long Frame, byte Status, byte Data1, byte Data2);

internal sealed class MidiTrack
{
    public List<RawEvent> Events { get; } = [];

    public List<(long Tick, int MicrosPerQuarter)> Tempos { get; } = [];

    public long EndTick { get; set; }
}

/// <summary>
/// A Standard MIDI File (formats 0, 1 and 2, also inside an RMID wrapper): header, tracks with running status, variable-length
/// times, tempo and track-name meta events, and the General MIDI reset. Format reference: the MIDI 1.0 Detailed Specification
/// and the Standard MIDI Files 1.0 specification (RP-001). Damage inside a track ends that track instead of failing the file.
/// </summary>
public sealed class MidiFile
{
    /// <summary>Largest file read into memory.</summary>
    public const int MaxFileBytes = 32 * 1024 * 1024;

    private MidiFile(int format, int division, double ticksPerSecond, List<MidiTrack> tracks, string? title)
    {
        Format = format;
        Division = division;
        TicksPerSecond = ticksPerSecond;
        Tracks = tracks;
        Title = title;
    }

    public int Format { get; }

    public int TrackCount => Tracks.Count;

    /// <summary>Ticks per quarter note, or 0 when the file counts SMPTE time.</summary>
    public int Division { get; }

    /// <summary>Ticks per second for SMPTE time, or 0 when the file counts quarter notes (tempo applies).</summary>
    public double TicksPerSecond { get; }

    /// <summary>The first track name meta event, if any.</summary>
    public string? Title { get; }

    internal List<MidiTrack> Tracks { get; }

    /// <summary>True when the bytes start a Standard MIDI File or an RMID wrapper around one.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[..4].SequenceEqual("MThd"u8)
        || head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("RMID"u8);

    /// <summary>Parses a whole file. Throws <see cref="InvalidDataException"/> when there is no usable header or track.</summary>
    public static MidiFile Parse(ReadOnlySpan<byte> data)
    {
        data = Unwrap(data);
        if (data.Length < 14 || !data[..4].SequenceEqual("MThd"u8)) throw new InvalidDataException("This is not a MIDI file.");
        uint headerLength = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (headerLength < 6 || headerLength > data.Length - 8) throw new InvalidDataException("The MIDI header is damaged.");
        int format = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        int declared = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        short rawDivision = BinaryPrimitives.ReadInt16BigEndian(data[12..]);
        if (format > 2) throw new InvalidDataException($"MIDI format {format} is not defined.");
        if (format == 0 && declared > 1) declared = 1;
        int division = 0;
        double ticksPerSecond = 0;
        if (rawDivision > 0)
        {
            division = rawDivision;
        }
        else
        {
            int fps = -(rawDivision >> 8);
            int perFrame = rawDivision & 0xFF;
            if (fps is not (24 or 25 or 29 or 30) || perFrame == 0) throw new InvalidDataException("The MIDI time division is invalid.");
            ticksPerSecond = (fps == 29 ? 29.97 : fps) * perFrame;
        }

        var tracks = new List<MidiTrack>();
        string? title = null;
        int position = 8 + (int)headerLength;
        while (position + 8 <= data.Length)
        {
            ReadOnlySpan<byte> id = data.Slice(position, 4);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
            position += 8;
            int available = (int)Math.Min(length, (uint)(data.Length - position));
            if (id.SequenceEqual("MTrk"u8) && tracks.Count < Math.Max(declared, 1) + 1024)
            {
                tracks.Add(ParseTrack(data.Slice(position, available), tracks.Count == 0, ref title));
            }
            if (length > available) break;
            position += available;
        }
        if (tracks.Count == 0) throw new InvalidDataException("The MIDI file has no tracks.");
        return new MidiFile(format, division, ticksPerSecond, tracks, title);
    }

    // An RMID file is RIFF with a "data" chunk that holds the SMF.
    private static ReadOnlySpan<byte> Unwrap(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("RMID"u8)) return data;
        int position = 12;
        while (position + 8 <= data.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
            int body = position + 8;
            if (data.Slice(position, 4).SequenceEqual("data"u8)) return data[body..(int)Math.Min(data.Length, body + (long)size)];
            long next = body + (long)size + (size & 1);
            if (next > data.Length) break;
            position = (int)next;
        }
        throw new InvalidDataException("The RMID file has no MIDI data.");
    }

    private static MidiTrack ParseTrack(ReadOnlySpan<byte> data, bool first, ref string? title)
    {
        var track = new MidiTrack();
        int p = 0;
        long tick = 0;
        int running = 0;
        while (p < data.Length)
        {
            if (!TryReadVlq(data, ref p, out uint delta)) break;
            tick += delta;
            track.EndTick = tick;
            if (p >= data.Length) break;
            int status = data[p];
            if (status < 0x80)
            {
                if (running == 0) break;
                status = running;
            }
            else
            {
                p++;
                running = status < 0xF0 ? status : 0;
            }

            if (status == 0xFF)
            {
                if (p >= data.Length) break;
                int type = data[p++];
                if (!TryReadVlq(data, ref p, out uint metaLength) || metaLength > data.Length - p) break;
                ReadOnlySpan<byte> body = data.Slice(p, (int)metaLength);
                p += (int)metaLength;
                if (type == 0x2F) break;
                if (type == 0x51 && body.Length == 3)
                {
                    int micros = body[0] << 16 | body[1] << 8 | body[2];
                    if (micros > 0) track.Tempos.Add((tick, micros));
                }
                else if (type == 0x03 && first && title is null && body.Length > 0)
                {
                    title = System.Text.Encoding.Latin1.GetString(body).Trim('\0', ' ');
                    if (title.Length == 0) title = null;
                }
            }
            else if (status is 0xF0 or 0xF7)
            {
                if (!TryReadVlq(data, ref p, out uint sysexLength) || sysexLength > data.Length - p) break;
                ReadOnlySpan<byte> body = data.Slice(p, (int)sysexLength);
                p += (int)sysexLength;
                // F0 7E <device> 09 01 F7: General MIDI System On.
                if (status == 0xF0 && body.Length >= 5 && body[0] == 0x7E && body[2] == 0x09 && body[3] == 0x01)
                {
                    track.Events.Add(new RawEvent(tick, 0xFF, 0, 0));
                }
            }
            else if (status >= 0xF1)
            {
                break;
            }
            else
            {
                int kind = status & 0xF0;
                int count = kind is 0xC0 or 0xD0 ? 1 : 2;
                if (p + count > data.Length) break;
                byte d1 = (byte)(data[p] & 0x7F);
                byte d2 = count == 2 ? (byte)(data[p + 1] & 0x7F) : (byte)0;
                p += count;
                if (kind is 0x80 or 0x90 or 0xB0 or 0xC0 or 0xE0) track.Events.Add(new RawEvent(tick, (byte)status, d1, d2));
            }
        }
        return track;
    }

    private static bool TryReadVlq(ReadOnlySpan<byte> data, ref int p, out uint value)
    {
        value = 0;
        for (int i = 0; i < 4; i++)
        {
            if (p >= data.Length) return false;
            byte b = data[p++];
            value = value << 7 | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }
}
