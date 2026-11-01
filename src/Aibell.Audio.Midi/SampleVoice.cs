namespace Aibell.Audio.Midi;

/// <summary>
/// A note played from a SoundFont zone (section 9.1): the sample read with 4-point interpolation and looping, a resonant low-pass filter, the volume and
/// modulation envelopes, the vibrato and modulation LFOs, and the modulators of the zone. Parameters are refreshed at the synthesizer's control ticks.
/// </summary>
internal sealed class SampleVoice : Voice
{
    private enum Phase { Delay, Attack, Hold, Decay, Sustain, Release }

    private const float Floor = 1e-5f; // -100 dB: the volume envelope's zero
    private const float OutputGain = .35f;

    private MidiSynth synth = null!;
    private SoundFont font = null!;
    private SoundFont.SampleZone zone = null!;
    private readonly float[] sum = new float[Gen.Count];

    private int key, velocity, rootKey;
    private double position, step, baseStep;
    private int startLimit, endLimit, loopStart, loopEnd;
    private bool looping, loopUntilRelease;

    // Volume envelope (amplitude, advanced per sample)
    private Phase phase;
    private int delaySamples, holdSamples;
    private float amplitude, attackStep, decayFactor, sustainAmplitude, releaseFactor;
    private bool released;

    // Modulation envelope and LFOs (advanced per control tick)
    private float modEnvelope, modTime;
    private Phase modPhase;
    private long ticks;

    // Output
    private float left, right, reverbSend, chorusSend;
    private float a1, a2, a3, dcGain;
    private bool filtering;
    private float ic1, ic2;

    public bool IsLooping => looping;

    public int ExclusiveClass => zone.ExclusiveClass;

    /// <summary>Starts the voice for <paramref name="note"/> on <paramref name="zone"/>.</summary>
    public void Begin(MidiSynth synth, SoundFont font, SoundFont.SampleZone zone, ChannelState channel, int note, int velocity127)
    {
        this.synth = synth;
        this.font = font;
        this.zone = zone;
        Note = note;
        Velocity = velocity127 / 127f;
        key = zone.Gen[Gen.Keynum] is >= 0 and <= 127 ? zone.Gen[Gen.Keynum] : note;
        velocity = zone.Gen[Gen.Velocity] is >= 0 and <= 127 ? zone.Gen[Gen.Velocity] : velocity127;
        SoundFont.SampleHeader s = zone.Sample;
        rootKey = zone.Gen[Gen.OverridingRootKey] is >= 0 and <= 127 ? zone.Gen[Gen.OverridingRootKey] : s.OriginalPitch;

        long first = s.Start + zone.Gen[Gen.StartAddrsOffset] + 32768L * zone.Gen[Gen.StartAddrsCoarseOffset];
        long last = s.End + zone.Gen[Gen.EndAddrsOffset] + 32768L * zone.Gen[Gen.EndAddrsCoarseOffset];
        startLimit = (int)Math.Clamp(first, 0, font.SampleCount - 1);
        endLimit = (int)Math.Clamp(last, startLimit + 1, font.SampleCount);
        long ls = s.StartLoop + zone.Gen[Gen.StartLoopAddrsOffset] + 32768L * zone.Gen[Gen.StartLoopAddrsCoarseOffset];
        long le = s.EndLoop + zone.Gen[Gen.EndLoopAddrsOffset] + 32768L * zone.Gen[Gen.EndLoopAddrsCoarseOffset];
        int mode = zone.Gen[Gen.SampleModes] & 3;
        loopStart = (int)Math.Clamp(ls, startLimit, endLimit - 1);
        loopEnd = (int)Math.Clamp(le, loopStart, endLimit);
        // Mode 2 is unused and means no loop; a loop needs some length to repeat.
        looping = (mode == 1 || mode == 3) && loopEnd - loopStart >= 2;
        loopUntilRelease = mode == 3;
        position = startLimit;
        baseStep = s.SampleRate / (double)synth.SampleRate;

        Stage = Stage.Attack;
        released = false;
        ticks = 0;
        modEnvelope = 0;
        modTime = 0;
        modPhase = Phase.Delay;
        ic1 = ic2 = 0;
        amplitude = 0;
        phase = Phase.Delay;
        ComputeEnvelope(channel);
        Control(synth, channel);
    }

    public override void Start(MidiSynth synth, ChannelState channel, int note, float velocity) => throw new NotSupportedException("Use Begin.");

    /// <summary>Terminates the voice quickly (exclusive class, section 8.1.2 generator 57).</summary>
    public void ForceRelease()
    {
        if (Stage == Stage.Off) return;
        Stage = Stage.Release;
        EnterRelease(.01f);
    }

    public override void Control(MidiSynth synth, ChannelState channel)
    {
        ticks++;
        float seconds = ticks * MidiSynth.ControlFrames / (float)synth.SampleRate;
        float dt = MidiSynth.ControlFrames / (float)synth.SampleRate;
        Array.Clear(sum);
        foreach (Modulator m in zone.Modulators) sum[m.Destination] += m.Evaluate(key, velocity, channel);
        float G(int g) => zone.Gen[g] + sum[g];

        AdvanceModulationEnvelope(dt, channel);
        float vibrato = Lfo(seconds, G(Gen.DelayVibLfo), G(Gen.FreqVibLfo));
        float modulation = Lfo(seconds, G(Gen.DelayModLfo), G(Gen.FreqModLfo));

        // Pitch in cents (9.1.1): key against root key with scale tuning, tunings, correction, modulators, LFOs and envelope.
        double cents = (key - rootKey) * G(Gen.ScaleTuning) + G(Gen.CoarseTune) * 100.0 + G(Gen.FineTune) + zone.Sample.PitchCorrection
            + sum[Gen.InitialPitch] + G(Gen.VibLfoToPitch) * vibrato + G(Gen.ModLfoToPitch) * modulation + G(Gen.ModEnvToPitch) * modEnvelope;
        step = baseStep * Math.Pow(2, cents / 1200);

        // Filter (9.1.3): cutoff in absolute cents, resonance in centibels above the DC gain, DC gain lowered by half of it.
        float fc = G(Gen.InitialFilterFc) + G(Gen.ModLfoToFilterFc) * modulation + G(Gen.ModEnvToFilterFc) * modEnvelope;
        float q = Math.Clamp(G(Gen.InitialFilterQ), 0, 960) / 10f;
        double hertz = 8.176 * Math.Pow(2, Math.Clamp(fc, 1500, 20000) / 1200.0);
        filtering = hertz < 19500 || q > 0;
        if (filtering)
        {
            double limit = synth.SampleRate * .45;
            float g = (float)Math.Tan(Math.PI * Math.Min(hertz, limit) / synth.SampleRate);
            float k = 1 / (MathF.Sqrt(.5f) * MathF.Pow(10, q / 20));
            a1 = 1 / (1 + g * (g + k));
            a2 = g * a1;
            a3 = g * a2;
            dcGain = MathF.Pow(10, -q / 40);
        }

        // Amplifier, pan and sends (9.1.4, 9.1.5)
        float attenuation = Math.Clamp(G(Gen.InitialAttenuation) - G(Gen.ModLfoToVolume) * modulation, 0, 1440);
        float gain = OutputGain * MathF.Pow(10, -attenuation / 200);
        float pan = Math.Clamp(G(Gen.Pan) / 1000f + .5f, 0, 1);
        left = gain * MathF.Cos(pan * (MathF.PI / 2));
        right = gain * MathF.Sin(pan * (MathF.PI / 2));
        reverbSend = gain * Math.Clamp(G(Gen.ReverbEffectsSend) / 1000f, 0, 1);
        chorusSend = gain * Math.Clamp(G(Gen.ChorusEffectsSend) / 1000f, 0, 1);

        if (Stage == Stage.Release && !released) EnterRelease(null);
    }

    public override void Render(Span<float> dry, Span<float> reverb, Span<float> chorus, int frames)
    {
        if (Stage == Stage.Release && !released) EnterRelease(null);
        double pos = position;
        float s1 = ic1, s2 = ic2, amp = amplitude;
        bool loop = looping && !(loopUntilRelease && released);
        for (int i = 0; i < frames; i++)
        {
            switch (phase)
            {
                case Phase.Delay:
                    if (--delaySamples <= 0) phase = Phase.Attack;
                    break;
                case Phase.Attack:
                    amp += attackStep;
                    if (amp >= 1)
                    {
                        amp = 1;
                        phase = Phase.Hold;
                    }
                    break;
                case Phase.Hold:
                    if (--holdSamples <= 0) phase = Phase.Decay;
                    break;
                case Phase.Decay:
                    amp *= decayFactor;
                    if (amp <= sustainAmplitude)
                    {
                        amp = sustainAmplitude;
                        phase = Phase.Sustain;
                    }
                    break;
                case Phase.Release:
                    amp *= releaseFactor;
                    break;
            }
            if (phase == Phase.Release && amp < Floor || amp < Floor && phase == Phase.Sustain)
            {
                Stage = Stage.Off;
                break;
            }
            if (phase == Phase.Delay) continue;

            if (loop && pos >= loopEnd) pos -= loopEnd - loopStart;
            if (pos >= endLimit)
            {
                Stage = Stage.Off;
                break;
            }
            float x = Interpolate((int)pos, (float)(pos - (int)pos));
            pos += step;

            if (filtering)
            {
                float v3 = x - s2;
                float v1 = a1 * s1 + a2 * v3;
                float v2 = s2 + a2 * s1 + a3 * v3;
                s1 = 2 * v1 - s1;
                s2 = 2 * v2 - s2;
                x = v2 * dcGain;
            }
            float sample = x * amp;
            dry[i * 2] += sample * left;
            dry[i * 2 + 1] += sample * right;
            reverb[i] += sample * reverbSend;
            chorus[i] += sample * chorusSend;
        }
        position = pos;
        ic1 = s1;
        ic2 = s2;
        amplitude = amp;
    }

    // Four-point, third-order (Catmull-Rom) interpolation between the points around the position. Near a loop end the data that follows the
    // loop is, by the specification's looping rules (section 6.3), the same as the data after the loop start, so it is read as it is.
    private float Interpolate(int index, float t)
    {
        float y0 = font.At(index - 1), y1 = font.At(index), y2 = font.At(index + 1), y3 = font.At(index + 2);
        return y1 + .5f * t * (y2 - y0 + t * (2 * y0 - 5 * y1 + 4 * y2 - y3 + t * (3 * (y1 - y2) + y3 - y0)));
    }

    // ---- envelopes ----

    private static float Seconds(float timecents) => MathF.Pow(2, Math.Clamp(timecents, -12000, 8000) / 1200);

    private void ComputeEnvelope(ChannelState channel)
    {
        int[] g = zone.Gen;
        float perKey = 60 - key;
        float hold = g[Gen.HoldVolEnv] + g[Gen.KeynumToVolEnvHold] * perKey;
        float decay = g[Gen.DecayVolEnv] + g[Gen.KeynumToVolEnvDecay] * perKey;
        delaySamples = Math.Max(1, (int)(Seconds(g[Gen.DelayVolEnv]) * synth.SampleRate));
        attackStep = 1f / MathF.Max(1, Seconds(g[Gen.AttackVolEnv]) * synth.SampleRate);
        holdSamples = Math.Max(1, (int)(Seconds(hold) * synth.SampleRate));
        // Decay and release are linear in decibels: 100 dB take the time given (so a constant factor per sample).
        decayFactor = MathF.Pow(10, -5f / (Seconds(decay) * synth.SampleRate));
        sustainAmplitude = MathF.Pow(10, -Math.Clamp(g[Gen.SustainVolEnv], 0, 1440) / 200f);
        releaseFactor = MathF.Pow(10, -5f / (Seconds(g[Gen.ReleaseVolEnv]) * synth.SampleRate));
        _ = channel;
    }

    private void EnterRelease(float? seconds)
    {
        released = true;
        phase = Phase.Release;
        if (seconds is float fast) releaseFactor = MathF.Pow(10, -5f / (fast * synth.SampleRate));
        modPhase = Phase.Release;
    }

    // Modulation envelope (9.1.7): delay, convex attack to 1, hold, decay to the sustain level, release to 0; linear in its own (percentage) units.
    private void AdvanceModulationEnvelope(float dt, ChannelState channel)
    {
        _ = channel;
        int[] g = zone.Gen;
        float perKey = 60 - key;
        switch (modPhase)
        {
            case Phase.Delay:
                modTime += dt;
                if (modTime >= Seconds(g[Gen.DelayModEnv]))
                {
                    modPhase = Phase.Attack;
                    modTime = 0;
                }
                break;
            case Phase.Attack:
                modEnvelope += dt / Seconds(g[Gen.AttackModEnv]);
                if (modEnvelope >= 1)
                {
                    modEnvelope = 1;
                    modPhase = Phase.Hold;
                    modTime = 0;
                }
                break;
            case Phase.Hold:
                modTime += dt;
                if (modTime >= Seconds(g[Gen.HoldModEnv] + g[Gen.KeynumToModEnvHold] * perKey)) modPhase = Phase.Decay;
                break;
            case Phase.Decay:
                float sustain = 1 - Math.Clamp(g[Gen.SustainModEnv], 0, 1000) / 1000f;
                modEnvelope -= dt / Seconds(g[Gen.DecayModEnv] + g[Gen.KeynumToModEnvDecay] * perKey);
                if (modEnvelope <= sustain)
                {
                    modEnvelope = sustain;
                    modPhase = Phase.Sustain;
                }
                break;
            case Phase.Release:
                modEnvelope = MathF.Max(0, modEnvelope - dt / Seconds(g[Gen.ReleaseModEnv]));
                break;
        }
    }

    // A triangle that starts at zero after its delay and ramps up to +1, down to -1 and up again (9.1.6).
    private static float Lfo(float seconds, float delayTimecents, float frequencyCents)
    {
        float t = seconds - Seconds(delayTimecents);
        if (t <= 0) return 0;
        float cycle = t * 8.176f * MathF.Pow(2, Math.Clamp(frequencyCents, -16000, 4500) / 1200);
        float p = cycle - MathF.Floor(cycle);
        return p < .25f ? 4 * p : p < .75f ? 2 - 4 * p : 4 * p - 4;
    }
}
