namespace Aibell.Audio.Midi;

/// <summary>A partial that is not a multiple of the fundamental (bells, struck bars): frequency ratio, level and decay time constant in seconds.</summary>
internal readonly record struct Partial(float Ratio, float Level, float Tau);

/// <summary>
/// A built-in instrument: a band-limited harmonic spectrum (or inharmonic partials), an amplitude envelope (seconds; sustain is a level, fall a time
/// constant after the decay, 0 to hold), a resonant low-pass filter whose cutoff is a multiple of the fundamental that closes from
/// <see cref="CutoffOpen"/> to <see cref="CutoffClosed"/> with <see cref="FilterTau"/>, a pair of slightly detuned oscillators, a delayed vibrato and a
/// noise burst for the hammer, pluck or breath at the start of the note.
/// </summary>
internal sealed record Timbre
{
    public float[] Harmonics { get; init; } = [];
    public Partial[] Partials { get; init; } = [];
    public float Attack { get; init; } = .01f;
    public float Decay { get; init; } = .1f;
    public float Sustain { get; init; } = 1;
    public float Fall { get; init; }
    public float Release { get; init; } = .1f;
    public float CutoffOpen { get; init; } = 40;
    public float CutoffClosed { get; init; } = 40;
    public float FilterTau { get; init; } = .2f;
    public float Q { get; init; } = .7f;
    public float DetuneCents { get; init; }
    public float VibratoDepth { get; init; }
    public float VibratoDelay { get; init; } = .3f;
    public float NoiseAmount { get; init; }
    public float NoiseTau { get; init; } = .01f;
}

/// <summary>A synthesized drum: a pitch-swept tone plus noise, each with its own decay time constant.</summary>
internal readonly record struct DrumPatch(float ToneStart, float ToneEnd, float ToneTau, float ToneLevel, float NoiseTau, float NoiseLevel, bool HighPass);

internal static class Timbres
{
    public const int MaxHarmonics = 64;

    private static float[] Saw(int n = MaxHarmonics) => [.. Enumerable.Range(1, n).Select(h => 1f / h)];

    private static float[] Square(int n = MaxHarmonics) => [.. Enumerable.Range(1, n).Select(h => h % 2 == 1 ? 1f / h : 0f)];

    private static float[] Pulse(float duty, int n = MaxHarmonics) =>
        [.. Enumerable.Range(1, n).Select(h => MathF.Abs(MathF.Sin(MathF.PI * h * duty)) / h)];

    /// <summary>One timbre per General MIDI instrument family (program / 8).</summary>
    public static readonly Timbre[] Families =
    [
        // piano: two strings slightly apart, a hammer thump, bright at the strike and mellower as it rings
        new() { Harmonics = [1, .6f, .35f, .2f, .14f, .1f, .07f, .05f, .03f, .02f], Attack = .002f, Decay = .35f, Sustain = .3f, Fall = 2.5f, Release = .25f,
                CutoffOpen = 16, CutoffClosed = 5, FilterTau = .5f, DetuneCents = 2, NoiseAmount = .25f, NoiseTau = .01f },
        // chromatic percussion: bell partials
        new() { Partials = [new(1, 1, 1.2f), new(2.756f, .5f, .8f), new(5.404f, .3f, .5f), new(8.933f, .15f, .3f)], Attack = .001f, Decay = .6f, Sustain = 0, Release = .4f, NoiseAmount = .1f, NoiseTau = .004f },
        // organ: drawbars and a key click
        new() { Harmonics = [1, .7f, .5f, .4f, .3f, .2f, .15f, .1f], Attack = .008f, Decay = .01f, Release = .05f, NoiseAmount = .05f, NoiseTau = .005f },
        // guitar
        new() { Harmonics = [1, .7f, .5f, .35f, .25f, .18f, .12f, .08f, .05f], Attack = .002f, Decay = .3f, Sustain = .25f, Fall = 2, Release = .15f,
                CutoffOpen = 10, CutoffClosed = 3, FilterTau = .25f, NoiseAmount = .15f, NoiseTau = .006f },
        // bass
        new() { Harmonics = [1, .6f, .35f, .2f, .1f], Attack = .005f, Decay = .15f, Sustain = .7f, Release = .1f, CutoffOpen = 8, CutoffClosed = 3, FilterTau = .2f, Q = 1 },
        // strings: detuned saws, slow bow attack, delayed vibrato
        new() { Harmonics = Saw(), Attack = .15f, Decay = .1f, Sustain = .9f, Release = .25f, CutoffOpen = 9, CutoffClosed = 5, FilterTau = .3f, Q = .8f,
                DetuneCents = 7, VibratoDepth = .08f, VibratoDelay = .3f },
        // ensemble
        new() { Harmonics = Saw(), Attack = .25f, Decay = .1f, Sustain = .9f, Release = .3f, CutoffOpen = 8, CutoffClosed = 5, FilterTau = .3f, Q = .8f,
                DetuneCents = 12, VibratoDepth = .06f },
        // brass: the filter opens at the attack ("blat") and settles
        new() { Harmonics = Saw(), Attack = .07f, Decay = .1f, Sustain = .8f, Release = .1f, CutoffOpen = 12, CutoffClosed = 3.5f, FilterTau = .15f, Q = 1,
                DetuneCents = 3, VibratoDepth = .04f, VibratoDelay = .4f },
        // reed: odd harmonics
        new() { Harmonics = Square(), Attack = .04f, Decay = .1f, Sustain = .85f, Release = .1f, CutoffOpen = 9, CutoffClosed = 4, FilterTau = .1f,
                VibratoDepth = .05f, NoiseAmount = .04f, NoiseTau = .05f },
        // pipe: almost a sine with a breath chiff
        new() { Harmonics = [1, .2f, .06f, .02f], Attack = .06f, Decay = .1f, Sustain = .9f, Release = .12f, CutoffOpen = 8, CutoffClosed = 6, FilterTau = .1f,
                VibratoDepth = .12f, VibratoDelay = .4f, NoiseAmount = .12f, NoiseTau = .08f },
        // synth lead
        new() { Harmonics = Pulse(.3f), Attack = .01f, Decay = .1f, Sustain = .8f, Release = .1f, CutoffOpen = 16, CutoffClosed = 6, FilterTau = .3f, Q = 2.5f,
                DetuneCents = 9, VibratoDepth = .1f, VibratoDelay = .2f },
        // synth pad
        new() { Harmonics = Saw(), Attack = .35f, Decay = .1f, Sustain = .85f, Release = .5f, CutoffOpen = 5, CutoffClosed = 3, FilterTau = 1, Q = 1.2f, DetuneCents = 14 },
        // synth effects: a slow filter sweep
        new() { Harmonics = Saw(), Attack = .1f, Decay = .1f, Sustain = .7f, Release = .4f, CutoffOpen = 18, CutoffClosed = 2, FilterTau = 1.5f, Q = 3, DetuneCents = 6 },
        // ethnic: plucked
        new() { Harmonics = [1, .7f, .4f, .2f, .1f, .06f], Attack = .003f, Decay = .25f, Sustain = .2f, Fall = 1.5f, Release = .15f,
                CutoffOpen = 12, CutoffClosed = 3, FilterTau = .2f, NoiseAmount = .2f, NoiseTau = .005f },
        // percussive
        new() { Harmonics = [1, 0, .5f, 0, 0, .3f], Attack = .001f, Decay = .15f, Sustain = 0, Release = .1f, CutoffOpen = 15, CutoffClosed = 4, FilterTau = .05f, NoiseAmount = .3f, NoiseTau = .004f },
        // sound effects
        new() { Harmonics = Saw(20), Attack = .05f, Decay = .1f, Sustain = .6f, Release = .3f, CutoffOpen = 6, CutoffClosed = 2, FilterTau = .8f, Q = 4,
                DetuneCents = 20, VibratoDepth = .3f }
    ];

    public static DrumPatch Drum(int note) => note switch
    {
        35 or 36 => new(120, 45, .12f, 1, .01f, .1f, false),
        37 => new(800, 700, .02f, .6f, .02f, .5f, true),
        38 or 40 => new(220, 170, .08f, .5f, .15f, .8f, false),
        39 => new(0, 0, .01f, 0, .12f, 1, true),
        42 or 44 => new(0, 0, .01f, 0, .03f, 1, true),
        46 => new(0, 0, .01f, 0, .25f, 1, true),
        41 or 43 or 45 or 47 or 48 or 50 => new(80 + (note - 41) * 14 + 100, 80 + (note - 41) * 14, .2f, 1, .02f, .1f, false),
        49 or 52 or 55 or 57 => new(0, 0, .01f, 0, .8f, .8f, true),
        51 or 53 or 59 => new(0, 0, .01f, 0, .4f, .5f, true),
        56 => new(560, 540, .1f, .8f, .01f, .1f, false),
        _ => new(400 + note % 12 * 60, 380 + note % 12 * 60, .05f, .8f, .01f, .1f, false)
    };
}
