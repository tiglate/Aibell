using NUnit.Framework;

namespace Aibell.Metadata.MusicBrainz.Tests;

[TestFixture]
public sealed class StringSimilarityTests
{
    [Test]
    public void Compute_IdenticalStrings_ReturnsOne()
    {
        Assert.That(StringSimilarity.Compute("Bohemian Rhapsody", "Bohemian Rhapsody"), Is.EqualTo(1.0));
    }

    [Test]
    public void Compute_DifferentCasingAndWhitespace_ReturnsOne()
    {
        Assert.That(StringSimilarity.Compute("  queen  ", "Queen"), Is.EqualTo(1.0));
    }

    [Test]
    public void Compute_BothEmpty_ReturnsOne()
    {
        Assert.That(StringSimilarity.Compute(null, "  "), Is.EqualTo(1.0));
    }

    [Test]
    public void Compute_OneEmpty_ReturnsZero()
    {
        Assert.That(StringSimilarity.Compute("Queen", null), Is.Zero);
    }

    [Test]
    public void Compute_CompletelyDifferentStrings_ReturnsLowScore()
    {
        Assert.That(StringSimilarity.Compute("Bohemian Rhapsody", "xyz"), Is.LessThan(0.2));
    }

    [Test]
    public void Compute_SingleCharacterTypo_ReturnsHighScore()
    {
        Assert.That(StringSimilarity.Compute("Bohemian Rhapsody", "Bohemian Rapsody"), Is.GreaterThan(0.9));
    }
}
