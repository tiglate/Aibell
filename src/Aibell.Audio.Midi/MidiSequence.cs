namespace Aibell.Audio.Midi;

/// <summary>A MIDI file laid out on a sample timeline: every message with its frame, the tempo map applied.</summary>
internal sealed class MidiSequence
{
    private const double MaxSeconds = 24 * 3600;

    private MidiSequence(TimedEvent[] events, long endFrame)
    {
        Events = events;
        EndFrame = endFrame;
    }

    /// <summary>Messages in playing order.</summary>
    public TimedEvent[] Events { get; }

    /// <summary>The frame where the last track ends (its end-of-track time, or its last message).</summary>
    public long EndFrame { get; }

    public static MidiSequence Build(MidiFile file, int sampleRate)
    {
        var events = new List<TimedEvent>();
        double start = 0;
        if (file.Format == 2)
        {
            // Format 2 holds independent songs; they play one after the other.
            foreach (MidiTrack track in file.Tracks) start = Append(file, [track], start, sampleRate, events);
        }
        else
        {
            start = Append(file, file.Tracks, 0, sampleRate, events);
        }
        return new MidiSequence([.. events], (long)Math.Ceiling(start));
    }

    private static double Append(MidiFile file, List<MidiTrack> tracks, double start, int sampleRate, List<TimedEvent> output)
    {
        var merged = new List<(RawEvent Event, int Sequence)>();
        var tempos = new List<(long Tick, int Micros, int Sequence)>();
        long endTick = 0;
        int sequence = 0;
        foreach (MidiTrack track in tracks)
        {
            foreach (RawEvent e in track.Events) merged.Add((e, sequence++));
            foreach ((long tick, int micros) in track.Tempos) tempos.Add((tick, micros, sequence++));
            endTick = Math.Max(endTick, track.EndTick);
        }
        // At one tick: note-offs, then everything else, then note-ons, so a repeated note retriggers.
        merged.Sort((a, b) =>
        {
            int byTick = a.Event.Tick.CompareTo(b.Event.Tick);
            if (byTick != 0) return byTick;
            int byClass = Class(a.Event).CompareTo(Class(b.Event));
            return byClass != 0 ? byClass : a.Sequence.CompareTo(b.Sequence);
        });
        tempos.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Sequence.CompareTo(b.Sequence));

        var clock = new TempoClock(file, sampleRate, start, tempos);
        foreach ((RawEvent e, _) in merged)
        {
            output.Add(new TimedEvent((long)Math.Round(clock.FrameAt(e.Tick)), e.Status, e.Data1, e.Data2));
        }
        return clock.FrameAt(endTick);
    }

    private static int Class(RawEvent e) => (e.Status & 0xF0) switch
    {
        0x80 => 0,
        0x90 => e.Data2 == 0 ? 0 : 2,
        _ => 1
    };

    // Converts ticks, asked for in nondecreasing order, to frames, walking the tempo map.
    private sealed class TempoClock
    {
        private readonly MidiFile file;
        private readonly int sampleRate;
        private readonly List<(long Tick, int Micros, int Sequence)> tempos;
        private int next;
        private long baseTick;
        private double baseFrame;
        private double framesPerTick;

        public TempoClock(MidiFile file, int sampleRate, double start, List<(long Tick, int Micros, int Sequence)> tempos)
        {
            this.file = file;
            this.sampleRate = sampleRate;
            this.tempos = tempos;
            baseFrame = start;
            framesPerTick = FramesPerTick(500_000);
        }

        public double FrameAt(long tick)
        {
            while (next < tempos.Count && tempos[next].Tick <= tick)
            {
                (long at, int micros, _) = tempos[next++];
                baseFrame += (at - baseTick) * framesPerTick;
                baseTick = at;
                framesPerTick = FramesPerTick(micros);
            }
            double frame = baseFrame + (tick - baseTick) * framesPerTick;
            if (frame > MaxSeconds * sampleRate) throw new InvalidDataException("The MIDI file plays for more than a day.");
            return frame;
        }

        private double FramesPerTick(int microsPerQuarter) => file.Division > 0
            ? microsPerQuarter * 1e-6 * sampleRate / file.Division
            : sampleRate / file.TicksPerSecond;
    }
}
