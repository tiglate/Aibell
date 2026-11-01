namespace Aibell.Audio.Midi;

/// <summary>
/// Plays a Standard MIDI File as stereo audio: the file is laid out on the sample timeline (tempo map applied) and rendered by the built-in
/// <see cref="MidiSynth"/>, which needs no sound bank. The length is known up front (end of the last track plus a short release tail).
/// Seeking restores the controller state (program, volume, pan, bend, ...) at the target; notes already sounding at that point are not
/// restarted.
/// </summary>
public sealed class MidiDecoder : IAudioDecoder
{
    public const int DefaultSampleRate = 44100;

    private const double TailSeconds = 3.0;

    private readonly MidiSequence sequence;
    private readonly MidiSynth synth;
    private long position;
    private int next;
    private bool disposed;

    private MidiDecoder(MidiFile file, int sampleRate, SoundFont? bank)
    {
        File = file;
        SampleRate = sampleRate;
        sequence = MidiSequence.Build(file, sampleRate);
        synth = new MidiSynth(sampleRate, bank);
        TotalFrames = sequence.Events.Length == 0 ? 0 : sequence.EndFrame + (long)(TailSeconds * sampleRate);
    }

    public MidiFile File { get; }

    public int SampleRate { get; }

    public int Channels => 2;

    public long TotalFrames { get; }

    /// <summary>True when the bytes start a Standard MIDI File or an RMID file.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head) => MidiFile.Sniff(head);

    /// <summary>Reads the whole file. Throws <see cref="InvalidDataException"/> for anything that is not a MIDI file.</summary>
    public static MidiDecoder Open(Stream stream, int sampleRate = DefaultSampleRate, SoundFont? soundFont = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192000);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > MidiFile.MaxFileBytes) throw new InvalidDataException("The MIDI file is too large.");
            buffer.Write(chunk, 0, read);
        }
        return new MidiDecoder(MidiFile.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)), sampleRate, soundFont);
    }

    public int Read(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frames <= 0) return 0;
        int wanted = (int)Math.Min(frames, TotalFrames - position);
        if (wanted <= 0) return 0;
        TimedEvent[] events = sequence.Events;
        int done = 0;
        while (done < wanted)
        {
            while (next < events.Length && events[next].Frame <= position) synth.Handle(events[next++]);
            long until = next < events.Length ? events[next].Frame : long.MaxValue;
            int count = (int)Math.Min(wanted - done, until - position);
            synth.Render(destination.Slice(done * 2, count * 2), count);
            position += count;
            done += count;
        }
        return wanted;
    }

    public bool Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame < 0 || frame > TotalFrames) return false;
        synth.Reset();
        TimedEvent[] events = sequence.Events;
        next = 0;
        while (next < events.Length && events[next].Frame < frame) synth.Handle(events[next++], sound: false);
        position = frame;
        return true;
    }

    public void Dispose() => disposed = true;
}
