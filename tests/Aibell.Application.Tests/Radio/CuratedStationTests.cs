using Aibell.Application.Radio;
using NUnit.Framework;

namespace Aibell.Application.Tests.Radio;

[TestFixture]
public sealed class CuratedStationTests
{
    [Test]
    public void IdFor_SameCatalogId_GivesTheSameIdentifierEveryTime()
    {
        Guid first = CuratedStation.IdFor("de-rock-antenne");
        Guid second = CuratedStation.IdFor("de-rock-antenne");
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void IdFor_DifferentCatalogIds_GiveDifferentIdentifiers() =>
        Assert.That(CuratedStation.IdFor("de-rock-antenne"), Is.Not.EqualTo(CuratedStation.IdFor("de-rock-antenne-2")));

    [Test]
    public void IdFor_IsAVersionFiveRfc4122Identifier()
    {
        string text = CuratedStation.IdFor("nl-concertzender-klassiek").ToString("D");
        Assert.Multiple(() =>
        {
            Assert.That(text[14], Is.EqualTo('5'));
            Assert.That(text[19], Is.AnyOf('8', '9', 'a', 'b'));
        });
    }

    [Test]
    public void IdFor_IsStableAcrossReleases() =>
        // A saved station is matched by this identifier forever: changing the algorithm or namespace would duplicate every saved copy.
        Assert.Multiple(() =>
        {
            Assert.That(CuratedStation.IdFor("de-rock-antenne").ToString("D"), Is.EqualTo("42e529a2-cddc-54e2-93bd-878cd93fed39"));
            Assert.That(CuratedStation.IdFor("nl-concertzender-klassiek").ToString("D"), Is.EqualTo("b38cfc9b-3776-52a8-8544-260bad10ee9d"));
        });

    [Test]
    public void ToRadioStation_CopiesTheCatalogFieldsAndShowsTheWebsiteAsCreator()
    {
        CuratedStation station = new("de-x", "X", "DE", "Germany", "Rock", "Loud", new Uri("https://stream.example.invalid/x"),
            new Uri("https://example.invalid/logo.png"), new Uri("https://www.example.invalid/home"));
        Domain.Radio.RadioStation saved = station.ToRadioStation();
        Assert.Multiple(() =>
        {
            Assert.That(saved.Id, Is.EqualTo(station.StationId));
            Assert.That(saved.CatalogId, Is.EqualTo("de-x"));
            Assert.That(saved.Country, Is.EqualTo("Germany"));
            Assert.That(saved.CountryCode, Is.EqualTo("DE"));
            Assert.That(saved.LogoUri, Is.EqualTo(station.Logo));
            Assert.That(saved.Creator, Is.EqualTo("www.example.invalid"));
            Assert.That(() => saved.Validate(), Throws.Nothing);
        });
    }
}
