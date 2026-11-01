using System.Text.Json;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class StructuredMetadataPreservationTests
{
    [TestCase("flac")]
    [TestCase("m4a")]
    [TestCase("ogg")]
    [TestCase("opus")]
    [TestCase("mp3")]
    public async Task StageAsync_TitleAndArtworkEdits_PreserveLyricsAndSupportedChapterLayouts(string extension)
    {
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-structured-tags-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(directory, "fixture." + extension);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), source);
            ATL.Track seed = new(source);
            seed.Lyrics.Add(new ATL.LyricsInfo { UnsynchronizedLyrics = "Synthetic fixture text 日本語\nSecond fixture line", LanguageCode = "eng" });
            if (extension is "m4a" or "mp3")
            {
                seed.Chapters.Add(new ATL.ChapterInfo(0, "First fixture chapter"));
                seed.Chapters.Add(new ATL.ChapterInfo(500, "Second fixture chapter"));
            }
            Assert.That(seed.Save(), Is.True);
            ATL.Track before = new(source);
            Assert.That(before.Lyrics.Select(lyric => lyric.UnsynchronizedLyrics), Has.Some.Contains("Synthetic fixture"));
            if (extension is "m4a" or "mp3") Assert.That(before.Chapters, Has.Count.GreaterThanOrEqualTo(2));
            string chapters = JsonSerializer.Serialize(before.Chapters), lyrics = JsonSerializer.Serialize(before.Lyrics);
            byte[] image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "artwork", "cover.png"));
            await using var stage = await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "Edited title", Artwork: new(ImageData: image)));
            using FileStream input = File.OpenRead(stage.StagedFile.LocalPath);
            ATL.Track after = new(input, "." + extension);
            Assert.That(JsonSerializer.Serialize(after.Lyrics), Is.EqualTo(lyrics));
            Assert.That(JsonSerializer.Serialize(after.Chapters), Is.EqualTo(chapters));
        }
        finally { Directory.Delete(directory, true); }
    }
}
