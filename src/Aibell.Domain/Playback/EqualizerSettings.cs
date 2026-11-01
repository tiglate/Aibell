namespace Aibell.Domain.Playback;

/// <summary>A ten-band graphic equalizer with a preamp; every gain is in decibels.</summary>
public sealed record EqualizerSettings
{
    public const int BandCount = 10;
    public const double MaximumGain = 12;

    /// <summary>LibVLC's equalizer tops out at +20 dB and treats 12 dB of its preamp as unity, which leaves +8 dB of boost.</summary>
    public const double MaximumPreamp = 8;

    /// <summary>Centre frequency of each band in hertz, lowest first. These match LibVLC's fixed band layout.</summary>
    public static IReadOnlyList<double> BandFrequencies { get; } = [31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public static EqualizerSettings Flat { get; } = new();

    public bool IsEnabled { get; init; }

    public double Preamp { get; init; }

    public IReadOnlyList<double> Bands { get; init; } = new double[BandCount];

    /// <summary>The same settings with every gain finite, clamped to ±12 dB and exactly <see cref="BandCount"/> bands.</summary>
    public EqualizerSettings Normalized()
    {
        double[] bands = new double[BandCount];
        for (int index = 0; index < BandCount; index++)
            bands[index] = index < Bands?.Count ? Clamp(Bands[index]) : 0;
        return this with { Preamp = Math.Min(Clamp(Preamp), MaximumPreamp), Bands = bands };
    }

    public bool SameAs(EqualizerSettings other) =>
        IsEnabled == other.IsEnabled && Preamp == other.Preamp && Bands.SequenceEqual(other.Bands);

    private static double Clamp(double gain) => double.IsFinite(gain) ? Math.Clamp(gain, -MaximumGain, MaximumGain) : 0;
}

/// <summary>Built-in curves. Ids are stable keys; the UI translates them.</summary>
public static class EqualizerPresets
{
    public static IReadOnlyList<(string Id, double Preamp, double[] Bands)> All { get; } =
    [
        ("Flat", 0, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        ("BassBoost", -3, [6, 5, 4, 2, 0, 0, 0, 0, 0, 0]),
        ("TrebleBoost", -3, [0, 0, 0, 0, 0, 1, 3, 5, 6, 6]),
        ("Rock", -2, [5, 3, 2, -2, -3, -1, 2, 4, 5, 5]),
        ("Pop", -2, [-1, 2, 4, 5, 3, 0, -1, -1, -1, -1]),
        ("Jazz", -2, [3, 2, 1, 2, -2, -2, 0, 1, 2, 3]),
        ("Classical", -1, [4, 3, 2, 1, -1, -1, 0, 2, 3, 4]),
        ("Vocal", -2, [-2, -3, -2, 1, 4, 4, 3, 1, 0, -1])
    ];

    /// <summary>The preset whose curve matches the given settings, or null for a custom curve.</summary>
    public static string? Match(EqualizerSettings settings)
    {
        foreach ((string id, double preamp, double[] bands) in All)
            if (settings.Preamp == preamp && settings.Bands.SequenceEqual(bands)) return id;
        return null;
    }
}
