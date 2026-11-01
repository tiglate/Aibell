using static Aibell.Audio.Midi.Tests.SoundFontBuilder;

namespace Aibell.Audio.Midi.Tests;

[TestFixture]
public class SoundFontTests
{
    private const int Rate = 44100;

    private static SoundFont Load(byte[] data) => SoundFont.Open(new MemoryStream(data));

    private static float[] Play(byte[] smf, SoundFont? font, int seconds = 0)
    {
        using MidiDecoder decoder = MidiDecoder.Open(new MemoryStream(smf), Rate, font);
        var all = new List<float>();
        var buffer = new float[1024 * 2];
        int got;
        while ((got = decoder.Read(buffer, 1024)) > 0 && (seconds == 0 || all.Count < seconds * Rate * 2)) all.AddRange(buffer.AsSpan(0, got * 2).ToArray());
        return [.. all];
    }

    private static float Peak(float[] pcm, double from, double to)
    {
        float peak = 0;
        for (int i = (int)(from * Rate); i < (int)(to * Rate) && i * 2 < pcm.Length; i++) peak = Math.Max(peak, Math.Abs(pcm[i * 2]));
        return peak;
    }

    private static double Hertz(float[] pcm, double from, double to)
    {
        int a = (int)(from * Rate), b = (int)(to * Rate), crossings = 0;
        for (int i = a + 1; i < b; i++) if (pcm[(i - 1) * 2] < 0 && pcm[i * 2] >= 0) crossings++;
        return crossings / (to - from);
    }

    private static byte[] Song(Action<MidiBuilder.TrackWriter> track) => new MidiBuilder().Track(track).ToArray();

    /// <summary>A bank with one preset (program 0, bank 0) that plays a 441 Hz looped sine at key 60 with the given instrument generators.</summary>
    private static byte[] OneSine(params (int Op, int Amount)[] instrumentGens) => OneSine(null, instrumentGens);

    private static byte[] OneSine(Action<SoundFontBuilder>? extra, params (int Op, int Amount)[] instrumentGens)
    {
        var b = new SoundFontBuilder();
        int sample = b.AddSine(100);
        // sampleModes 1: loop; a short volume release so that notes end promptly; no reverb
        (int, int)[] gens = [(54, 1), (38, -7973), .. instrumentGens, (SampleId, sample)];
        int inst = b.AddInstrument("sine", new ZoneSpec([.. gens.Take(gens.Length - 1)], sample));
        b.AddPreset("Sine", 0, 0, ZoneSpec.Of(inst));
        extra?.Invoke(b);
        return b.ToArray();
    }

    [Test]
    public void Open_ReadsPresets()
    {
        SoundFont font = Load(OneSine());
        Assert.That(font.Name, Is.EqualTo("Test bank"));
        Assert.That(font.PresetCount, Is.EqualTo(1));
    }

    [Test]
    public void Read_PlaysTheSampleAtTheRightPitch()
    {
        SoundFont font = Load(OneSine());
        byte[] low = Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 110).NoteOff(960, 0, 60));
        byte[] high = Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 72, 110).NoteOff(960, 0, 72));
        Assert.That(Hertz(Play(low, font), 0.2, 0.9), Is.EqualTo(441).Within(4));
        Assert.That(Hertz(Play(high, font), 0.2, 0.9), Is.EqualTo(882).Within(8));
    }

    [Test]
    public void Read_ALoopKeepsTheNoteSoundingBeyondTheSample()
    {
        // The sample is 60 cycles (6000 points, 0.136 s); the note is held for two seconds.
        SoundFont font = Load(OneSine());
        float[] pcm = Play(Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 110).NoteOff(1920, 0, 60)), font);
        Assert.That(Peak(pcm, 1.5, 1.9), Is.GreaterThan(0.05f));
        Assert.That(Peak(pcm, 2.3, 2.6), Is.LessThan(1e-3f));
    }

    [Test]
    public void Read_WithoutALoopTheSampleEnds()
    {
        SoundFont font = Load(OneSine((54, 0)));
        float[] pcm = Play(Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 110).NoteOff(1920, 0, 60)), font);
        Assert.That(Peak(pcm, 0.02, 0.1), Is.GreaterThan(0.05f));
        Assert.That(Peak(pcm, 0.3, 1.5), Is.LessThan(1e-4f));
    }

    [Test]
    public void Preset_GeneratorsAddToTheInstrumentOnes()
    {
        var b = new SoundFontBuilder();
        int sample = b.AddSine(100);
        int inst = b.AddInstrument("i", new ZoneSpec([(54, 1), (38, -7973)], sample));
        // The preset zone raises the pitch by an octave (coarse tune +12 semitones).
        b.AddPreset("Up", 0, 0, ZoneSpec.Of(inst, (51, 12)));
        float[] pcm = Play(Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 110).NoteOff(960, 0, 60)), Load(b.ToArray()));
        Assert.That(Hertz(pcm, 0.2, 0.9), Is.EqualTo(882).Within(8));
    }

    [Test]
    public void Preset_InstrumentGeneratorsAreIgnoredAtThePresetLevel()
    {
        var b = new SoundFontBuilder();
        int sample = b.AddSine(100);
        int inst = b.AddInstrument("i", new ZoneSpec([(54, 1), (38, -7973)], sample));
        // sampleModes (54) and overridingRootKey (58) are instrument-only; at the preset level they must not change anything.
        b.AddPreset("P", 0, 0, ZoneSpec.Of(inst, (54, 0), (58, 48)));
        float[] pcm = Play(Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 110).NoteOff(960, 0, 60)), Load(b.ToArray()));
        Assert.That(Hertz(pcm, 0.2, 0.9), Is.EqualTo(441).Within(4));
    }

    [Test]
    public void Zones_AreChosenByKeyAndVelocityRange()
    {
        var b = new SoundFontBuilder();
        int a = b.AddSine(100), c = b.AddSine(50);
        int inst = b.AddInstrument("split",
            new ZoneSpec([Range(KeyRange, 0, 59), (54, 1), (38, -7973)], a),
            new ZoneSpec([Range(KeyRange, 60, 127), Range(VelRange, 0, 63), (54, 1), (38, -7973)], c),
            new ZoneSpec([Range(KeyRange, 60, 127), Range(VelRange, 64, 127), (54, 1), (38, -7973), (51, -12)], c));
        b.AddPreset("Split", 0, 0, ZoneSpec.Of(inst));
        SoundFont font = Load(b.ToArray());
        // Key 48 uses the first zone (441 Hz at root 60, an octave down: 220), key 60 soft the second (882), key 60 loud the third (441).
        Assert.That(Hertz(Play(Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 48, 100).NoteOff(960, 0, 48)), font), 0.2, 0.9), Is.EqualTo(220).Within(4));
        Assert.That(Hertz(Play(Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 40).NoteOff(960, 0, 60)), font), 0.2, 0.9), Is.EqualTo(882).Within(8));
        Assert.That(Hertz(Play(Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 100).NoteOff(960, 0, 60)), font), 0.2, 0.9), Is.EqualTo(441).Within(4));
    }

    [Test]
    public void Zones_GlobalZoneSuppliesDefaults()
    {
        var b = new SoundFontBuilder();
        int sample = b.AddSine(100);
        // The global zone sets the loop mode and release; the local zone has only the sample.
        int inst = b.AddInstrument("g", ZoneSpec.Global((54, 1), (38, -7973)), ZoneSpec.Of(sample));
        b.AddPreset("G", 0, 0, ZoneSpec.Of(inst));
        float[] pcm = Play(Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 110).NoteOff(1920, 0, 60)), Load(b.ToArray()));
        Assert.That(Peak(pcm, 1.5, 1.9), Is.GreaterThan(0.05f));
    }

    [Test]
    public void Volume_ControllersAttenuateThroughTheDefaultModulators()
    {
        SoundFont font = Load(OneSine());
        float loud = Peak(Play(Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 127).NoteOff(480, 0, 60)), font), 0.1, 0.4);
        float quiet = Peak(Play(Song(t => t.Controller(0, 0, 91, 0).Controller(0, 0, 7, 40).NoteOn(0, 0, 60, 127).NoteOff(480, 0, 60)), font), 0.1, 0.4);
        float soft = Peak(Play(Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 30).NoteOff(480, 0, 60)), font), 0.1, 0.4);
        Assert.That(quiet, Is.LessThan(loud * 0.3f));
        Assert.That(soft, Is.LessThan(loud * 0.3f));
        Assert.That(loud, Is.GreaterThan(0.05f));
    }

    [Test]
    public void Pan_ControllerMovesTheSoundThroughTheDefaultModulator()
    {
        SoundFont font = Load(OneSine());
        float[] pcm = Play(Song(t => t.Controller(0, 0, 91, 0).Controller(0, 0, 10, 0).NoteOn(0, 0, 60, 110).NoteOff(480, 0, 60)), font);
        float left = 0, right = 0;
        for (int i = Rate / 10; i < Rate / 3; i++) { left = Math.Max(left, Math.Abs(pcm[i * 2])); right = Math.Max(right, Math.Abs(pcm[i * 2 + 1])); }
        Assert.That(left, Is.GreaterThan(0.05f));
        Assert.That(right, Is.LessThan(left * 0.05f));
    }

    [Test]
    public void Pitch_WheelBendsBySensitivity()
    {
        SoundFont font = Load(OneSine());
        float[] pcm = Play(Song(t => t.Controller(0, 0, 91, 0).Bend(0, 0, 16383).NoteOn(0, 0, 60, 110).NoteOff(960, 0, 60)), font);
        // Two semitones up at full bend: 441 * 2^(2/12) = 495, less the specification's 127/128 factor.
        Assert.That(Hertz(pcm, 0.2, 0.9), Is.EqualTo(495).Within(8));
    }

    [Test]
    public void Percussion_UsesBank128OnChannelTen()
    {
        var b = new SoundFontBuilder();
        int sample = b.AddSine(50);
        int inst = b.AddInstrument("kit", new ZoneSpec([Range(KeyRange, 36, 36), (38, -7973), (54, 1)], sample));
        b.AddPreset("Kit", 0, 128, ZoneSpec.Of(inst));
        SoundFont font = Load(b.ToArray());
        float[] pcm = Play(Song(t => t.Controller(0, 9, 91, 0).NoteOn(0, 9, 36, 110).NoteOff(480, 9, 36)), font);
        Assert.That(Peak(pcm, 0.05, 0.3), Is.GreaterThan(0.05f));
        // The drum key 36 sits an octave and a half below the root key 60... the 882 Hz sine played 2 octaves down is 220 Hz.
        Assert.That(Hertz(pcm, 0.1, 0.4), Is.EqualTo(220).Within(5));
    }

    [Test]
    public void ExclusiveClass_ACloseHatStopsTheOpenOne()
    {
        var b = new SoundFontBuilder();
        int sample = b.AddSine(100);
        int inst = b.AddInstrument("hats",
            new ZoneSpec([Range(KeyRange, 42, 42), (54, 1), (38, -7973), (57, 1)], sample),
            new ZoneSpec([Range(KeyRange, 46, 46), (54, 1), (38, -7973), (57, 1)], sample));
        b.AddPreset("Hats", 0, 128, ZoneSpec.Of(inst));
        SoundFont font = Load(b.ToArray());
        float[] open = Play(Song(t => t.Controller(0, 9, 91, 0).NoteOn(0, 9, 46, 100).End(1920)), font);
        float[] choked = Play(Song(t => t.Controller(0, 9, 91, 0).NoteOn(0, 9, 46, 100).NoteOn(240, 9, 42, 100).NoteOff(480, 9, 42).End(1920)), font);
        Assert.That(Peak(open, 1.0, 1.5), Is.GreaterThan(0.05f));
        Assert.That(Peak(choked, 1.0, 1.5), Is.LessThan(1e-3f));
    }

    [Test]
    public void MissingPreset_FallsBackToTheBuiltInInstruments()
    {
        SoundFont font = Load(OneSine());
        // Program 5 does not exist in the bank (and neither does bank 0 program 5): the note still sounds.
        float[] pcm = Play(Song(t => t.Program(0, 0, 5).Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 110).NoteOff(480, 0, 60)), font);
        Assert.That(Peak(pcm, 0.1, 0.4), Is.GreaterThan(0.01f));
    }

    [Test]
    public void BankSelect_ChoosesThePresetOfThatBank_AndFallsBackToBankZero()
    {
        var b = new SoundFontBuilder();
        int a = b.AddSine(100), c = b.AddSine(50);
        int i1 = b.AddInstrument("a", new ZoneSpec([(54, 1), (38, -7973)], a));
        int i2 = b.AddInstrument("c", new ZoneSpec([(54, 1), (38, -7973)], c));
        b.AddPreset("A", 3, 0, ZoneSpec.Of(i1));
        b.AddPreset("C", 3, 5, ZoneSpec.Of(i2));
        SoundFont font = Load(b.ToArray());
        double Tone(int bank) => Hertz(Play(Song(t => t.Controller(0, 0, 0, bank).Controller(0, 0, 91, 0).Program(0, 0, 3).NoteOn(0, 0, 60, 100).NoteOff(960, 0, 60)), font), 0.2, 0.9);
        Assert.That(Tone(5), Is.EqualTo(882).Within(8));
        Assert.That(Tone(0), Is.EqualTo(441).Within(4));
        Assert.That(Tone(9), Is.EqualTo(441).Within(4)); // no such bank: the same program in bank 0
    }

    [Test]
    public void Modulators_InTheFileAreApplied()
    {
        // A modulator in the instrument zone: CC 21 (unipolar linear) adds up to 1200 cents of fine pitch (an octave) at full scale.
        var b = new SoundFontBuilder();
        int sample = b.AddSine(100);
        var zone = new ZoneSpec([(54, 1), (38, -7973)], sample, [(0x0080 | 21, 52, 1200, 0, 0)]);
        int inst = b.AddInstrument("m", zone);
        b.AddPreset("M", 0, 0, ZoneSpec.Of(inst));
        float[] pcm = Play(Song(t => t.Controller(0, 0, 91, 0).Controller(0, 0, 21, 127).NoteOn(0, 0, 60, 110).NoteOff(960, 0, 60)), Load(b.ToArray()));
        // 127/128 of an octave
        Assert.That(Hertz(pcm, 0.2, 0.9), Is.EqualTo(441 * Math.Pow(2, 127 / 128.0)).Within(10));
    }

    [Test]
    public void Samples_With24Bits_AreReadWhenTheBankIs204()
    {
        SoundFont wide = Load(new SoundFontBuilder { With24Bit = true }.Also(b => b.AddSine(100)).ToArray());
        SoundFont narrow = Load(new SoundFontBuilder { With24Bit = true, Minor = 1 }.Also(b => b.AddSine(100)).ToArray());
        // With sm24 each point carries 0x40 / 256 of a 16-bit step extra; an older bank ignores the chunk.
        Assert.That(wide.At(1) - narrow.At(1), Is.EqualTo(0x40 / 8388608f).Within(1e-9f));
    }

    [Test]
    public void Read_IsIdenticalForAnyBlockSizeAndAfterSeek()
    {
        SoundFont font = Load(OneSine((8, 6000), (9, 200)));
        byte[] smf = Song(t => t.NoteOn(0, 0, 60, 100).NoteOn(100, 0, 67, 90).NoteOff(500, 0, 60).NoteOff(700, 0, 67));
        float[] reference = Play(smf, font);
        foreach (int block in new[] { 1, 63, 64, 777 })
        {
            using MidiDecoder decoder = MidiDecoder.Open(new MemoryStream(smf), Rate, font);
            var pieces = new List<float>();
            var buffer = new float[2 * block];
            int got;
            while ((got = decoder.Read(buffer, block)) > 0) pieces.AddRange(buffer.AsSpan(0, got * 2).ToArray());
            Assert.That(pieces, Is.EqualTo(reference), $"block {block}");
        }
    }

    [Test]
    public void Read_AllocatesNothingOnceOpen()
    {
        SoundFont font = Load(OneSine((8, 6000), (9, 200)));
        byte[] smf = Song(t => { for (int i = 0; i < 20; i++) t.NoteOn(i * 20, 0, 48 + i, 100).NoteOff(i * 20 + 300, 0, 48 + i); });
        using MidiDecoder decoder = MidiDecoder.Open(new MemoryStream(smf), Rate, font);
        var buffer = new float[2 * 4096];
        decoder.Read(buffer, 4096);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) decoder.Read(buffer, 4096);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0));
    }

    // ---- damaged and unusual files ----

    [Test]
    public void Open_RejectsNonSoundFonts()
    {
        Assert.Throws<InvalidDataException>(() => Load([]));
        Assert.Throws<InvalidDataException>(() => Load([.. "RIFF"u8.ToArray(), 4, 0, 0, 0, .. "WAVE"u8.ToArray()]));
        Assert.Throws<InvalidDataException>(() => Load(new byte[100]));
    }

    [Test]
    public void Open_RejectsVersionThree_AndStructuralDamage()
    {
        Assert.Throws<NotSupportedException>(() => Load(new SoundFontBuilder { Major = 3 }.Also(b => b.AddSine(100)).ToArray()));
        byte[] good = OneSine();
        Assert.Throws<InvalidDataException>(() => Load(good.AsSpan(0, good.Length / 2).ToArray()));
        // A preset bag index beyond the bag list, a chunk of the wrong record size, a missing chunk.
        Assert.Throws<InvalidDataException>(() => Load(Build(h => h["phdr"][24] = 77)));
        Assert.Throws<InvalidDataException>(() => Load(Build(h => h["igen"] = [.. h["igen"], 1])));
        Assert.Throws<InvalidDataException>(() => Load(Build(h => h.Remove("shdr"))));
    }

    private static byte[] Build(Action<Dictionary<string, byte[]>> damage)
    {
        var b = new SoundFontBuilder();
        int sample = b.AddSine(100);
        int inst = b.AddInstrument("i", new ZoneSpec([(54, 1)], sample));
        b.AddPreset("P", 0, 0, ZoneSpec.Of(inst));
        return b.ToArray(damage);
    }

    [Test]
    public void Samples_OutsideThePoolAreSkippedNotFatal()
    {
        var b = new SoundFontBuilder();
        int good = b.AddSine(100);
        int bad = b.AddRawSample(new short[10], 44100, 60, 0, 5, type: 0x8001); // ROM sample
        int inst = b.AddInstrument("i", new ZoneSpec([(54, 1)], good), new ZoneSpec([(54, 1)], bad));
        b.AddPreset("P", 0, 0, ZoneSpec.Of(inst));
        Assert.That(Peak(Play(Song(t => t.Controller(0, 0, 91, 0).NoteOn(0, 0, 60, 100).NoteOff(240, 0, 60)), Load(b.ToArray())), 0.05, 0.2), Is.GreaterThan(0.02f));
    }

    [Test]
    public void Open_SurvivesRandomDamage()
    {
        byte[] good = OneSine((8, 6000), (9, 200));
        byte[] smf = Song(t => t.NoteOn(0, 0, 60, 100).NoteOff(240, 0, 60));
        var random = new Random(11);
        for (int round = 0; round < 400; round++)
        {
            byte[] bad = (byte[])good.Clone();
            for (int k = 0; k < 6; k++)
            {
                // Prefer the structure at the end of the file (the hydra) over the sample data.
                int at = random.Next(2) == 0 ? bad.Length - 1 - random.Next(Math.Min(bad.Length, 600)) : random.Next(bad.Length);
                bad[at] = (byte)random.Next(256);
            }
            try
            {
                SoundFont font = Load(bad);
                Play(smf, font, seconds: 3);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
            }
        }
    }
}

internal static class BuilderExtensions
{
    public static SoundFontBuilder Also(this SoundFontBuilder builder, Action<SoundFontBuilder> action)
    {
        action(builder);
        int sample = 0;
        int inst = builder.AddInstrument("i", new SoundFontBuilder.ZoneSpec([(54, 1)], sample));
        builder.AddPreset("P", 0, 0, SoundFontBuilder.ZoneSpec.Of(inst));
        return builder;
    }
}
