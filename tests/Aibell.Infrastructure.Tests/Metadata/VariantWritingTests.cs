using System.Text;
using Aibell.Application.Library;
using Aibell.Domain.Audiobooks;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

/// <summary>
/// File writing across the tag and format variants in <c>fixtures/generated/variants</c> (see <see cref="MetadataVariantTests"/> for reading):
/// each case edits a disposable copy through the real writer, checks that the audio and everything not edited survives, and restores the
/// original bytes from the backup.
/// </summary>
[TestFixture]
public sealed class VariantWritingTests
{
    private static readonly string[] ChapterTitles = ["Opening", "Second Part"];

    private string directory = "";

    [SetUp]
    public void Create() => directory = Directory.CreateTempSubdirectory("aibell-variant-writing").FullName;

    [TearDown]
    public void Remove() => Directory.Delete(directory, recursive: true);

    private Uri Copy(string name, string? asName = null)
    {
        string target = Path.Combine(directory, asName ?? name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "variants", name), target);
        return new Uri(target);
    }

    private static MetadataFileWriter Writer() => new(new AtlMetadataEditStager());

    private static Task<TrackFileDetails> Details(Uri location) => new AtlTrackFileDetailsReader().ReadAsync(location);

    private static async Task RestoreAndExpectOriginal(MetadataFileWriter writer, Uri location, byte[] original)
    {
        IReadOnlyList<MetadataFileBackup> backups = await writer.GetBackupsAsync(location);
        Assert.That(backups, Has.Count.EqualTo(1));
        await writer.RestoreAsync(backups[0]);
        Assert.That(File.ReadAllBytes(location.LocalPath), Is.EqualTo(original), "restoring the backup returns the original bytes");
    }

    [TestCase("pcm-s24-stereo-48k.wav")]
    [TestCase("pcm-f32-stereo-44k.wav")]
    public async Task Write_ExtensibleAndFloatWav_ChangesOnlyTheInfoList(string file)
    {
        Uri location = Copy(file);
        byte[] original = File.ReadAllBytes(location.LocalPath);
        string audioBefore;
        using (FileStream before = File.OpenRead(location.LocalPath)) audioBefore = RiffWaveInfo.Read(before).OtherChunksHash;
        MetadataFileWriter writer = Writer();

        await writer.WriteAsync(location, new MetadataEdit(Title: "Edited Hi-Res", Artist: "New Artist", Year: 2024, TrackNumber: 4));

        using (FileStream after = File.OpenRead(location.LocalPath))
            Assert.That(RiffWaveInfo.Read(after).OtherChunksHash, Is.EqualTo(audioBefore), "format, data and every other chunk are byte-identical");
        MediaMetadata metadata = await new AtlMediaMetadataReader().ReadAsync(location);
        Assert.That(metadata, Has.Property("Title").EqualTo("Edited Hi-Res").And.Property("Artist").EqualTo("New Artist").And.Property("Year").EqualTo(2024).And.Property("TrackNumber").EqualTo(4));
        await RestoreAndExpectOriginal(writer, location, original);
    }

    [Test]
    public async Task Write_WavWithAnInvalidExtensibleHeader_StaysLibraryOnly()
    {
        Uri location = Copy("pcm-s24-stereo-48k.wav");
        byte[] bytes = File.ReadAllBytes(location.LocalPath);
        int format = IndexOf(bytes, "fmt "u8) + 8;
        bytes[format + 26] = 0x7F; // corrupt the sub-format GUID
        File.WriteAllBytes(location.LocalPath, bytes);

        Assert.That((await Details(location)).FileWriteRestriction, Is.Not.Null);
        await Assert.ThatAsync(() => Writer().WriteAsync(location, new MetadataEdit(Title: "No")), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public async Task Write_Id3v2WithId3v1Footer_KeepsBothTagsInStepAndTheAudioIntact()
    {
        Uri location = Copy("id3v23-and-v1.mp3");
        byte[] original = File.ReadAllBytes(location.LocalPath);
        string audioBefore;
        using (FileStream before = File.OpenRead(location.LocalPath)) audioBefore = MpegAudioData.Read(before).Hash;
        MetadataFileWriter writer = Writer();

        await writer.WriteAsync(location, new MetadataEdit(Title: "A title that is much longer than thirty characters", Album: "Fresh Album"));

        byte[] written = File.ReadAllBytes(location.LocalPath);
        Assert.That(Encoding.Latin1.GetString(written, written.Length - 128, 3), Is.EqualTo("TAG"), "the ID3v1 footer is still the last 128 bytes");
        Assert.That(Encoding.Latin1.GetString(written, written.Length - 125, 30).TrimEnd('\0'), Is.EqualTo("A title that is much longer th"), "ID3v1 holds the first 30 characters");
        using (FileStream after = File.OpenRead(location.LocalPath))
        {
            MpegAudioData saved = MpegAudioData.Read(after);
            Assert.That(saved.Hash, Is.EqualTo(audioBefore));
            Assert.That(saved.HasId3v1);
        }
        MediaMetadata metadata = await new AtlMediaMetadataReader().ReadAsync(location);
        Assert.That(metadata, Has.Property("Title").EqualTo("A title that is much longer than thirty characters").And.Property("Album").EqualTo("Fresh Album").And.Property("Artist").EqualTo("Tag Duo"));
        await RestoreAndExpectOriginal(writer, location, original);
    }

    [Test]
    public void ReadAudio_FooterThatIsNotPlainId3v1_StaysRejected()
    {
        Uri location = Copy("id3v23-and-v1.mp3");
        byte[] bytes = File.ReadAllBytes(location.LocalPath);
        byte[] extended = [.. bytes[..^128], .. "TAG+"u8, .. new byte[223], .. bytes[^128..]];
        using MemoryStream stream = new(extended);

        Assert.Throws<NotSupportedException>(() => MpegAudioData.Read(stream));
    }

    [Test]
    public async Task Write_Mp3WithLyrics_PreservesTheLyricsText()
    {
        Uri location = Copy("lyrics.mp3");
        MetadataFileWriter writer = Writer();
        byte[] original = File.ReadAllBytes(location.LocalPath);

        await writer.WriteAsync(location, new MetadataEdit(Title: "Lyrics Edited"));

        TrackFileDetails details = await Details(location);
        Assert.That(details.Metadata.Title, Is.EqualTo("Lyrics Edited"));
        using (FileStream stream = File.OpenRead(location.LocalPath))
        {
            ATL.Track track = new(stream, ".mp3");
            Assert.That(track.Lyrics.Select(lyrics => lyrics.UnsynchronizedLyrics), Does.Contain("First line\nSecond line").Or.Contain("First line\r\nSecond line"));
        }
        // The read handle must be closed first: Windows cannot replace a file that is still open.
        await RestoreAndExpectOriginal(writer, location, original);
    }

    [TestCase("chapters.m4b", ".m4b")]
    [TestCase("chapters.m4b", ".m4a")]
    public async Task Write_Mp4WithChapters_KeepsTheChapterTable(string file, string extension)
    {
        Uri location = Copy(file, "book" + extension);
        byte[] original = File.ReadAllBytes(location.LocalPath);
        MetadataFileWriter writer = Writer();

        await writer.WriteAsync(location, new MetadataEdit(Title: "Retitled Book", Grouping: "Series"));

        AudiobookChapterListing listing = await new Aibell.Infrastructure.Audiobooks.AtlAudiobookChapterReader().ReadAsync(location);
        Assert.That(listing.Chapters.Select(chapter => chapter.Title), Is.EqualTo(ChapterTitles));
        Assert.That((await new AtlMediaMetadataReader().ReadAsync(location)).Title, Is.EqualTo("Retitled Book"));
        await RestoreAndExpectOriginal(writer, location, original);
    }

    [TestCase("flac-24bit-tags.flac")]
    [TestCase("two-pictures.flac")]
    [TestCase("mono-22k.ogg")]
    [TestCase("unicode-id3v24.mp3")]
    [TestCase("pcm-u8-mono-8k.wav")]
    public async Task Write_EveryOtherVariant_RoundTripsThroughBackupRestore(string file)
    {
        Uri location = Copy(file);
        byte[] original = File.ReadAllBytes(location.LocalPath);
        MetadataFileWriter writer = Writer();
        Assert.That((await Details(location)).FileWriteRestriction, Is.Null);

        await writer.WriteAsync(location, new MetadataEdit(Title: "Probe Title Ünï", Comment: "probe"));

        Assert.That((await new AtlMediaMetadataReader().ReadAsync(location)).Title, Is.EqualTo("Probe Title Ünï"));
        await RestoreAndExpectOriginal(writer, location, original);
    }

    [TestCase("pcm-s24-stereo-48k.wav")]
    [TestCase("pcm-f32-stereo-44k.wav")]
    [TestCase("id3v23-and-v1.mp3")]
    [TestCase("lyrics.mp3")]
    [TestCase("chapters.m4b")]
    [Category("MetadataWriteDecode")]
    public async Task Write_NewlyWritableVariants_DecodeToIdenticalAudio(string file)
    {
        if (Environment.GetEnvironmentVariable("AIBELL_METADATA_WRITE_TESTS") != "1") Assert.Ignore("Enable FFmpeg metadata decode tests.");
        Uri location = Copy(file);
        string before = await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(location.LocalPath);

        await Writer().WriteAsync(location, new MetadataEdit(Title: "Decoded Check", Artist: "Decoded Artist"));

        Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(location.LocalPath), Is.EqualTo(before));
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().IndexOf(pattern);
}
