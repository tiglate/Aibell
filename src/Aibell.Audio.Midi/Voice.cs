namespace Aibell.Audio.Midi;

internal enum Stage { Off, Attack, Decay, Sustain, Release }

/// <summary>
/// One sounding note. The synthesizer owns allocation and the channel state; a voice only turns a note into samples. Its parameters (pitch, gains, filter) are
/// refreshed every <see cref="MidiSynth.ControlFrames"/> frames at fixed positions of the timeline, so the output never depends on how the caller splits its reads.
/// </summary>
internal abstract class Voice
{
    public Stage Stage;
    public int Channel, Note;
    public float Velocity;
    public bool Held;
    public long Age;

    public bool IsDrum { get; protected init; }

    public abstract void Start(MidiSynth synth, ChannelState channel, int note, float velocity);

    /// <summary>Refreshes the parameters that change slowly.</summary>
    public abstract void Control(MidiSynth synth, ChannelState channel);

    /// <summary>Adds <paramref name="frames"/> frames to the dry stereo buffer and to the mono reverb and chorus sends.</summary>
    public abstract void Render(Span<float> dry, Span<float> reverb, Span<float> chorus, int frames);

    public void Release()
    {
        if (Stage != Stage.Off) Stage = Stage.Release;
    }

    protected static float Noise(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state >> 8) * (2f / (1 << 24)) - 1f;
    }
}

/// <summary>A pitched instrument: detuned oscillators, hammer/pluck noise, a resonant low-pass filter with its own envelope, delayed vibrato.</summary>
internal sealed class ToneVoice : Voice
{
    private const float Silence = 1e-4f;

    private MidiSynth synth = null!;
    private TimbreRuntime rt = null!;
    private float[]? table;
    private double phaseA, phaseB, incrementA, incrementB;
    private readonly double[] partialPhase = new double[4];
    private readonly double[] partialIncrement = new double[4];
    private readonly float[] partialLevel = new float[4];
    private float level, filterEnvelope, noiseEnvelope, lfo;
    private float a1, a2, a3, k;
    private float ic1, ic2;
    private float left, right, reverbSend, chorusSend;
    private uint noise;
    private long ticks;

    public override void Start(MidiSynth synth, ChannelState channel, int note, float velocity)
    {
        this.synth = synth;
        Note = note;
        Velocity = velocity;
        rt = synth.Runtime[Math.Min(channel.Program / 8, Timbres.Families.Length - 1)];
        float[][] bands = synth.Tables[rt.Index];
        table = bands.Length == 0 ? null : bands[WaveTables.Band(note)];
        Stage = Stage.Attack;
        level = 0;
        // Both oscillators start together at a phase taken from the note so that stacked unisons do not all begin at zero.
        phaseA = phaseB = (note * 0.618034) % 1;
        ic1 = ic2 = 0;
        filterEnvelope = 1;
        noiseEnvelope = 1;
        lfo = 0;
        ticks = 0;
        noise = (uint)(note * 2654435761u + 0x9E3779B9u) | 1;
        for (int i = 0; i < partialLevel.Length; i++)
        {
            partialPhase[i] = 0;
            partialLevel[i] = i < rt.Timbre.Partials.Length ? rt.Timbre.Partials[i].Level : 0;
        }
        Control(synth, channel);
    }

    public override void Control(MidiSynth synth, ChannelState channel)
    {
        Timbre t = rt.Timbre;
        float seconds = ticks * MidiSynth.ControlFrames / (float)synth.SampleRate;
        ticks++;
        float depth = t.VibratoDepth * Math.Clamp((seconds - t.VibratoDelay) / .5f, 0, 1) + channel.Modulation / 127f * .5f;
        lfo += 5.5f * MidiSynth.ControlFrames / synth.SampleRate;
        lfo -= MathF.Floor(lfo);
        double semitones = Note - 69 + channel.Bend * channel.BendRange + depth * MathF.Sin(2 * MathF.PI * lfo);
        double f0 = 440.0 * Math.Pow(2, semitones / 12);
        double spread = Math.Pow(2, t.DetuneCents / 2400);
        incrementA = f0 * spread / synth.SampleRate;
        incrementB = f0 / spread / synth.SampleRate;
        for (int i = 0; i < t.Partials.Length; i++) partialIncrement[i] = f0 * t.Partials[i].Ratio < synth.SampleRate * .45 ? f0 * t.Partials[i].Ratio / synth.SampleRate : 0;

        filterEnvelope *= rt.FilterTick;
        float open = t.CutoffOpen * (.6f + .4f * Velocity);
        double cutoff = f0 * (t.CutoffClosed + (open - t.CutoffClosed) * filterEnvelope);
        cutoff = Math.Clamp(cutoff, 30, synth.SampleRate * .45);
        float g = (float)Math.Tan(Math.PI * cutoff / synth.SampleRate);
        k = 1 / t.Q;
        a1 = 1 / (1 + g * (g + k));
        a2 = g * a1;
        a3 = g * a2;

        (left, right, reverbSend, chorusSend) = MidiSynth.Mix(this, channel);
    }

    public override void Render(Span<float> dry, Span<float> reverb, Span<float> chorus, int frames)
    {
        Timbre t = rt.Timbre;
        bool unison = t.DetuneCents > 0;
        float[]? wave = table;
        Stage stage = Stage;
        float lvl = level, noiseLevel = noiseEnvelope;
        double pa = phaseA, pb = phaseB;
        float s1 = ic1, s2 = ic2;
        for (int i = 0; i < frames; i++)
        {
            switch (stage)
            {
                case Stage.Attack:
                    lvl += rt.AttackStep;
                    if (lvl >= 1)
                    {
                        lvl = 1;
                        stage = Stage.Decay;
                    }
                    break;
                case Stage.Decay:
                    lvl = t.Sustain + (lvl - t.Sustain) * rt.Decay;
                    if (MathF.Abs(lvl - t.Sustain) < Silence) stage = Stage.Sustain;
                    break;
                case Stage.Sustain:
                    if (!Held) lvl *= rt.Fall;
                    break;
                case Stage.Release:
                    lvl *= rt.Release;
                    break;
            }
            if (lvl < Silence && stage != Stage.Attack)
            {
                stage = Stage.Off;
                break;
            }

            float x = 0;
            if (wave is not null)
            {
                x = Lookup(wave, pa);
                pa += incrementA;
                pa -= Math.Floor(pa);
                if (unison)
                {
                    x = .5f * (x + Lookup(wave, pb));
                    pb += incrementB;
                    pb -= Math.Floor(pb);
                }
            }
            for (int p = 0; p < rt.Timbre.Partials.Length; p++)
            {
                partialPhase[p] += partialIncrement[p];
                partialPhase[p] -= Math.Floor(partialPhase[p]);
                x += partialLevel[p] * MathF.Sin((float)(partialPhase[p] * 2 * Math.PI)) * .5f;
                partialLevel[p] *= rt.PartialDecay[p];
            }
            if (t.NoiseAmount > 0)
            {
                x += Noise(ref noise) * noiseLevel * t.NoiseAmount;
                noiseLevel *= rt.NoiseDecay;
            }

            // Resonant low-pass: a state-variable filter in the trapezoidal (zero-delay) form.
            float v3 = x - s2;
            float v1 = a1 * s1 + a2 * v3;
            float v2 = s2 + a2 * s1 + a3 * v3;
            s1 = 2 * v1 - s1;
            s2 = 2 * v2 - s2;

            float sample = v2 * lvl;
            dry[i * 2] += sample * left;
            dry[i * 2 + 1] += sample * right;
            reverb[i] += sample * reverbSend;
            chorus[i] += sample * chorusSend;
        }
        Stage = stage;
        level = lvl;
        noiseEnvelope = noiseLevel;
        phaseA = pa;
        phaseB = pb;
        ic1 = s1;
        ic2 = s2;
    }

    private static float Lookup(float[] table, double phase)
    {
        double position = phase * WaveTables.Size;
        int index = (int)position;
        float fraction = (float)(position - index);
        return table[index] + (table[(index + 1) & (WaveTables.Size - 1)] - table[index]) * fraction;
    }
}

/// <summary>A synthesized drum: a pitch-swept sine plus (high-passed) noise.</summary>
internal sealed class DrumVoice : Voice
{
    private const float Silence = 1e-4f;

    private MidiSynth synth = null!;
    private DrumPatch patch;
    private double phase;
    private float tone, noiseLevel, toneDecay, noiseDecay, previous;
    private float left, right, reverbSend, chorusSend;
    private uint noise;

    public DrumVoice() => IsDrum = true;

    public override void Start(MidiSynth synth, ChannelState channel, int note, float velocity)
    {
        this.synth = synth;
        Note = note;
        Velocity = velocity;
        patch = Timbres.Drum(note);
        tone = patch.ToneLevel;
        noiseLevel = patch.NoiseLevel;
        toneDecay = synth.Coefficient(patch.ToneTau);
        noiseDecay = synth.Coefficient(patch.NoiseTau);
        phase = 0;
        previous = 0;
        noise = (uint)(note * 2654435761u + 0x85EBCA6Bu) | 1;
        Stage = Stage.Sustain;
        Control(synth, channel);
    }

    public override void Control(MidiSynth synth, ChannelState channel) => (left, right, reverbSend, chorusSend) = MidiSynth.Mix(this, channel);

    public override void Render(Span<float> dry, Span<float> reverb, Span<float> chorus, int frames)
    {
        double ph = phase;
        float t = tone, n = noiseLevel, prev = previous;
        for (int i = 0; i < frames; i++)
        {
            float sample = 0;
            if (patch.ToneLevel > 0)
            {
                double sweep = t / patch.ToneLevel;
                ph += (patch.ToneEnd + (patch.ToneStart - patch.ToneEnd) * sweep) / synth.SampleRate;
                ph -= Math.Floor(ph);
                sample += t * MathF.Sin((float)(ph * 2 * Math.PI));
                t *= toneDecay;
            }
            if (patch.NoiseLevel > 0)
            {
                float white = Noise(ref noise);
                float shaped = patch.HighPass ? white - prev : white;
                prev = white;
                sample += n * shaped * (patch.HighPass ? .5f : 1f);
                n *= noiseDecay;
            }
            dry[i * 2] += sample * left;
            dry[i * 2 + 1] += sample * right;
            reverb[i] += sample * reverbSend;
            chorus[i] += sample * chorusSend;
            if (t < Silence && n < Silence)
            {
                Stage = Stage.Off;
                break;
            }
        }
        phase = ph;
        tone = t;
        noiseLevel = n;
        previous = prev;
    }
}

/// <summary>A timbre's envelope time constants converted to per-sample (or per-control-tick) multipliers for one sample rate.</summary>
internal sealed class TimbreRuntime
{
    public TimbreRuntime(int index, Timbre timbre, int sampleRate)
    {
        Index = index;
        Timbre = timbre;
        AttackStep = 1f / MathF.Max(1, timbre.Attack * sampleRate);
        Decay = Coef(timbre.Decay, sampleRate);
        Fall = timbre.Fall > 0 ? Coef(timbre.Fall, sampleRate) : 1f;
        Release = Coef(timbre.Release / 4, sampleRate);
        NoiseDecay = Coef(timbre.NoiseTau, sampleRate);
        FilterTick = MathF.Exp(-MidiSynth.ControlFrames / (MathF.Max(timbre.FilterTau, 1e-3f) * sampleRate));
        PartialDecay = [.. timbre.Partials.Select(p => Coef(p.Tau, sampleRate))];
    }

    public int Index { get; }
    public Timbre Timbre { get; }
    public float AttackStep { get; }
    public float Decay { get; }
    public float Fall { get; }
    public float Release { get; }
    public float NoiseDecay { get; }
    public float FilterTick { get; }
    public float[] PartialDecay { get; }

    private static float Coef(float tau, int rate) => MathF.Exp(-1f / (MathF.Max(tau, 1e-4f) * rate));
}
