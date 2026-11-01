using Aibell.Domain.Playlists;
using NUnit.Framework;

namespace Aibell.Domain.Tests.Playlists;

[TestFixture]
public sealed class SmartPlaylistDefinitionTests
{
    [TestCase(SmartPlaylistField.Rating, "0")]
    [TestCase(SmartPlaylistField.Rating, "5")]
    [TestCase(SmartPlaylistField.Year, "2026")]
    [TestCase(SmartPlaylistField.PlayCount, "2147483647")]
    [TestCase(SmartPlaylistField.SkipCount, "0")]
    public void Validate_ValidNumericRules_AcceptsIntegerValues(SmartPlaylistField field, string value)
    {
        SmartPlaylistDefinition definition = new(SmartPlaylistMatch.All,
            [new(field, SmartPlaylistOperator.GreaterOrEqual, value)]);

        Assert.That(definition.Validate, Throws.Nothing);
    }

    [TestCase(SmartPlaylistField.Rating, "6")]
    [TestCase(SmartPlaylistField.Year, "10000")]
    [TestCase(SmartPlaylistField.PlayCount, "-1")]
    [TestCase(SmartPlaylistField.SkipCount, "2147483648")]
    [TestCase(SmartPlaylistField.Rating, "4.5")]
    [TestCase(SmartPlaylistField.Rating, "4,5")]
    public void Validate_InvalidNumericRules_RejectsValue(SmartPlaylistField field, string value)
    {
        SmartPlaylistDefinition definition = new(SmartPlaylistMatch.Any,
            [new(field, SmartPlaylistOperator.Equals, value)]);

        Assert.That(definition.Validate, Throws.ArgumentException);
    }

    [Test]
    public void Validate_UnknownEnumsAndIncompatibleComparisons_RejectsRules()
    {
        SmartPlaylistRule[] invalid =
        [
            new((SmartPlaylistField)99, SmartPlaylistOperator.Equals, "text"),
            new(SmartPlaylistField.Rating, SmartPlaylistOperator.Contains, "4"),
            new(SmartPlaylistField.Title, SmartPlaylistOperator.GreaterOrEqual, "text"),
            new(SmartPlaylistField.Artist, (SmartPlaylistOperator)99, "Artist")
        ];
        foreach (SmartPlaylistRule rule in invalid)
            Assert.That(rule.Validate, Throws.ArgumentException);
        Assert.That(new SmartPlaylistDefinition((SmartPlaylistMatch)99,
            [new(SmartPlaylistField.Rating, SmartPlaylistOperator.Equals, "4")]).Validate, Throws.ArgumentException);
    }

    [Test]
    public void Validate_BlankText_AllowsMissingTagEqualityButRejectsContains()
    {
        Assert.That(new SmartPlaylistRule(SmartPlaylistField.Genre, SmartPlaylistOperator.Equals, "").Validate, Throws.Nothing);
        Assert.That(new SmartPlaylistRule(SmartPlaylistField.Genre, SmartPlaylistOperator.Contains, " ").Validate, Throws.ArgumentException);
    }

    [Test]
    public void Validate_EmptyExcessiveOrNullRules_RejectsDefinition()
    {
        SmartPlaylistRule rule = new(SmartPlaylistField.Artist, SmartPlaylistOperator.Contains, "Artist");
        Assert.That(new SmartPlaylistDefinition(SmartPlaylistMatch.All, []).Validate, Throws.ArgumentException);
        Assert.That(new SmartPlaylistDefinition(SmartPlaylistMatch.All, Enumerable.Repeat(rule, 21).ToArray()).Validate, Throws.ArgumentException);
        Assert.That(new SmartPlaylistDefinition(SmartPlaylistMatch.All, [null!]).Validate, Throws.ArgumentException);
    }
}
