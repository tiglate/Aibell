namespace Aibell.Audio.Spc.Tests;

[TestFixture]
public class SpcDspTests
{
    private static (SpcDsp Dsp, byte[] Ram) NewDsp()
    {
        var ram = new byte[0x10000];
        var dsp = new SpcDsp(ram);
        dsp.Write(0x0C, 0x7F);
        dsp.Write(0x1C, 0x7F);
        dsp.Write(0x5D, 0x0F);          // directory at 0F00
        dsp.Write(0x6C, 0x20);          // unmuted, echo writes off
        return (dsp, ram);
    }

    private static void SetVoice(SpcDsp dsp, int voice, int pitch = 0x1000, int volume = 0x7F, int adsr1 = 0, int adsr2 = 0, int gain = 0x7F, int source = 0)
    {
        int b = voice * 16;
        dsp.Write(b, (byte)volume);
        dsp.Write(b + 1, (byte)volume);
        dsp.Write(b + 2, (byte)pitch);
        dsp.Write(b + 3, (byte)(pitch >> 8));
        dsp.Write(b + 4, (byte)source);
        dsp.Write(b + 5, (byte)adsr1);
        dsp.Write(b + 6, (byte)adsr2);
        dsp.Write(b + 7, (byte)gain);
    }

    private static void Sample(byte[] ram, byte[] brr, int address = 0x1000, int slot = 0)
    {
        brr.CopyTo(ram, address);
        ram[0x0F00 + slot * 4] = (byte)address;
        ram[0x0F01 + slot * 4] = (byte)(address >> 8);
        ram[0x0F02 + slot * 4] = (byte)address;
        ram[0x0F03 + slot * 4] = (byte)(address >> 8);
    }

    private static (int[] Left, int[] Right) Render(SpcDsp dsp, int frames)
    {
        var left = new int[frames];
        var right = new int[frames];
        for (int i = 0; i < frames; i++) dsp.Step(out left[i], out right[i]);
        return (left, right);
    }

    [Test]
    public void GaussTable_GroupsSumToTheDocumentedRange()
    {
        short[] g = GaussTable.Values;

        Assert.Multiple(() =>
        {
            Assert.That(g, Has.Length.EqualTo(512));
            for (int i = 0; i < 256; i++) Assert.That(g[i] + g[0xFF - i] + g[0x100 + i] + g[0x1FF - i], Is.InRange(0x7FF, 0x801));
        });
    }

    [Test]
    public void Voice_StaysSilentUntilKeyedOn()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0);

        (int[] left, _) = Render(dsp, 200);

        Assert.That(left, Is.All.EqualTo(0));
    }

    [Test]
    public void KeyOn_PlaysTheLoopedSquareWaveAtItsPitch()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0);
        dsp.Write(0x4C, 1);

        (int[] left, int[] right) = Render(dsp, 400);

        Assert.Multiple(() =>
        {
            // One sample per output frame: the 16-sample block repeats every 16 frames.
            for (int i = 100; i < 300; i++) Assert.That(left[i + 16], Is.EqualTo(left[i]), $"frame {i}");
            Assert.That(left.Max(), Is.InRange(20000, 32767));
            Assert.That(left.Min(), Is.LessThan(-8000));
            Assert.That(right, Is.EqualTo(left));
        });
    }

    [Test]
    public void Pitch_HalvingDoublesThePeriod()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0, pitch: 0x0800);
        dsp.Write(0x4C, 1);

        (int[] left, _) = Render(dsp, 600);

        for (int i = 200; i < 400; i++) Assert.That(left[i + 32], Is.EqualTo(left[i]), $"frame {i}");
    }

    [Test]
    public void Volume_PansAndInvertsEachChannel()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0);
        dsp.Write(0x00, 0x7F);
        dsp.Write(0x01, 0x81);          // -127
        dsp.Write(0x4C, 1);

        (int[] left, int[] right) = Render(dsp, 200);

        for (int i = 100; i < 200; i++) Assert.That(right[i], Is.InRange(-left[i] - 2, -left[i] + 2));
    }

    [Test]
    public void MasterVolumeAndMute_ScaleTheOutput()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0);
        dsp.Write(0x4C, 1);
        (int[] full, _) = Render(dsp, 200);

        (SpcDsp half, byte[] ram2) = NewDsp();
        Sample(ram2, SpcBuilder.SquareLoop());
        SetVoice(half, 0);
        half.Write(0x0C, 0x40);
        half.Write(0x4C, 1);
        (int[] halved, _) = Render(half, 200);

        (SpcDsp muted, byte[] ram3) = NewDsp();
        Sample(ram3, SpcBuilder.SquareLoop());
        SetVoice(muted, 0);
        muted.Write(0x6C, 0x60);
        muted.Write(0x4C, 1);
        (int[] silent, _) = Render(muted, 200);

        Assert.Multiple(() =>
        {
            Assert.That(halved.Max(), Is.InRange(full.Max() * 0.47, full.Max() * 0.53));
            Assert.That(silent, Is.All.EqualTo(0));
        });
    }

    [Test]
    public void BlockWithEndFlag_SetsEndxAndLoops()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0);
        dsp.Write(0x4C, 1);

        Render(dsp, 60);

        Assert.That(dsp.Read(0x7C) & 1, Is.EqualTo(1));
        dsp.Write(0x7C, 0xFF);
        Assert.That(dsp.Read(0x7C), Is.EqualTo(0), "any write clears ENDX");
    }

    [Test]
    public void BlockWithEndAndMute_ReleasesTheVoice()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.Block(12, 0, 1, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7));
        SetVoice(dsp, 0);
        dsp.Write(0x4C, 1);

        (int[] left, _) = Render(dsp, 100);

        Assert.Multiple(() =>
        {
            Assert.That(left.Take(30).Max(), Is.GreaterThan(1000));
            Assert.That(left.Skip(60), Is.All.EqualTo(0));
            Assert.That(dsp.Read(0x08), Is.EqualTo(0), "ENVX");
        });
    }

    [Test]
    public void KeyOff_FadesTheEnvelopeToZero()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0);
        dsp.Write(0x4C, 1);
        Render(dsp, 100);
        Assert.That(dsp.Read(0x08), Is.GreaterThan(100));

        dsp.Write(0x5C, 1);
        (int[] left, _) = Render(dsp, 400);

        Assert.That(left.Skip(300), Is.All.EqualTo(0));
    }

    [Test]
    public void Attack_RaisesTheEnvelopeAtTheSelectedRate()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0, adsr1: 0x8F, adsr2: 0xE0);     // attack rate 31 (+1024 per sample), sustain level 7
        dsp.Write(0x4C, 1);

        Render(dsp, 20);

        Assert.That(dsp.Read(0x08), Is.GreaterThan(100), "the envelope reaches its top within a few samples");
    }

    [Test]
    public void SlowAttack_TakesThousandsOfSamplesPerStep()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0, adsr1: 0x80, adsr2: 0xE0);     // attack rate 1: +32 every 2048 samples
        dsp.Write(0x4C, 1);

        Render(dsp, 4200);

        Assert.That(dsp.Read(0x08) * 16, Is.InRange(64, 128));
    }

    [Test]
    public void Decay_FallsToTheSustainLevel()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0, adsr1: 0xFF, adsr2: 0x40);     // fastest attack, decay 7, sustain level 2 (boundary 0x300), sustain rate 0
        dsp.Write(0x4C, 1);

        Render(dsp, 2000);

        // Sustain rate 0 never steps, so the envelope parks at the first value at or below the boundary.
        Assert.That(dsp.Read(0x08) * 16, Is.InRange(0x2C0, 0x300));
    }

    [Test]
    public void DirectGain_SetsTheEnvelopeFromTheRegister()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0, gain: 0x40);                   // 0x40 * 16 = 0x400
        dsp.Write(0x4C, 1);

        Render(dsp, 50);

        Assert.That(dsp.Read(0x08), Is.EqualTo(0x40));
    }

    [Test]
    public void LinearGainIncrease_ClimbsAndStopsAtTheTop()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0, gain: 0xDF);                   // linear increase, rate 31
        dsp.Write(0x4C, 1);

        Render(dsp, 30);
        int partway = dsp.Read(0x08);
        Render(dsp, 400);

        Assert.Multiple(() =>
        {
            Assert.That(partway, Is.InRange(1, 126));
            Assert.That(dsp.Read(0x08), Is.EqualTo(0x7F));
        });
    }

    [Test]
    public void Filter1_DecaysByFifteenSixteenthsPerSample()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        // An impulse as the last sample of a block, then a block that only has the filter running on that history.
        byte[] impulse = SpcBuilder.Block(12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7);
        byte[] decay = SpcBuilder.Block(0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        impulse.CopyTo(ram, 0x1000);
        decay.CopyTo(ram, 0x1009);
        ram[0x0F00] = 0x00; ram[0x0F01] = 0x10; ram[0x0F02] = 0x09; ram[0x0F03] = 0x10;
        SetVoice(dsp, 0);
        dsp.Write(0x4C, 1);

        (int[] left, _) = Render(dsp, 80);

        int peak = Array.IndexOf(left, left.Max());
        Assert.That(peak, Is.InRange(8, 25));
        // Past the interpolation window the tail is a pure geometric decay.
        double ratio = (double)left[peak + 10] / left[peak + 6];
        Assert.That(ratio, Is.EqualTo(Math.Pow(15.0 / 16, 4)).Within(0.05));
    }

    [Test]
    public void Noise_ReplacesTheSampleWithPseudoRandomValues()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0);
        dsp.Write(0x3D, 1);
        dsp.Write(0x6C, 0x20 | 0x1F);   // noise at 32 kHz
        dsp.Write(0x4C, 1);

        (int[] left, _) = Render(dsp, 600);

        Assert.Multiple(() =>
        {
            Assert.That(left.Skip(100).Distinct().Count(), Is.GreaterThan(100));
            Assert.That(left.Max(), Is.InRange(16000, 32767));
            Assert.That(left.Min(), Is.InRange(-32768, -16000));
        });
    }

    [Test]
    public void PitchModulation_ChangesTheCarrierPeriod()
    {
        // Voice 0 runs a square wave at zero output volume; voice 1 is modulated by it.
        (SpcDsp plain, byte[] ramA) = NewDsp();
        Sample(ramA, SpcBuilder.SquareLoop());
        SetVoice(plain, 0, volume: 0);
        SetVoice(plain, 1);
        plain.Write(0x4C, 3);
        (int[] a, _) = Render(plain, 600);

        (SpcDsp modulated, byte[] ramB) = NewDsp();
        Sample(ramB, SpcBuilder.SquareLoop());
        SetVoice(modulated, 0, volume: 0);
        SetVoice(modulated, 1);
        modulated.Write(0x2D, 0x02);
        modulated.Write(0x4C, 3);
        (int[] b, _) = Render(modulated, 600);

        Assert.That(b.Skip(100), Is.Not.EqualTo(a.Skip(100)));
    }

    [Test]
    public void Echo_ReturnsTheVoiceAfterTheDelay()
    {
        (SpcDsp dsp, byte[] ram) = NewDsp();
        Sample(ram, SpcBuilder.SquareLoop());
        SetVoice(dsp, 0);
        dsp.Write(0x0C, 0);             // only the echo reaches the output
        dsp.Write(0x1C, 0);
        dsp.Write(0x2C, 0x7F);
        dsp.Write(0x3C, 0x7F);
        dsp.Write(0x4D, 1);             // voice 0 feeds the echo
        dsp.Write(0x6D, 0x20);          // buffer at 2000
        dsp.Write(0x7D, 1);             // 512 entries = 512 frames of delay
        dsp.Write(0x7F, 0x7F);          // newest FIR tap passes the signal
        dsp.Write(0x6C, 0x00);          // echo writes on
        dsp.Write(0x4C, 1);

        (int[] left, _) = Render(dsp, 1400);

        Assert.Multiple(() =>
        {
            Assert.That(left.Take(500), Is.All.EqualTo(0));
            Assert.That(left.Skip(520).Take(300).Max(), Is.GreaterThan(3000));
        });
    }
}
