using Aibell.Application.Library;
using Aibell.Domain.Audiobooks;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Audiobooks;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

/// <summary>
/// Characterizes how the readers treat tag and format variants beyond the basic one-tone corpus: Unicode text, two tag versions in one
/// file, lyrics, chapters, several pictures, repeated fields and unusual WAV/FLAC/Vorbis stream parameters. The files in
/// <c>fixtures/generated/variants</c> are synthetic and refreshed with <c>tests/fixtures/generate-variants.sh</c>.
/// </summary>
[TestFixture]
public sealed class MetadataVariantTests
{
    private static Uri Variant(string name) => new(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "variants", name));

    private static Task<MediaMetadata> Read(string name) => new AtlMediaMetadataReader().ReadAsync(Variant(name));

    private static Task<TrackFileDetails> Details(string name) => new AtlTrackFileDetailsReader().ReadAsync(Variant(name));

    [Test]
    public async Task Read_Id3v24WithUnicodeText_KeepsEveryScriptIntact()
    {
        MediaMetadata metadata = await Read("unicode-id3v24.mp3");

        Assert.That(metadata.Title, Is.EqualTo("Zażółć gęślą jaźń — 日本語 — Привет"));
        Assert.That(metadata.Artist, Is.EqualTo("Björk & Sigur Rós"));
        Assert.That(metadata.Album, Is.EqualTo("Ünïcödé Álbum"));
        Assert.That(metadata.Genre, Is.EqualTo("Électronique"));
        Assert.That(metadata.Year, Is.EqualTo(1999));
        Assert.That(metadata.TrackNumber, Is.EqualTo(3));
        Assert.That((await Details("unicode-id3v24.mp3")).Metadata.TrackTotal, Is.EqualTo(12));
    }

    [Test]
    public async Task Read_FileWithId3v2AndId3v1_ReportsBothVersionsAndReadsTheText()
    {
        MediaMetadata metadata = await Read("id3v23-and-v1.mp3");
        TrackFileDetails details = await Details("id3v23-and-v1.mp3");

        Assert.That(metadata, Has.Property("Title").EqualTo("Both Tags").And.Property("Artist").EqualTo("Tag Duo").And.Property("Year").EqualTo(2001));
        Assert.That(details.TagFormats, Is.EqualTo(Items("ID3v2.3", "ID3v1.0")));
        Assert.That(details.FileWriteRestriction, Is.Null, "an ID3v2 tag with an ID3v1 footer is written together (see VariantWritingTests)");
    }

    [Test]
    public async Task Details_LyricsFrame_IsExposedAsAnAdditionalField()
    {
        TrackFileDetails details = await Details("lyrics.mp3");

        Assert.That(details.AdditionalFields["USLT"], Does.Contain("First line").And.Contain("Second line"));
        Assert.That((await Read("lyrics.mp3")).Title, Is.EqualTo("With Lyrics"));
    }

    [Test]
    public async Task Details_TwoEmbeddedPictures_AreBothListed()
    {
        TrackFileDetails details = await Details("two-pictures.flac");

        Assert.That(details.EmbeddedArtwork, Has.Count.EqualTo(2));
        Assert.That((await Read("two-pictures.flac")).Title, Is.EqualTo("Two Pictures"));
    }

    [Test]
    public async Task Read_RepeatedVorbisField_UsesTheLastValue()
    {
        // ffmpeg wrote ARTIST twice ("One" then "Two"); characterization of today's behavior, not a promise.
        Assert.That((await Read("mono-22k.ogg")).Artist, Is.EqualTo("Two"));
    }

    [Test]
    public async Task Read_FlacWithAlbumArtistDiscAndComposer_FillsTheStructuredFields()
    {
        TrackFileDetails details = await Details("flac-24bit-tags.flac");
        MediaMetadata metadata = await Read("flac-24bit-tags.flac");

        Assert.That(details.BitsPerSample, Is.EqualTo(24));
        Assert.That(metadata, Has.Property("AlbumArtist").EqualTo("Album Artist").And.Property("DiscNumber").EqualTo(2).And.Property("Composer").EqualTo("A. Composer"));
        Assert.That(details.Metadata.DiscTotal, Is.EqualTo(3));
    }

    [TestCase("pcm-u8-mono-8k.wav", 8, 1, 8000)]
    [TestCase("pcm-s24-stereo-48k.wav", 24, 2, 48000)]
    [TestCase("pcm-f32-stereo-44k.wav", 32, 2, 44100)]
    public async Task Read_WavProfiles_ReportTheirStreamParameters(string file, int bits, int channels, int rate)
    {
        MediaMetadata metadata = await Read(file);
        TrackFileDetails details = await Details(file);

        Assert.That(details.BitsPerSample, Is.EqualTo(bits));
        Assert.That(metadata.Channels, Is.EqualTo(channels));
        Assert.That(metadata.SampleRate, Is.EqualTo(rate));
        Assert.That(metadata.Duration, Is.EqualTo(TimeSpan.FromSeconds(file.Contains("u8", StringComparison.Ordinal) ? 0.5 : 0.2)).Within(TimeSpan.FromMilliseconds(30)));
        Assert.That(metadata.Title, Is.Not.Empty, "the RIFF INFO title is read for every profile");
    }

    [Test]
    public async Task Read_ExtensibleAndFloatWavProfiles_AreOfferedForFileWriting()
    {
        Assert.That((await Details("pcm-u8-mono-8k.wav")).FileWriteRestriction, Is.Null);
        Assert.That((await Details("pcm-s24-stereo-48k.wav")).FileWriteRestriction, Is.Null, "WAVE_FORMAT_EXTENSIBLE 24-bit PCM");
        Assert.That((await Details("pcm-f32-stereo-44k.wav")).FileWriteRestriction, Is.Null, "32-bit IEEE float");
    }

    [Test]
    public async Task Chapters_Mp4WithTwoMarkers_AreListedInOrder()
    {
        AudiobookChapterListing listing = await new AtlAudiobookChapterReader().ReadAsync(Variant("chapters.m4b"));

        Assert.That(listing.Chapters.Select(chapter => chapter.Title), Is.EqualTo(Items("Opening", "Second Part")));
        Assert.That(listing.Chapters[0].Start, Is.EqualTo(TimeSpan.Zero));
        Assert.That(listing.Chapters[1].Start, Is.EqualTo(TimeSpan.FromMilliseconds(200)).Within(TimeSpan.FromMilliseconds(5)));
    }

    [Test]
    public async Task Import_WholeVariantFolder_ImportsEveryFileAndKeepsUnicodeThroughTheDatabase()
    {
        string directory = Directory.CreateTempSubdirectory("aibell-variants").FullName;
        try
        {
            string source = Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "variants");
            string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(media, Path.GetFileName(file)));
            await using SqliteTrackRepository repository = new(Path.Combine(directory, "library.db"));
            LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), repository);

            LibraryImportResult result = await importer.ImportAsync([media]);

            Assert.That(result.Issues, Is.Empty);
            // .m4b audiobooks belong to the audiobook shelf, not the music library, so everything else is imported.
            Assert.That(result.Imported, Is.EqualTo(Directory.GetFiles(media).Count(file => !file.EndsWith(".m4b", StringComparison.Ordinal))));
            IReadOnlyList<Track> tracks = await repository.ListAsync();
            Assert.That(tracks.Select(track => track.Title), Does.Contain("Zażółć gęślą jaźń — 日本語 — Привет"));
            Assert.That(tracks.Single(track => track.Title.StartsWith("Zażółć", StringComparison.Ordinal)).Artist, Is.EqualTo("Björk & Sigur Rós"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static T[] Items<T>(params T[] items) => items;
}
