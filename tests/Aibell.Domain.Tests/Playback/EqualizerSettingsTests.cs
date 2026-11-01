using Aibell.Domain.Playback;
using NUnit.Framework;

namespace Aibell.Domain.Tests.Playback;

[TestFixture]
public sealed class EqualizerSettingsTests
{
    [Test]
    public void Normalized_OutOfRangeAndNonFiniteGains_AreClampedOrZeroed()
    {
        EqualizerSettings settings = new() { IsEnabled = true, Preamp = 40, Bands = [-99, double.NaN, double.PositiveInfinity, 3.5] };

        EqualizerSettings normalized = settings.Normalized();

        Assert.That(normalized.IsEnabled, Is.True);
        Assert.That(normalized.Preamp, Is.EqualTo(EqualizerSettings.MaximumPreamp));
        Assert.That(normalized.Bands, Has.Count.EqualTo(EqualizerSettings.BandCount));
        Assert.That(normalized.Bands.Take(4), Is.EqualTo(new[] { -12d, 0, 0, 3.5 }));
        Assert.That(normalized.Bands.Skip(4), Is.All.Zero, "missing bands are flat");
    }

    [Test]
    public void Normalized_TooManyBands_KeepsTheFirstTen()
    {
        EqualizerSettings normalized = (new EqualizerSettings { Bands = Enumerable.Repeat(1d, 14).ToArray() }).Normalized();

        Assert.That(normalized.Bands, Is.EqualTo(Enumerable.Repeat(1d, EqualizerSettings.BandCount)));
    }

    [Test]
    public void BandFrequencies_AreTenAscendingValues()
    {
        Assert.That(EqualizerSettings.BandFrequencies, Has.Count.EqualTo(EqualizerSettings.BandCount));
        Assert.That(EqualizerSettings.BandFrequencies, Is.Ordered.Ascending);
    }

    [Test]
    public void Presets_AreValidAndMatchThemselves()
    {
        foreach ((string id, double preamp, double[] bands) in EqualizerPresets.All)
        {
            EqualizerSettings settings = new() { Preamp = preamp, Bands = bands };
            Assert.That(settings.Normalized().SameAs(settings), Is.True, $"{id} stays inside ±12 dB with ten bands");
            Assert.That(EqualizerPresets.Match(settings), Is.EqualTo(id));
        }
        Assert.That(EqualizerPresets.Match(EqualizerSettings.Flat with { Preamp = 1 }), Is.Null);
    }
}
