namespace Aibell.Audio.Midi;

/// <summary>
/// A General MIDI style synthesizer with no sound bank: 16 channels, 64 pitched voices and 24 drum voices, an effects bus (reverb and chorus sends,
/// CC 91 and 93) and a smooth peak limiter. It handles notes, program change, bank select (stored), modulation, volume, expression, pan, sustain pedal,
/// pitch bend with its range (RPN 0), all notes/sound off and reset controllers. Deterministic: noise generators belong to voices and every voice
/// parameter changes only at fixed positions of the timeline, so the same file renders to the same samples however the reads are split. Allocation-free
/// once built.
/// </summary>
internal sealed class MidiSynth
{
    /// <summary>Frames between refreshes of voice parameters.</summary>
    public const int ControlFrames = 64;

    private const int TonePolyphony = 64;
    private const int DrumPolyphony = 24;
    private const int SamplePolyphony = 96;
    private const float VoiceGain = .2f;

    private readonly ToneVoice[] tones = new ToneVoice[TonePolyphony];
    private readonly DrumVoice[] drums = new DrumVoice[DrumPolyphony];
    private readonly SampleVoice[] samples = new SampleVoice[SamplePolyphony];
    private readonly Voice[] all = new Voice[TonePolyphony + DrumPolyphony + SamplePolyphony];
    private readonly SoundFont? bank;
    private readonly ChannelState[] channels = new ChannelState[16];
    private readonly float[] reverbBus = new float[ControlFrames];
    private readonly float[] chorusBus = new float[ControlFrames];
    private readonly Reverb reverb;
    private readonly Chorus chorus;
    private long clock;

    public MidiSynth(int sampleRate, SoundFont? bank = null)
    {
        SampleRate = sampleRate;
        this.bank = bank;
        Tables = WaveTables.For(sampleRate);
        Runtime = [.. Timbres.Families.Select((t, i) => new TimbreRuntime(i, t, sampleRate))];
        for (int i = 0; i < tones.Length; i++) all[i] = tones[i] = new ToneVoice();
        for (int i = 0; i < drums.Length; i++) all[TonePolyphony + i] = drums[i] = new DrumVoice();
        for (int i = 0; i < samples.Length; i++) all[TonePolyphony + DrumPolyphony + i] = samples[i] = new SampleVoice();
        for (int i = 0; i < channels.Length; i++) channels[i] = new ChannelState();
        reverb = new Reverb(sampleRate);
        chorus = new Chorus(sampleRate);
        Reset();
    }

    public int SampleRate { get; }

    internal float[][][] Tables { get; }

    internal TimbreRuntime[] Runtime { get; }

    public void Reset()
    {
        foreach (Voice v in all) v.Stage = Stage.Off;
        foreach (ChannelState c in channels) c.Reset();
        reverb.Clear();
        chorus.Clear();
        clock = 0;
    }

    /// <summary>Applies one message. With <paramref name="sound"/> false, note messages are ignored (used to restore controller state after a seek).</summary>
    public void Handle(in TimedEvent e, bool sound = true)
    {
        if (e.Status == 0xFF)
        {
            Reset();
            return;
        }
        int ch = e.Status & 0x0F;
        ChannelState channel = channels[ch];
        switch (e.Status & 0xF0)
        {
            case 0x80:
                if (sound) NoteOff(ch, e.Data1);
                break;
            case 0x90:
                if (!sound) break;
                if (e.Data2 == 0) NoteOff(ch, e.Data1);
                else NoteOn(ch, e.Data1, e.Data2);
                break;
            case 0xB0:
                Controller(ch, channel, e.Data1, e.Data2, sound);
                break;
            case 0xC0:
                channel.Program = e.Data1;
                break;
            case 0xE0:
                channel.Bend = ((e.Data2 << 7 | e.Data1) - 8192) / 8192f;
                break;
        }
    }

    /// <summary>Renders <paramref name="frames"/> stereo frames (overwriting the buffer).</summary>
    public void Render(Span<float> destination, int frames)
    {
        int offset = 0;
        while (offset < frames)
        {
            int phase = (int)(clock % ControlFrames);
            if (phase == 0) ControlTick();
            int count = Math.Min(frames - offset, ControlFrames - phase);
            Span<float> output = destination.Slice(offset * 2, count * 2);
            output.Clear();
            reverbBus.AsSpan(0, count).Clear();
            chorusBus.AsSpan(0, count).Clear();
            foreach (Voice v in all)
            {
                if (v.Stage != Stage.Off) v.Render(output, reverbBus, chorusBus, count);
            }
            chorus.Process(chorusBus, output, count);
            reverb.Process(reverbBus, output, count);
            for (int i = 0; i < output.Length; i++) output[i] = Limit(output[i]);
            offset += count;
            clock += count;
        }
    }

    internal float Coefficient(float tau) => MathF.Exp(-1f / (MathF.Max(tau, 1e-4f) * SampleRate));

    /// <summary>Left and right amplitude (constant-power pan; volume and expression follow a squared curve like GM synths do) and the reverb and chorus send amplitudes.</summary>
    internal static (float Left, float Right, float Reverb, float Chorus) Mix(Voice v, ChannelState channel)
    {
        float volume = channel.Volume / 127f * (channel.Expression / 127f);
        float amplitude = VoiceGain * v.Velocity * v.Velocity * volume * volume;
        float angle = (Math.Clamp(channel.Pan, 1, 127) - 1) / 126f * (MathF.PI / 2);
        return (amplitude * MathF.Cos(angle), amplitude * MathF.Sin(angle), amplitude * channel.ReverbSend / 127f, amplitude * channel.ChorusSend / 127f);
    }

    private void ControlTick()
    {
        foreach (Voice v in all)
        {
            if (v.Stage != Stage.Off) v.Control(this, channels[v.Channel]);
        }
    }

    private void NoteOn(int ch, int note, int velocity)
    {
        NoteOff(ch, note);
        ChannelState channel = channels[ch];
        if (bank is not null && StartSamples(ch, channel, note, velocity)) return;
        Voice v = ch == 9 ? Allocate(drums) : Allocate(tones);
        v.Channel = ch;
        v.Held = false;
        v.Age = clock;
        v.Start(this, channel, note, velocity / 127f);
    }

    // Plays the note from the SoundFont preset of the channel (bank select and program change; channel 10 uses the percussion bank 128).
    // False when the bank has nothing for it, so the built-in instruments take over.
    private bool StartSamples(int ch, ChannelState channel, int note, int velocity)
    {
        bool percussion = ch == 9;
        SoundFont.Preset? preset = bank!.FindPreset(percussion ? 128 : channel.BankMsb, channel.Program, percussion);
        if (preset is null) return false;
        bool started = false;
        foreach (SoundFont.SampleZone zone in preset.Zones)
        {
            if (note < zone.KeyLo || note > zone.KeyHi || velocity < zone.VelLo || velocity > zone.VelHi) continue;
            if (zone.ExclusiveClass != 0)
            {
                foreach (SampleVoice other in samples)
                {
                    if (other.Stage != Stage.Off && other.Channel == ch && other.ExclusiveClass == zone.ExclusiveClass) other.ForceRelease();
                }
            }
            SampleVoice v = (SampleVoice)Allocate(samples);
            v.Channel = ch;
            v.Held = false;
            v.Age = clock;
            v.Begin(this, bank, zone, channel, note, velocity);
            started = true;
        }
        return started;
    }

    private void NoteOff(int ch, int note)
    {
        foreach (Voice v in samples)
        {
            if (v.Stage == Stage.Off || v.Stage == Stage.Release || v.Channel != ch || v.Note != note) continue;
            if (channels[ch].Sustain) v.Held = true;
            else v.Release();
        }
        foreach (Voice v in tones)
        {
            if (v.Stage == Stage.Off || v.Stage == Stage.Release || v.Channel != ch || v.Note != note) continue;
            if (channels[ch].Sustain) v.Held = true;
            else v.Release();
        }
    }

    private void Controller(int ch, ChannelState channel, int controller, int value, bool sound)
    {
        channel.Controllers[controller] = (byte)value;
        switch (controller)
        {
            case 0: channel.BankMsb = value; break;
            case 32: channel.BankLsb = value; break;
            case 1: channel.Modulation = value; break;
            case 7: channel.Volume = value; break;
            case 10: channel.Pan = value; break;
            case 11: channel.Expression = value; break;
            case 91: channel.ReverbSend = value; break;
            case 93: channel.ChorusSend = value; break;
            case 64:
                channel.Sustain = value >= 64;
                if (!channel.Sustain) ReleaseHeld(ch);
                break;
            case 98 or 99: channel.RpnMsb = channel.RpnLsb = 127; break;
            case 100: channel.RpnLsb = value; break;
            case 101: channel.RpnMsb = value; break;
            case 6:
                if (channel.RpnMsb == 0 && channel.RpnLsb == 0) channel.BendRange = Math.Max(value, 1);
                break;
            case 120:
                if (sound) foreach (Voice v in all) if (v.Channel == ch) v.Stage = Stage.Off;
                break;
            case 121:
                channel.ResetControllers();
                break;
            case 123:
                if (!sound) break;
                foreach (Voice v in all)
                {
                    if (v.Stage == Stage.Off || v.Channel != ch || v.IsDrum) continue;
                    if (channel.Sustain) v.Held = true;
                    else v.Release();
                }
                break;
        }
    }

    private void ReleaseHeld(int ch)
    {
        foreach (Voice v in all)
        {
            if (v.Held && v.Channel == ch)
            {
                v.Held = false;
                v.Release();
            }
        }
    }

    // A free voice; otherwise the oldest one already dying; otherwise the oldest.
    private static Voice Allocate(Voice[] pool)
    {
        Voice best = pool[0];
        foreach (Voice v in pool)
        {
            if (v.Stage == Stage.Off) return v;
            if (Rank(v) < Rank(best)) best = v;
        }
        return best;

        static long Rank(Voice v) => (v.Stage == Stage.Release ? 0 : long.MaxValue / 2) + v.Age;
    }

    // Linear up to 0.6, then a smooth tanh shoulder: loud chords never wrap or hard-clip.
    private static float Limit(float x)
    {
        float a = MathF.Abs(x);
        if (a <= .6f) return x;
        float shaped = .6f + .4f * MathF.Tanh((a - .6f) / .4f);
        return x < 0 ? -shaped : shaped;
    }
}
