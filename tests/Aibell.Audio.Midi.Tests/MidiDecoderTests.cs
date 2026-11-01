namespace Aibell.Audio.Midi.Tests;

[TestFixture]
public class MidiDecoderTests
{
    private const int Rate = 44100;

    private static MidiDecoder Open(byte[] data) => MidiDecoder.Open(new MemoryStream(data), Rate);

    private static float[] ReadAll(MidiDecoder decoder)
    {
        var all = new List<float>();
        var buffer = new float[1024 * 2];
        int got;
        while ((got = decoder.Read(buffer, 1024)) > 0) all.AddRange(buffer.AsSpan(0, got * 2).ToArray());
        return [.. all];
    }

    // Peak absolute value of the left channel between two frames.
    private static float Peak(float[] pcm, double fromSeconds, double toSeconds)
    {
        float peak = 0;
        for (int i = (int)(fromSeconds * Rate); i < (int)(toSeconds * Rate) && i * 2 < pcm.Length; i++) peak = Math.Max(peak, Math.Abs(pcm[i * 2]));
        return peak;
    }

    // Magnitude of the left channel at each frequency (1 Hz steps up to maxHz), Hann window of 8192 frames starting at fromSeconds.
    private static double[] Spectrum(float[] pcm, double fromSeconds, int maxHz = 4000)
    {
        const int N = 8192;
        int start = (int)(fromSeconds * Rate);
        var window = new double[N];
        for (int i = 0; i < N; i++) window[i] = pcm[(start + i) * 2] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1)));
        var magnitude = new double[maxHz + 1];
        for (int f = 20; f <= maxHz; f++)
        {
            double re = 0, im = 0, w = 2 * Math.PI * f / Rate;
            for (int i = 0; i < N; i++)
            {
                re += window[i] * Math.Cos(w * i);
                im -= window[i] * Math.Sin(w * i);
            }
            magnitude[f] = Math.Sqrt(re * re + im * im);
        }
        return magnitude;
    }

    // Frequency of the strongest component.
    private static double Frequency(float[] pcm, double fromSeconds, int maxHz = 4000) => Array.IndexOf(Spectrum(pcm, fromSeconds, maxHz), Spectrum(pcm, fromSeconds, maxHz).Max());

    // Share of the spectral magnitude above 1.5 kHz.
    private static double BrightShare(float[] pcm, double fromSeconds)
    {
        double[] m = Spectrum(pcm, fromSeconds, 8000);
        return m.Skip(1500).Sum() / m.Sum();
    }

    private static byte[] SingleNote(int note = 69, int program = 0, int channel = 0) => new MidiBuilder().Track(t => t
        .Program(0, channel, program).NoteOn(0, channel, note, 100).NoteOff(480, channel, note)).ToArray();

    [Test]
    public void Parse_ReadsHeaderTracksAndTitle()
    {
        byte[] data = new MidiBuilder { Format = 1, Division = 96 }
            .Track(t => t.Name("Song").Tempo(0, 500000))
            .Track(t => t.NoteOn(0, 0, 60, 90).NoteOff(96, 0, 60)).ToArray();

        MidiFile file = MidiFile.Parse(data);

        Assert.That(file.Format, Is.EqualTo(1));
        Assert.That(file.TrackCount, Is.EqualTo(2));
        Assert.That(file.Division, Is.EqualTo(96));
        Assert.That(file.Title, Is.EqualTo("Song"));
    }

    [Test]
    public void Read_NoteLengthFollowsTempo()
    {
        // One quarter note at 120 bpm lasts half a second.
        using MidiDecoder decoder = Open(SingleNote());
        Assert.That(decoder.TotalFrames, Is.EqualTo(Rate / 2 + 3 * Rate));
        float[] pcm = ReadAll(decoder);
        Assert.That(pcm.Length, Is.EqualTo(decoder.TotalFrames * 2));
        Assert.That(Peak(pcm, 0.05, 0.4), Is.GreaterThan(0.02f));
        Assert.That(Peak(pcm, 1.8, 2.0), Is.LessThan(1e-3f));
    }

    [Test]
    public void Read_TempoChangeShortensLaterTicks()
    {
        byte[] data = new MidiBuilder().Track(t => t.Tempo(0, 500000).Tempo(480, 250000).NoteOn(480, 0, 69, 100).NoteOff(960, 0, 69)).ToArray();
        MidiSequence sequence = MidiSequence.Build(MidiFile.Parse(data), Rate);
        Assert.That(sequence.Events.Select(e => e.Frame), Is.EqualTo(new long[] { Rate / 2, Rate / 2 + Rate / 4 }));
    }

    [Test]
    public void Read_PlaysTheNotePitch()
    {
        using MidiDecoder a4 = Open(SingleNote(69));
        float[] pcm = ReadAll(a4);
        Assert.That(Frequency(pcm, 0.1), Is.EqualTo(440).Within(8));

        using MidiDecoder a5 = Open(SingleNote(81));
        Assert.That(Frequency(ReadAll(a5), 0.1), Is.EqualTo(880).Within(10));
    }

    [Test]
    public void Read_PitchBendMovesThePitch()
    {
        byte[] data = new MidiBuilder().Track(t => t.Bend(0, 0, 16383).NoteOn(0, 0, 69, 100).NoteOff(960, 0, 69)).ToArray();
        using MidiDecoder decoder = Open(data);
        // Full bend up with the default range of two semitones: 440 * 2^(2/12) = 493.9 Hz.
        Assert.That(Frequency(ReadAll(decoder), 0.1), Is.EqualTo(493.9).Within(8));
    }

    [Test]
    public void Read_SustainPedalHoldsReleasedNotes()
    {
        // A pad (program 88) with the note released at tick 48 and the pedal lifted at tick 960 (one second).
        byte[] pedal = new MidiBuilder().Track(t => t.Program(0, 0, 88).Controller(0, 0, 64, 127).NoteOn(0, 0, 60, 100).NoteOff(48, 0, 60).Controller(960, 0, 64, 0)).ToArray();
        byte[] noPedal = new MidiBuilder().Track(t => t.Program(0, 0, 88).NoteOn(0, 0, 60, 100).NoteOff(48, 0, 60).End(960)).ToArray();
        using MidiDecoder held = Open(pedal);
        using MidiDecoder free = Open(noPedal);
        Assert.That(Peak(ReadAll(held), 0.7, 0.95), Is.GreaterThan(0.02f));
        Assert.That(Peak(ReadAll(free), 0.7, 0.95), Is.LessThan(1e-3f));
    }

    [Test]
    public void Read_VelocityZeroEndsTheNote()
    {
        byte[] ended = new MidiBuilder().Track(t => t.Program(0, 0, 16).Controller(0, 0, 91, 0).NoteOn(0, 0, 69, 100).NoteOn(240, 0, 69, 0).End(960)).ToArray();
        byte[] held = new MidiBuilder().Track(t => t.Program(0, 0, 16).Controller(0, 0, 91, 0).NoteOn(0, 0, 69, 100).End(960)).ToArray();
        using MidiDecoder a = Open(ended);
        using MidiDecoder b = Open(held);
        float[] stopped = ReadAll(a);
        float[] sustained = ReadAll(b);
        Assert.That(Peak(stopped, 0.05, 0.2), Is.GreaterThan(0.005f));
        Assert.That(Peak(stopped, 0.8, 0.95), Is.LessThan(1e-3f));
        Assert.That(Peak(sustained, 0.8, 0.95), Is.GreaterThan(0.02f));
    }

    [Test]
    public void Parse_HonoursRunningStatus()
    {
        // Note on 69, note on 71 (running status), note off 69, note off 71 (running status).
        byte[] body = [0, 0x90, 69, 100, 0x60, 71, 100, 0x60, 0x80, 69, 0, 0, 71, 0, 0, 0xFF, 0x2F, 0];
        byte[] file = [.. "MThd"u8.ToArray(), 0, 0, 0, 6, 0, 0, 0, 1, 1, 224, .. "MTrk"u8.ToArray(), 0, 0, 0, (byte)body.Length, .. body];
        MidiFile parsed = MidiFile.Parse(file);
        Assert.That(parsed.Tracks[0].Events.Select(e => e.Data1), Is.EqualTo(new byte[] { 69, 71, 69, 71 }));
        Assert.That(parsed.Tracks[0].Events.Select(e => e.Tick), Is.EqualTo(new long[] { 0, 96, 192, 192 }));
    }

    [Test]
    public void Read_PercussionOnChannelTenSounds()
    {
        byte[] data = new MidiBuilder().Track(t => t.NoteOn(0, 9, 36, 110).NoteOn(240, 9, 38, 110).NoteOn(480, 9, 42, 110).End(960)).ToArray();
        using MidiDecoder decoder = Open(data);
        float[] pcm = ReadAll(decoder);
        Assert.That(Peak(pcm, 0, 0.1), Is.GreaterThan(0.05f));
        Assert.That(Peak(pcm, 0.25, 0.35), Is.GreaterThan(0.02f));
        Assert.That(Peak(pcm, 0.5, 0.55), Is.GreaterThan(0.01f));
    }

    [Test]
    public void Read_ManyLoudNotesStayWithinRange()
    {
        byte[] data = new MidiBuilder().Track(t =>
        {
            for (int n = 0; n < 100; n++) t.NoteOn(0, n % 9, 30 + n % 60, 127);
            t.End(480);
        }).ToArray();
        using MidiDecoder decoder = Open(data);
        float[] pcm = ReadAll(decoder);
        Assert.That(pcm.All(x => !float.IsNaN(x) && Math.Abs(x) <= 1f));
        Assert.That(pcm.Max(Math.Abs), Is.GreaterThan(0.3f));
    }

    [Test]
    public void Read_IsDeterministicAcrossBlockSizes()
    {
        byte[] data = new MidiBuilder().Track(t => t.NoteOn(0, 9, 38, 100).NoteOn(0, 0, 60, 100).NoteOff(300, 0, 60).NoteOn(333, 0, 64, 90).NoteOff(700, 0, 64)).ToArray();
        using MidiDecoder a = Open(data);
        using MidiDecoder b = Open(data);
        float[] whole = ReadAll(a);
        var pieces = new List<float>();
        var buffer = new float[2 * 777];
        int got;
        while ((got = b.Read(buffer, 777)) > 0) pieces.AddRange(buffer.AsSpan(0, got * 2).ToArray());
        Assert.That(pieces, Is.EqualTo(whole));
    }

    [Test]
    public void Seek_RestoresControllersAndSkipsEarlierNotes()
    {
        byte[] data = new MidiBuilder().Track(t => t
            .Controller(0, 0, 7, 0).NoteOn(0, 0, 69, 100).NoteOff(480, 0, 69)
            .Controller(480, 0, 7, 127).NoteOn(960, 0, 69, 100).NoteOff(1440, 0, 69)).ToArray();
        using MidiDecoder decoder = Open(data);
        // Seeking to 0.9 s lands after the volume-127 controller (0.5 s) and before the second note (1.0 s).
        Assert.That(decoder.Seek(Rate * 9 / 10), Is.True);
        float[] rest = ReadAll(decoder);
        Assert.That(rest.Length, Is.EqualTo((decoder.TotalFrames - Rate * 9 / 10) * 2));
        Assert.That(Peak(rest, 0.15, 0.45), Is.GreaterThan(0.02f));
        Assert.That(decoder.Seek(0), Is.True);
        float[] first = ReadAll(decoder);
        // The first note plays at volume 0.
        Assert.That(Peak(first, 0, 0.45), Is.LessThan(1e-4f));
    }

    [Test]
    public void Seek_RejectsOutOfRange()
    {
        using MidiDecoder decoder = Open(SingleNote());
        Assert.That(decoder.Seek(-1), Is.False);
        Assert.That(decoder.Seek(decoder.TotalFrames + 1), Is.False);
        Assert.That(decoder.Seek(decoder.TotalFrames), Is.True);
        Assert.That(decoder.Read(new float[64], 32), Is.EqualTo(0));
    }

    [Test]
    public void Format2_PlaysTracksOneAfterTheOther()
    {
        byte[] data = new MidiBuilder { Format = 2 }
            .Track(t => t.NoteOn(0, 0, 60, 100).NoteOff(480, 0, 60))
            .Track(t => t.NoteOn(0, 0, 62, 100).NoteOff(480, 0, 62)).ToArray();
        MidiSequence sequence = MidiSequence.Build(MidiFile.Parse(data), Rate);
        Assert.That(sequence.Events.Select(e => e.Frame), Is.EqualTo(new long[] { 0, Rate / 2, Rate / 2, Rate }));
    }

    [Test]
    public void Parse_SmpteDivisionCountsRealTime()
    {
        // 25 fps, 40 ticks per frame: 1000 ticks per second.
        byte[] data = new MidiBuilder { Division = unchecked((ushort)((-25 << 8) | 40)) }.Track(t => t.NoteOn(0, 0, 60, 100).NoteOff(1000, 0, 60)).ToArray();
        MidiSequence sequence = MidiSequence.Build(MidiFile.Parse(data), Rate);
        Assert.That(sequence.Events[1].Frame, Is.EqualTo(Rate));
    }

    [Test]
    public void Parse_AcceptsRmidWrapper()
    {
        byte[] smf = SingleNote();
        byte[] riff = [.. "RIFF"u8.ToArray(), .. BitConverter.GetBytes(smf.Length + 12), .. "RMID"u8.ToArray(), .. "data"u8.ToArray(), .. BitConverter.GetBytes(smf.Length), .. smf];
        Assert.That(MidiFile.Sniff(riff), Is.True);
        Assert.That(MidiFile.Parse(riff).TrackCount, Is.EqualTo(1));
        Assert.That(AudioSniffer.Detect(riff), Is.EqualTo(AudioContainer.Midi));
        Assert.That(AudioSniffer.Detect(SingleNote()), Is.EqualTo(AudioContainer.Midi));
    }

    [Test]
    public void Parse_ToleratesATruncatedTrack()
    {
        byte[] data = SingleNote();
        MidiFile file = MidiFile.Parse(data.AsSpan(0, data.Length - 6));
        Assert.That(file.TrackCount, Is.EqualTo(1));
        Assert.That(file.Tracks[0].Events, Is.Not.Empty);
    }

    [TestCase(new byte[] { })]
    [TestCase(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 })]
    public void Open_RejectsNonMidi(byte[] data)
    {
        Assert.Throws<InvalidDataException>(() => Open(data));
    }

    [Test]
    public void Open_RejectsHeaderWithoutTracksAndBadDivision()
    {
        byte[] noTracks = [.. "MThd"u8.ToArray(), 0, 0, 0, 6, 0, 1, 0, 0, 1, 224];
        Assert.Throws<InvalidDataException>(() => Open(noTracks));
        byte[] badFormat = [.. "MThd"u8.ToArray(), 0, 0, 0, 6, 0, 7, 0, 1, 1, 224];
        Assert.Throws<InvalidDataException>(() => Open(badFormat));
        byte[] badSmpte = [.. "MThd"u8.ToArray(), 0, 0, 0, 6, 0, 1, 0, 1, 0xE7, 0];
        Assert.Throws<InvalidDataException>(() => Open(badSmpte));
    }

    [Test]
    public void Open_SurvivesRandomDamage()
    {
        byte[] good = new MidiBuilder().Track(t => t.Tempo(0, 400000).Program(0, 1, 40).NoteOn(0, 1, 60, 90).NoteOff(480, 1, 60)).ToArray();
        var random = new Random(7);
        for (int round = 0; round < 300; round++)
        {
            byte[] bad = (byte[])good.Clone();
            for (int k = 0; k < 4; k++) bad[random.Next(bad.Length)] = (byte)random.Next(256);
            try
            {
                using MidiDecoder decoder = Open(bad);
                if (decoder.TotalFrames < 30 * Rate) ReadAll(decoder);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Test]
    public void Reset_GeneralMidiSystemOnSilencesNotes()
    {
        byte[] data = new MidiBuilder().Track(t =>
        {
            t.NoteOn(0, 0, 69, 100);
            t.At(240);
            t.Raw(0xF0, 5, 0x7E, 0x7F, 0x09, 0x01, 0xF7);
            t.End(960);
        }).ToArray();
        using MidiDecoder decoder = Open(data);
        float[] pcm = ReadAll(decoder);
        Assert.That(Peak(pcm, 0.1, 0.2), Is.GreaterThan(0.02f));
        Assert.That(Peak(pcm, 0.3, 0.9), Is.LessThan(1e-4f));
    }

    [Test]
    public void Reverb_SendAddsATailAfterTheNote()
    {
        byte[] wet = new MidiBuilder().Track(t => t.Controller(0, 0, 91, 127).NoteOn(0, 0, 60, 110).NoteOff(48, 0, 60)).ToArray();
        byte[] dry = new MidiBuilder().Track(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 110).NoteOff(48, 0, 60)).ToArray();
        using MidiDecoder a = Open(wet);
        using MidiDecoder b = Open(dry);
        float tail = Peak(ReadAll(a), 0.5, 0.8);
        float none = Peak(ReadAll(b), 0.5, 0.8);
        Assert.That(tail, Is.GreaterThan(0.001f));
        Assert.That(tail, Is.GreaterThan(none * 5));
    }

    [Test]
    public void Reverb_TailDiesOutAndSeekClearsIt()
    {
        byte[] data = new MidiBuilder().Track(t => t.Controller(0, 0, 91, 127).NoteOn(0, 0, 60, 127).NoteOff(48, 0, 60)).ToArray();
        using MidiDecoder decoder = Open(data);
        float[] pcm = ReadAll(decoder);
        Assert.That(Peak(pcm, 2.7, 3.0), Is.LessThan(2e-3f));
        // After a seek to the end of the file nothing of the earlier tail may be heard.
        decoder.Seek(decoder.TotalFrames - 1000);
        var buffer = new float[2000];
        Assert.That(decoder.Read(buffer, 1000), Is.EqualTo(1000));
        Assert.That(buffer.Max(Math.Abs), Is.EqualTo(0f));
    }

    [Test]
    public void Chorus_SendWidensTheSound()
    {
        float Side(int send)
        {
            byte[] data = new MidiBuilder().Track(t => t.Controller(0, 0, 91, 0).Controller(0, 0, 93, send).NoteOn(0, 0, 60, 110).NoteOff(480, 0, 60)).ToArray();
            using MidiDecoder decoder = Open(data);
            float[] pcm = ReadAll(decoder);
            float side = 0;
            for (int i = Rate / 10; i < Rate / 2; i++) side = Math.Max(side, Math.Abs(pcm[i * 2] - pcm[i * 2 + 1]));
            return side;
        }
        Assert.That(Side(0), Is.LessThan(1e-5f));
        Assert.That(Side(127), Is.GreaterThan(0.005f));
    }

    [Test]
    public void Brass_FilterClosesAfterTheAttack()
    {
        byte[] data = new MidiBuilder().Track(t => t.Program(0, 0, 56).Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 127).NoteOff(1920, 0, 60)).ToArray();
        using MidiDecoder decoder = Open(data);
        float[] pcm = ReadAll(decoder);
        Assert.That(BrightShare(pcm, 0.05), Is.GreaterThan(BrightShare(pcm, 1.5) * 1.3));
    }

    [Test]
    public void Modulation_WheelBendsThePitchPeriodically()
    {
        byte[] still = new MidiBuilder().Track(t => t.Program(0, 0, 73).Controller(0, 0, 91, 0).NoteOn(0, 0, 69, 100).NoteOff(960, 0, 69)).ToArray();
        byte[] wheel = new MidiBuilder().Track(t => t.Program(0, 0, 73).Controller(0, 0, 91, 0).Controller(0, 0, 1, 127).NoteOn(0, 0, 69, 100).NoteOff(960, 0, 69)).ToArray();
        using MidiDecoder a = Open(still);
        using MidiDecoder b = Open(wheel);
        float[] x = ReadAll(a);
        float[] y = ReadAll(b);
        double drift = 0;
        for (int i = Rate / 4; i < Rate; i++) drift = Math.Max(drift, Math.Abs(x[i * 2] - y[i * 2]));
        Assert.That(drift, Is.GreaterThan(0.01));
    }

    [Test]
    public void Read_DifferentBlockSizesGiveTheSameSamples()
    {
        byte[] data = new MidiBuilder().Track(t => t.Program(0, 0, 48).Controller(0, 0, 91, 80).Controller(0, 0, 93, 60)
            .NoteOn(0, 0, 60, 100).NoteOn(0, 9, 38, 100).NoteOff(333, 0, 60).NoteOn(500, 0, 64, 90).NoteOff(900, 0, 64)).ToArray();
        float[] reference = ReadAll(Open(data));
        foreach (int block in new[] { 1, 63, 64, 65, 1000 })
        {
            using MidiDecoder decoder = Open(data);
            var pieces = new List<float>();
            var buffer = new float[2 * block];
            int got;
            while ((got = decoder.Read(buffer, block)) > 0) pieces.AddRange(buffer.AsSpan(0, got * 2).ToArray());
            Assert.That(pieces, Is.EqualTo(reference), $"block {block}");
        }
    }
}
