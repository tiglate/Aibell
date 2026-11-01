namespace Aibell.Audio.Midi;

/// <summary>Generator numbers of the SoundFont 2.04 specification (section 8.1.2), as far as the synthesizer uses them.</summary>
internal static class Gen
{
    public const int StartAddrsOffset = 0, EndAddrsOffset = 1, StartLoopAddrsOffset = 2, EndLoopAddrsOffset = 3, StartAddrsCoarseOffset = 4;
    public const int ModLfoToPitch = 5, VibLfoToPitch = 6, ModEnvToPitch = 7, InitialFilterFc = 8, InitialFilterQ = 9;
    public const int ModLfoToFilterFc = 10, ModEnvToFilterFc = 11, EndAddrsCoarseOffset = 12, ModLfoToVolume = 13;
    public const int ChorusEffectsSend = 15, ReverbEffectsSend = 16, Pan = 17;
    public const int DelayModLfo = 21, FreqModLfo = 22, DelayVibLfo = 23, FreqVibLfo = 24;
    public const int DelayModEnv = 25, AttackModEnv = 26, HoldModEnv = 27, DecayModEnv = 28, SustainModEnv = 29, ReleaseModEnv = 30;
    public const int KeynumToModEnvHold = 31, KeynumToModEnvDecay = 32;
    public const int DelayVolEnv = 33, AttackVolEnv = 34, HoldVolEnv = 35, DecayVolEnv = 36, SustainVolEnv = 37, ReleaseVolEnv = 38;
    public const int KeynumToVolEnvHold = 39, KeynumToVolEnvDecay = 40;
    public const int Instrument = 41, KeyRange = 43, VelRange = 44, StartLoopAddrsCoarseOffset = 45, Keynum = 46, Velocity = 47;
    public const int InitialAttenuation = 48, EndLoopAddrsCoarseOffset = 50, CoarseTune = 51, FineTune = 52, SampleId = 53, SampleModes = 54;
    public const int ScaleTuning = 56, ExclusiveClass = 57, OverridingRootKey = 58;

    /// <summary>Number of generator slots (0-59 are defined; slot 60 is the end marker and 61 collects the "initial pitch" modulator destination).</summary>
    public const int Count = 62;

    /// <summary>Pseudo destination of the pitch wheel modulator (section 8.4.10: "Initial Pitch"), in cents.</summary>
    public const int InitialPitch = 61;

    /// <summary>Generators that are not available at the preset level (section 8.5); ignored there.</summary>
    public static bool InstrumentOnly(int g) => g is 0 or 1 or 2 or 3 or 4 or 12 or 45 or 46 or 47 or 50 or 54 or 57 or 58;

    /// <summary>The generators whose amounts are two bytes (lo, hi) or indexes instead of signed values.</summary>
    public static bool IsRangeOrIndex(int g) => g is KeyRange or VelRange or Instrument or SampleId;

    /// <summary>Defaults of section 8.1.3 (everything else is 0).</summary>
    public static int[] Defaults()
    {
        var d = new int[Count];
        d[InitialFilterFc] = 13500;
        foreach (int g in new[] { DelayModLfo, DelayVibLfo, DelayModEnv, AttackModEnv, HoldModEnv, DecayModEnv, ReleaseModEnv, DelayVolEnv, AttackVolEnv, HoldVolEnv, DecayVolEnv, ReleaseVolEnv }) d[g] = -12000;
        d[Keynum] = -1;
        d[Velocity] = -1;
        d[ScaleTuning] = 100;
        d[OverridingRootKey] = -1;
        return d;
    }
}

/// <summary>One modulator of section 7.4 / 9.5: the destination receives Transform(Amount * Map(Source) * Map(AmountSource)).</summary>
internal readonly record struct Modulator(int Source, int Destination, int Amount, int AmountSource, int Transform)
{
    /// <summary>Two modulators are identical when source, destination, amount source and transform agree (section 9.5.1).</summary>
    public bool IsIdenticalTo(in Modulator other) =>
        Source == other.Source && Destination == other.Destination && AmountSource == other.AmountSource && Transform == other.Transform;

    /// <summary>False for the enumerators the specification says to ignore: unknown sources or transforms, links, reserved bits.</summary>
    public bool IsUsable => Destination is >= 0 and < Gen.Count && Destination != 60 && Transform is 0 or 2 && IsUsableSource(Source, false) && IsUsableSource(AmountSource, true);

    private static bool IsUsableSource(int source, bool amount)
    {
        if ((source & 0xF800) != 0 || ((source >> 10) & 0x3F) > 3) return false;
        int index = source & 0x7F;
        if ((source & 0x80) != 0)
        {
            // MIDI controller palette: 0, 6, 32, 38, 98-101 and 120-127 are not controllers, 33-63 are LSB halves.
            return !(index is 0 or 6 or 32 or 38 || index is >= 33 and <= 63 || index is >= 98 and <= 101 || index >= 120);
        }
        return index is 0 or 2 or 3 or 10 or 13 or 14 or 16;
    }

    /// <summary>Default modulators of section 8.4. (8.4.5 and 8.4.6 print wrong source and destination values; the enumerations follow the bit layout of 8.2 and the intent of the text.)</summary>
    public static Modulator[] Defaults() =>
    [
        new(0x0502, Gen.InitialAttenuation, 960, 0, 0),   // velocity -> attenuation
        new(0x0102, Gen.InitialFilterFc, -2400, 0, 0),    // velocity -> filter cutoff
        new(0x000D, Gen.VibLfoToPitch, 50, 0, 0),         // channel pressure -> vibrato
        new(0x0081, Gen.VibLfoToPitch, 50, 0, 0),         // CC1 -> vibrato
        new(0x0587, Gen.InitialAttenuation, 960, 0, 0),   // CC7 -> attenuation
        new(0x028A, Gen.Pan, 1000, 0, 0),                 // CC10 -> pan
        new(0x058B, Gen.InitialAttenuation, 960, 0, 0),   // CC11 -> attenuation
        new(0x00DB, Gen.ReverbEffectsSend, 200, 0, 0),    // CC91 -> reverb send
        new(0x00DD, Gen.ChorusEffectsSend, 200, 0, 0),    // CC93 -> chorus send
        new(0x020E, Gen.InitialPitch, 12700, 0x0010, 0)   // pitch wheel x sensitivity -> pitch
    ];

    /// <summary>Evaluates the modulator for a note and its channel.</summary>
    public float Evaluate(int key, int velocity, ChannelState channel)
    {
        float primary = Map(Source, key, velocity, channel);
        float secondary = AmountSource == 0 ? 1 : Map(AmountSource, key, velocity, channel);
        float value = Amount * primary * secondary;
        return Transform == 2 ? MathF.Abs(value) : value;
    }

    private static float Map(int source, int key, int velocity, ChannelState channel)
    {
        int index = source & 0x7F;
        bool controller = (source & 0x80) != 0;
        bool negative = (source & 0x100) != 0;
        bool bipolar = (source & 0x200) != 0;
        int type = (source >> 10) & 0x3F;
        float x;
        if (controller)
        {
            x = channel.Controllers[index] / 128f;
        }
        else
        {
            switch (index)
            {
                case 0: return 1; // "No Controller": treated as 1, whatever the other bits say
                case 2: x = velocity / 128f; break;
                case 3: x = key / 128f; break;
                case 14: x = Math.Clamp((int)MathF.Round((channel.Bend + 1) * 8192), 0, 16383) / 16384f; break;
                case 16: x = (int)channel.BendRange / 128f; break;
                default: x = 0; break; // poly and channel pressure: not tracked
            }
        }
        if (negative) x = 1 - x;
        if (!bipolar) return Shape(x, type);
        float u = 2 * x - 1;
        return u < 0 ? -Shape(-u, type) : Shape(u, type);
    }

    // The curve families of section 8.2.4 on 0..1. Concave is the amplitude-squared law, convex is its mirror image,
    // switch changes at the middle. The specification gives only the shape, not the formula (its printed formula is garbled),
    // so the concave curve is chosen to give the General MIDI volume law: attenuation of 40 log10(127/value) dB.
    private static float Shape(float x, int type) => type switch
    {
        1 => Concave(x),
        2 => 1 - Concave(1 - x),
        3 => x >= .5f ? 1 : 0,
        _ => x
    };

    private static float Concave(float x) => x <= 0 ? 0 : x >= 1 ? 1 : Math.Clamp(-5f / 12 * MathF.Log10(1 - x), 0, 1);
}
