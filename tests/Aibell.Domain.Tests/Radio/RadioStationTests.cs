using Aibell.Domain.Radio;
using NUnit.Framework;

namespace Aibell.Domain.Tests.Radio;

[TestFixture]
public sealed class RadioStationTests
{
    [TestCase("https://radio.example.invalid/live.mp3")]
    [TestCase("http://localhost:12345/live?channel=1")]
    [TestCase("https://radio.example.invalid:8443/stream")]
    public void Validate_DirectHttpStream_AcceptsWithoutResolvingOrConnecting(string location) =>
        Assert.That(() => new RadioStation(Guid.NewGuid(), "Station", new Uri(location)).Validate(), Throws.Nothing);

    [TestCase("file:///music/song.mp3")]
    [TestCase("ftp://example.invalid/stream")]
    [TestCase("https://user:password@example.invalid/live")]
    [TestCase("https://example.invalid/live#fragment")]
    [TestCase("https://example.invalid/list.m3u")]
    [TestCase("https://example.invalid/list.pls")]
    [TestCase("https://example.invalid/list.xspf")]
    public void Validate_UnsupportedAddress_Rejects(string location) =>
        Assert.That(() => new RadioStation(Guid.NewGuid(), "Station", new Uri(location)).Validate(), Throws.TypeOf<ArgumentException>());

    [TestCase("https://radio.example.invalid/live/master.m3u8")]
    [TestCase("http://radio.example.invalid/hls/stream.M3U8?token=abc&x=1")]
    [TestCase("https://radio.example.invalid:8443/a/b/playlist_audio.m3u8")]
    public void Validate_HlsStream_AcceptsAndReportsHls(string location)
    {
        RadioStation station = new(Guid.NewGuid(), "Station", new Uri(location));
        Assert.Multiple(() =>
        {
            Assert.That(station.Validate, Throws.Nothing);
            Assert.That(station.IsHls, Is.True);
            Assert.That(RadioStation.IsHlsStream(station.Stream), Is.True);
        });
    }

    [TestCase("https://example.invalid/live.mp3")]
    [TestCase("https://example.invalid/live.m3u8x")]
    [TestCase("https://example.invalid/m3u8")]
    [TestCase("https://example.invalid/live?file=a.m3u8")]
    [TestCase("file:///music/list.m3u8")]
    public void IsHls_NonHlsAddress_IsFalse(string location) =>
        Assert.That(RadioStation.IsHlsStream(new Uri(location)), Is.False);

    [TestCase("https://user:password@example.invalid/live.m3u8")]
    [TestCase("https://example.invalid/live.m3u8#fragment")]
    [TestCase("ftp://example.invalid/live.m3u8")]
    [TestCase("file:///music/live.m3u8")]
    public void Validate_HlsWithCredentialsFragmentOrOtherScheme_Rejects(string location) =>
        Assert.That(() => new RadioStation(Guid.NewGuid(), "Station", new Uri(location)).Validate(), Throws.TypeOf<ArgumentException>());

    [Test]
    public void Validate_RejectedWrapper_ExplainsWhichStreamsAreSupported() =>
        Assert.That(() => new RadioStation(Guid.NewGuid(), "Station", new Uri("https://example.invalid/list.pls")).Validate(),
            Throws.TypeOf<ArgumentException>().With.Message.Contains("HLS").And.Message.Contains("PLS"));

    [Test]
    public void Validate_InvalidIdentityTextAndBounds_Rejects()
    {
        RadioStation valid = new(Guid.NewGuid(), "Station", new Uri("https://radio.example.invalid/live"));
        foreach (RadioStation invalid in new[] { valid with { Id = Guid.Empty }, valid with { Name = " " },
            valid with { Name = "Station\0" }, valid with { Name = new string('x', 201) },
            valid with { Stream = new Uri("relative", UriKind.Relative) }, valid with { Rating = 6 }, valid with { PlayCount = -1 } })
            Assert.That(invalid.Validate, Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void Validate_CatalogMetadata_AcceptsCountryCodeLogoAndCatalogId() =>
        Assert.That(() => new RadioStation(Guid.NewGuid(), "Station", new Uri("https://radio.example.invalid/live"),
            Country: "Germany", CountryCode: "DE", LogoUri: new Uri("https://radio.example.invalid/logo.png"), CatalogId: "de-station-1").Validate(), Throws.Nothing);

    [TestCase("de")]
    [TestCase("DEU")]
    [TestCase("1A")]
    [TestCase("")]
    public void Validate_BadCountryCode_Rejects(string code) =>
        Assert.That(() => new RadioStation(Guid.NewGuid(), "Station", new Uri("https://radio.example.invalid/live"), CountryCode: code).Validate(), Throws.TypeOf<ArgumentException>());

    [TestCase("http://radio.example.invalid/logo.png")]
    [TestCase("https://user:secret@radio.example.invalid/logo.png")]
    [TestCase("https://radio.example.invalid/logo.png#frag")]
    [TestCase("file:///tmp/logo.png")]
    public void Validate_UnsafeLogo_Rejects(string logo) =>
        Assert.That(() => new RadioStation(Guid.NewGuid(), "Station", new Uri("https://radio.example.invalid/live"), LogoUri: new Uri(logo)).Validate(), Throws.TypeOf<ArgumentException>());

    [TestCase("Upper-Case")]
    [TestCase("has space")]
    [TestCase("-leading")]
    [TestCase("")]
    public void Validate_BadCatalogId_Rejects(string id) =>
        Assert.That(() => new RadioStation(Guid.NewGuid(), "Station", new Uri("https://radio.example.invalid/live"), CatalogId: id).Validate(), Throws.TypeOf<ArgumentException>());

    [Test]
    public void Validate_CountryWithControlCharacter_Rejects() =>
        Assert.That(() => new RadioStation(Guid.NewGuid(), "Station", new Uri("https://radio.example.invalid/live"), Country: "Ger\nmany").Validate(), Throws.TypeOf<ArgumentException>());
}
