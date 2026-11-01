using System.Text.RegularExpressions;
using Aibell.Application.Radio;
using Aibell.Domain.Radio;
using Aibell.Infrastructure.Radio;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Radio;

[TestFixture]
public sealed partial class EmbeddedCuratedStationCatalogTests
{
    private static readonly string[] ExpectedCountryCodes = ["BR", "CA", "CN", "DE", "ES", "FR", "IL", "IT", "JP", "NL", "PT", "SA", "US"];
    private static readonly string[] CountriesWithoutClassicalOrJazz = ["SA"];
    private static readonly string[] PairStationIds = ["de-a", "de-b"];
    private static readonly string[] GoodStationIds = ["de-good"];
    private static readonly string[] FirstStationNames = ["First"];

    // The user's taste: no gospel, pagode, samba, sertanejo (or Brazilian country), forro or axe in the shipped list.
    [GeneratedRegex("gospel|pagode|samba|sertanej|forró|forro|axé", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Blacklist();

    private static string Entry(string id = "de-test", string stream = "https://stream.example.invalid/live", string name = "Test", string extra = "") =>
        $$"""{ "id": "{{id}}", "name": "{{name}}", "countryCode": "DE", "country": "Germany", "genre": "Rock", "description": "Text", "stream": "{{stream}}", "logo": null, "homepage": "https://example.invalid/" {{extra}} }""";

    private static string File(params string[] entries) => $$"""{ "version": 1, "stations": [ {{string.Join(",", entries)}} ] }""";

    [Test]
    public void ShippedFile_ParsesFullyWithUniqueIdsSafeStreamsAndNoBlacklistedGenres()
    {
        IReadOnlyList<CuratedStation> stations = new EmbeddedCuratedStationCatalog().Stations;
        Assert.That(stations, Has.Count.GreaterThanOrEqualTo(100));
        Assert.Multiple(() =>
        {
            Assert.That(stations.Select(station => station.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(stations.Count));
            Assert.That(stations.Select(station => station.StationId).Distinct().Count(), Is.EqualTo(stations.Count));
            Assert.That(stations.Select(station => station.Stream.AbsoluteUri).Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(stations.Count));
            Assert.That(stations.Where(station => station.Stream.Scheme is not ("http" or "https")), Is.Empty);
            Assert.That(stations.Where(station => station.Logo is { Scheme: not "https" }), Is.Empty);
            Assert.That(stations.Where(station => Blacklist().IsMatch($"{station.Id} {station.Name} {station.Genre} {station.Description}")).Select(station => station.Id), Is.Empty);
            foreach (CuratedStation station in stations) Assert.That(() => station.ToRadioStation().Validate(), Throws.Nothing, station.Id);
        });
    }

    [Test]
    public void ShippedFile_EveryEntryInTheJsonIsAccepted()
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(
            new StreamReader(typeof(EmbeddedCuratedStationCatalog).Assembly.GetManifestResourceStream(EmbeddedCuratedStationCatalog.ResourceName)!).ReadToEnd());
        int declared = document.RootElement.GetProperty("stations").GetArrayLength();
        Assert.That(new EmbeddedCuratedStationCatalog().Stations, Has.Count.EqualTo(declared));
    }

    [Test]
    public void ShippedFile_OffersAClassicalOrJazzStationInEveryCountryWhereOneCouldBeFound()
    {
        IReadOnlyList<CuratedStation> stations = new EmbeddedCuratedStationCatalog().Stations;
        string[] without = [.. stations.GroupBy(station => station.CountryCode)
            .Where(group => !group.Any(station => station.Genre is "Classical" or "Jazz")).Select(group => group.Key).Order()];
        Assert.That(without, Is.EqualTo(CountriesWithoutClassicalOrJazz), "Saudi Arabia has none; any other gap is a regression.");
    }

    [Test]
    public void ShippedFile_KeepsTheCountriesAndHlsStationsTheUserCanPlay()
    {
        IReadOnlyList<CuratedStation> stations = new EmbeddedCuratedStationCatalog().Stations;
        Assert.That(stations.Select(station => station.CountryCode).Distinct().Order(), Is.EqualTo(ExpectedCountryCodes));
        Assert.That(stations.Any(station => RadioStation.IsHlsStream(station.Stream)), Is.True);
    }

    [Test]
    public void Parse_ValidEntries_AreReadInFileOrder()
    {
        IReadOnlyList<CuratedStation> stations = EmbeddedCuratedStationCatalog.Parse(File(Entry("de-a", "https://a.example.invalid/1"), Entry("de-b", "https://b.example.invalid/2")));
        Assert.That(stations.Select(station => station.Id), Is.EqualTo(PairStationIds));
    }

    [Test]
    public void Parse_UnsupportedFormat_IsReadTrimmedAndBadValuesAreIgnored()
    {
        IReadOnlyList<CuratedStation> stations = EmbeddedCuratedStationCatalog.Parse(File(
            Entry("de-a", "https://a.example.invalid/1", extra: ", \"unsupportedFormat\": \" HE-AAC \""),
            Entry("de-b", "https://b.example.invalid/2", extra: ", \"unsupportedFormat\": \"\""),
            Entry("de-c", "https://c.example.invalid/3", extra: ", \"unsupportedFormat\": \"this label is far too long to show on a card\""),
            Entry("de-d", "https://d.example.invalid/4", extra: ", \"unsupportedFormat\": 7"),
            Entry("de-e", "https://e.example.invalid/5")));
        Assert.That(stations.Select(station => station.UnsupportedFormat), Is.EqualTo(new string?[] { "HE-AAC", null, null, null, null }));
    }

    [Test]
    public void ShippedFile_FlagsTheStationsTheBuiltInPlayerCannotPlay()
    {
        // From ./make.sh radio-check on 2026-10-25: 24 HE-AAC (SBR) stations, one Opus and one fragmented-MP4 HLS stream.
        IReadOnlyList<CuratedStation> stations = new EmbeddedCuratedStationCatalog().Stations;
        Dictionary<string, int> counts = stations.Where(station => station.UnsupportedFormat is not null)
            .GroupBy(station => station.UnsupportedFormat!).ToDictionary(group => group.Key, group => group.Count());
        Assert.That(counts, Is.EqualTo(new Dictionary<string, int> { ["HE-AAC"] = 24, ["Opus"] = 1, ["fMP4"] = 1 }));
        Assert.That(stations.Single(station => station.Name == "Radio Deejay").UnsupportedFormat, Is.EqualTo("HE-AAC"));
        Assert.That(stations.Single(station => station.Name == "Europe 1").UnsupportedFormat, Is.Null);
    }

    [Test]
    public void Parse_HlsEntry_IsValid() =>
        Assert.That(EmbeddedCuratedStationCatalog.Parse(File(Entry(stream: "https://cdn.example.invalid/live/master.m3u8"))), Has.Count.EqualTo(1));

    [TestCase("file:///tmp/x.mp3")]
    [TestCase("ftp://example.invalid/x")]
    [TestCase("https://user:pw@example.invalid/x")]
    [TestCase("https://example.invalid/list.pls")]
    [TestCase("not a url")]
    public void Parse_UnsafeOrUnsupportedStream_SkipsOnlyThatEntry(string stream)
    {
        IReadOnlyList<CuratedStation> stations = EmbeddedCuratedStationCatalog.Parse(File(Entry("de-bad", stream), Entry("de-good", "https://good.example.invalid/x")));
        Assert.That(stations.Select(station => station.Id), Is.EqualTo(GoodStationIds));
    }

    [Test]
    public void Parse_DuplicateIdOrStream_KeepsTheFirst()
    {
        IReadOnlyList<CuratedStation> stations = EmbeddedCuratedStationCatalog.Parse(File(
            Entry("de-a", "https://a.example.invalid/1", "First"), Entry("de-a", "https://b.example.invalid/2", "Same id"), Entry("de-c", "https://a.example.invalid/1", "Same stream")));
        Assert.That(stations.Select(station => station.Name), Is.EqualTo(FirstStationNames));
    }

    [TestCase("Upper")]
    [TestCase("has space")]
    [TestCase("")]
    public void Parse_BadCatalogId_SkipsTheEntry(string id) =>
        Assert.That(EmbeddedCuratedStationCatalog.Parse(File(Entry(id))), Is.Empty);

    [Test]
    public void Parse_NonHttpsLogo_IsDroppedButTheStationIsKept()
    {
        string json = File(Entry(extra: ", \"extra\": 1").Replace("\"logo\": null", "\"logo\": \"http://insecure.example.invalid/logo.png\""),
            Entry("de-ok", "https://ok.example.invalid/x").Replace("\"logo\": null", "\"logo\": \"https://ok.example.invalid/logo.png\""));
        IReadOnlyList<CuratedStation> stations = EmbeddedCuratedStationCatalog.Parse(json);
        Assert.Multiple(() =>
        {
            Assert.That(stations, Has.Count.EqualTo(2));
            Assert.That(stations[0].Logo, Is.Null);
            Assert.That(stations[1].Logo!.Scheme, Is.EqualTo("https"));
        });
    }

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("{")]
    [TestCase("[]")]
    [TestCase("null")]
    [TestCase("{ \"stations\": 5 }")]
    [TestCase("{ \"stations\": [ 1, \"x\", null, [], {} ] }")]
    [TestCase("{ \"stations\": [ { \"id\": 5, \"name\": [], \"stream\": {} } ] }")]
    public void Parse_BadData_NeverThrowsAndYieldsNothing(string json)
    {
        IReadOnlyList<CuratedStation>? result = null;
        Assert.That(() => result = EmbeddedCuratedStationCatalog.Parse(json), Throws.Nothing);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Parse_ControlCharactersInText_SkipTheEntry() =>
        Assert.That(EmbeddedCuratedStationCatalog.Parse(File(Entry(name: "Bad\\nName"))), Is.Empty);

    [Test]
    public void Parse_Stream_ReadsTheSameAsString()
    {
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(File(Entry())));
        Assert.That(EmbeddedCuratedStationCatalog.Parse(stream), Has.Count.EqualTo(1));
    }
}
