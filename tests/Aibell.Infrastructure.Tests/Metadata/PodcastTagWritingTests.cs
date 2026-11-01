using System.Security.Cryptography;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class PodcastTagWritingTests
{
    private static readonly string[] Formats = ["flac", "m4a", "ogg", "opus", "mp3"];
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-podcast-tags-" + Guid.NewGuid().ToString("N"))).FullName;
    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    private string Copy(string extension)
    {
        string source = Path.Combine(directory, "fixture." + extension);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), source);
        return source;
    }

    [TestCaseSource(nameof(Formats))]
    public async Task WriteAsync_ShowSeasonEpisode_AddReplaceClearAndRestore(string extension)
    {
        string source = Copy(extension);
        string original = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        MetadataFileWriter writer = new(new AtlMetadataEditStager());
        var added = await writer.WriteAsync(new Uri(source), new(ShowName: "Show 日本語", SeasonNumber: 2, EpisodeNumber: 7));
        await AssertValues(source, "Show 日本語", 2, 7);
        var replaced = await writer.WriteAsync(new Uri(source), new(ShowName: "Replacement ação", SeasonNumber: 3, EpisodeNumber: 12));
        await AssertValues(source, "Replacement ação", 3, 12);
        var cleared = await writer.WriteAsync(new Uri(source), new(ShowName: "", SeasonNumber: 0, EpisodeNumber: 0));
        await AssertValues(source, "", 0, 0);
        await writer.RestoreAsync(cleared.Backup);
        await AssertValues(source, "Replacement ação", 3, 12);
        await writer.RestoreAsync(replaced.Backup);
        await writer.RestoreAsync(added.Backup);
        Assert.That(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))), Is.EqualTo(original));
    }

    [TestCaseSource(nameof(Formats))]
    public async Task WriteAsync_ConflictingAliases_UpdateTogetherAndPreserveMovementTags(string extension)
    {
        string source = Copy(extension);
        ATL.Track seed = new(source);
        seed.AdditionalFields["SHOWTITLE"] = "Old show";
        seed.AdditionalFields["TVSHOW"] = "Other show";
        seed.AdditionalFields["SEASON"] = "2";
        seed.AdditionalFields["TVSEASON"] = "3";
        seed.AdditionalFields["EPISODE"] = "4";
        seed.AdditionalFields["TVEPISODENUM"] = "5";
        seed.AdditionalFields["AIBELL_SHOW"] = "Private show";
        seed.AdditionalFields["AIBELL_EPISODE"] = "Private episode";
        if (extension is "mp3" or "m4a") { seed.SeriesTitle = "Native show"; seed.SeriesPart = "99"; }
        Assert.That(seed.Save(), Is.True);
        MetadataFileWriter writer = new(new AtlMetadataEditStager());
        await writer.WriteAsync(new Uri(source), new(ShowName: "New show", SeasonNumber: 8, EpisodeNumber: 13));
        await AssertValues(source, "New show", 8, 13);
        ATL.Track after = new(source);
        if (extension is "mp3" or "m4a")
        {
            Assert.That(after.SeriesTitle, Is.EqualTo("Native show"));
            Assert.That(after.SeriesPart, Is.EqualTo("99"));
        }
        Assert.That(after.AdditionalFields.Values, Does.Contain("Private show"));
        Assert.That(after.AdditionalFields.Values, Does.Contain("Private episode"));
        await writer.WriteAsync(new Uri(source), new(SeasonNumber: 9));
        await AssertValues(source, "New show", 9, 13);
        await writer.WriteAsync(new Uri(source), new(EpisodeNumber: 0));
        await AssertValues(source, "New show", 9, 0);
        await writer.WriteAsync(new Uri(source), new(Title: "Unrelated edit"));
        await AssertValues(source, "New show", 9, 0);
        await writer.WriteAsync(new Uri(source), new(ShowName: "", SeasonNumber: 0));
        await AssertValues(source, "", 0, 0);
    }

    [TestCaseSource(nameof(Formats))]
    [Category("MetadataWriteDecode")]
    public async Task WriteAsync_ShowSeasonEpisode_PreserveDecodedAudio(string extension)
    {
        if (Environment.GetEnvironmentVariable("AIBELL_METADATA_WRITE_TESTS") != "1") Assert.Ignore("Enable FFmpeg metadata decode tests.");
        string source = Copy(extension);
        string original = await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source);
        MetadataFileWriter writer = new(new AtlMetadataEditStager());
        await writer.WriteAsync(new Uri(source), new(ShowName: "Show 日本語", SeasonNumber: 2, EpisodeNumber: 7));
        Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source), Is.EqualTo(original));
        await writer.WriteAsync(new Uri(source), new(ShowName: "", SeasonNumber: 0, EpisodeNumber: 0));
        Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source), Is.EqualTo(original));
    }

    private static async Task AssertValues(string source, string show, int season, int episode)
    {
        var imported = await new AtlMediaMetadataReader().ReadAsync(new Uri(source));
        var details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source));
        Assert.Multiple(() =>
        {
            Assert.That(imported.ShowName ?? "", Is.EqualTo(show));
            Assert.That(imported.SeasonNumber, Is.EqualTo(season));
            Assert.That(imported.EpisodeNumber, Is.EqualTo(episode));
            Assert.That(details.Metadata.ShowName, Is.EqualTo(show));
            Assert.That(details.Metadata.SeasonNumber, Is.EqualTo(season));
            Assert.That(details.Metadata.EpisodeNumber, Is.EqualTo(episode));
        });
    }
}
