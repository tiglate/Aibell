namespace Aibell.Audio.Midi.Tests;

/// <summary>Writes Standard MIDI Files for the tests.</summary>
internal sealed class MidiBuilder
{
    private readonly List<byte[]> tracks = [];

    public int Format { get; init; } = 1;

    public int Division { get; init; } = 480;

    public MidiBuilder Track(Action<TrackWriter> write)
    {
        var writer = new TrackWriter();
        write(writer);
        tracks.Add(writer.Finish());
        return this;
    }

    public byte[] ToArray(bool endOfTrack = true)
    {
        var bytes = new List<byte>();
        bytes.AddRange("MThd"u8.ToArray());
        bytes.AddRange([0, 0, 0, 6, 0, (byte)Format, (byte)(tracks.Count >> 8), (byte)tracks.Count, (byte)(Division >> 8), (byte)Division]);
        foreach (byte[] track in tracks)
        {
            bytes.AddRange("MTrk"u8.ToArray());
            bytes.AddRange([(byte)(track.Length >> 24), (byte)(track.Length >> 16), (byte)(track.Length >> 8), (byte)track.Length]);
            bytes.AddRange(track);
        }
        return [.. bytes];
    }

    public sealed class TrackWriter
    {
        private readonly List<byte> body = [];
        private long last;
        private long end;

        public TrackWriter At(long tick)
        {
            Vlq(tick - last);
            last = tick;
            return this;
        }

        public TrackWriter NoteOn(long tick, int channel, int note, int velocity) => Message(tick, 0x90 | channel, note, velocity);

        public TrackWriter NoteOff(long tick, int channel, int note) => Message(tick, 0x80 | channel, note, 0);

        public TrackWriter Controller(long tick, int channel, int controller, int value) => Message(tick, 0xB0 | channel, controller, value);

        public TrackWriter Program(long tick, int channel, int program)
        {
            At(tick);
            body.AddRange([(byte)(0xC0 | channel), (byte)program]);
            return this;
        }

        public TrackWriter Bend(long tick, int channel, int value14) => Message(tick, 0xE0 | channel, value14 & 0x7F, value14 >> 7);

        public TrackWriter Tempo(long tick, int microsPerQuarter)
        {
            At(tick);
            body.AddRange([0xFF, 0x51, 3, (byte)(microsPerQuarter >> 16), (byte)(microsPerQuarter >> 8), (byte)microsPerQuarter]);
            return this;
        }

        public TrackWriter Name(string name)
        {
            At(last);
            body.AddRange([0xFF, 0x03, (byte)name.Length]);
            body.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
            return this;
        }

        public TrackWriter Raw(params byte[] bytes)
        {
            body.AddRange(bytes);
            return this;
        }

        public TrackWriter End(long tick)
        {
            end = tick;
            return this;
        }

        public byte[] Finish()
        {
            Vlq(Math.Max(end, last) - last);
            body.AddRange([0xFF, 0x2F, 0]);
            return [.. body];
        }

        private TrackWriter Message(long tick, int status, int d1, int d2)
        {
            At(tick);
            body.AddRange([(byte)status, (byte)d1, (byte)d2]);
            return this;
        }

        private void Vlq(long value)
        {
            var parts = new Stack<byte>();
            parts.Push((byte)(value & 0x7F));
            for (value >>= 7; value > 0; value >>= 7) parts.Push((byte)(value & 0x7F | 0x80));
            body.AddRange(parts);
        }
    }
}
