using System.Security.Cryptography;
using ATL;
using Track = ATL.Track;
using Aibell.Application.Library;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class AtlMetadataEditStagerTests
{
    private static readonly string[] Formats = ["flac", "m4a", "ogg", "opus", "mp3"];
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [TestCaseSource(nameof(Formats))]
    public async Task StageAsync_MetadataEdit_PreservesSourceOtherTagsAndArtworkAndCleansUp(string extension)
    {
        string source = Copy(extension);
        Track seed = new(source);
        seed.AdditionalFields["AIBELL_PRESERVE"] = "Unedited 日本語";
        byte[] image = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "artwork", "cover.png"));
        seed.EmbeddedPictures.Add(PictureInfo.fromBinaryData(image, PictureInfo.PIC_TYPE.Front));
        Assert.That(seed.Save(), Is.True);
        string hash = Hash(source);
        await using IStagedMetadataEdit staged = await new AtlMetadataEditStager().StageAsync(new Uri(source),
            new(Title: "Edited ação 日本語", Genre: "Electronic", Year: 2025, TrackNumber: 2, Comment: "One\nTwo"));
        string stagePath = staged.StagedFile.LocalPath;
        using (FileStream stream = File.OpenRead(stagePath))
        {
            Track read = new(stream, "." + extension);
            Assert.Multiple(() =>
            {
                Assert.That(read.Title, Is.EqualTo("Edited ação 日本語"));
                Assert.That(read.Artist, Is.EqualTo("Aibell Test Runner"));
                Assert.That(read.Album, Is.EqualTo("Synthetic Fixtures"));
                Assert.That(read.AdditionalFields.Values, Does.Contain("Unedited 日本語"));
                Assert.That(read.EmbeddedPictures.Single().PictureData, Is.EqualTo(image));
                Assert.That(staged.SourceHash, Is.EqualTo(hash));
                Assert.That(Hash(source), Is.EqualTo(hash));
            });
        }
        List<Uri> discovered = [];
        await foreach (Uri file in new FileSystemMediaDiscovery().DiscoverAsync([directory])) discovered.Add(file);
        Assert.That(discovered, Is.EqualTo(new[] { new Uri(source) }));
        if (!OperatingSystem.IsWindows())
            Assert.That(File.GetUnixFileMode(stagePath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        await staged.VerifyUnchangedAsync();
        await staged.DisposeAsync();
        await staged.DisposeAsync();
        Assert.That(File.Exists(stagePath), Is.False);
        Assert.That(async () => await staged.VerifyUnchangedAsync(), Throws.TypeOf<ObjectDisposedException>());
        Assert.That(Hash(source), Is.EqualTo(hash));
    }

    [TestCaseSource(nameof(Formats))]
    public async Task StageAsync_ExtendedMetadata_RoundTripsAndPreservesUneditedFields(string extension)
    {
        string source = Copy(extension);
        string originalHash = Hash(source);
        MetadataEdit edit = new(Description: "Description 日本語", AlbumArtist: "Album artist 日本語", Composer: "Composer ação",
            Conductor: "Conductor", Grouping: extension == "m4a" ? "Suite" : null, Copyright: "Copyright 2026",
            SortTitle: extension == "m4a" ? "Title sort" : null, SortArtist: extension == "m4a" ? "Artist sort" : null, SortAlbumArtist: extension == "m4a" ? "Album artist sort" : null,
            SortAlbum: extension == "m4a" ? "Album sort" : null, TrackTotal: 12, DiscNumber: 2, DiscTotal: 3, BeatsPerMinute: 120);
        await using IStagedMetadataEdit staged = await new AtlMetadataEditStager().StageAsync(new Uri(source), edit);
        string preview = Path.Combine(directory, "extended." + extension);
        File.Copy(staged.StagedFile.LocalPath, preview);
        Track read = new(preview);
        Assert.Multiple(() =>
        {
            Assert.That(read.Description, Is.EqualTo(edit.Description));
            Assert.That(read.AlbumArtist, Is.EqualTo(edit.AlbumArtist));
            Assert.That(read.Composer, Is.EqualTo(edit.Composer));
            Assert.That(read.Conductor, Is.EqualTo(edit.Conductor));
            Assert.That(read.Group, Is.EqualTo(edit.Grouping ?? ""));
            Assert.That(read.Copyright, Is.EqualTo(edit.Copyright));
            Assert.That(read.SortTitle, Is.EqualTo(edit.SortTitle ?? ""));
            Assert.That(read.SortArtist, Is.EqualTo(edit.SortArtist ?? ""));
            Assert.That(read.SortAlbumArtist, Is.EqualTo(edit.SortAlbumArtist ?? ""));
            Assert.That(read.SortAlbum, Is.EqualTo(edit.SortAlbum ?? ""));
            Assert.That(read.TrackTotal, Is.EqualTo(12));
            Assert.That(read.DiscNumber, Is.EqualTo(2));
            Assert.That(read.DiscTotal, Is.EqualTo(3));
            Assert.That(read.BPM, Is.EqualTo(120));
            Assert.That(read.Title, Is.EqualTo("Fixture Tone"));
            Assert.That(Hash(source), Is.EqualTo(originalHash));
        });
        await using IStagedMetadataEdit titleOnly = await new AtlMetadataEditStager().StageAsync(new Uri(preview), new(Title: "Only title"));
        using FileStream preserved = File.OpenRead(titleOnly.StagedFile.LocalPath);
        Track unchanged = new(preserved, "." + extension);
        Assert.That(unchanged.Composer, Is.EqualTo(edit.Composer));
        Assert.That(unchanged.SortAlbum, Is.EqualTo(edit.SortAlbum ?? ""));
        Assert.That(unchanged.DiscTotal, Is.EqualTo(3));
        await using IStagedMetadataEdit cleared = await new AtlMetadataEditStager().StageAsync(new Uri(preview),
            new(Description: "", AlbumArtist: "", Composer: "", Conductor: "", Copyright: "",
                Grouping: extension == "m4a" ? "" : null,
                SortTitle: extension == "m4a" ? "" : null,
                SortArtist: extension == "m4a" ? "" : null,
                SortAlbumArtist: extension == "m4a" ? "" : null,
                SortAlbum: extension == "m4a" ? "" : null));
        using FileStream clearedStream = File.OpenRead(cleared.StagedFile.LocalPath);
        Track empty = new(clearedStream, "." + extension);
        Assert.That(empty.Composer, Is.Empty);
        Assert.That(empty.AlbumArtist, Is.Empty);
        Assert.That(empty.DiscTotal, Is.EqualTo(3));
    }

    [TestCase("flac")]
    [TestCase("ogg")]
    [TestCase("opus")]
    public void StageAsync_UnverifiedGroupingAndSorting_RejectsBeforeCreatingPreview(string extension)
    {
        string source = Copy(extension);
        string hash = Hash(source);
        foreach (MetadataEdit edit in new MetadataEdit[] { new(Grouping: "Suite"), new(SortTitle: "Title"),
            new(SortArtist: "Artist"), new(SortAlbumArtist: "Artist"), new(SortAlbum: "Album") })
            Assert.That(async () => await new AtlMetadataEditStager().StageAsync(new Uri(source), edit),
                Throws.TypeOf<NotSupportedException>());
        Assert.That(Hash(source), Is.EqualTo(hash));
        Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { source }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void StageAsync_NumberConflictsWithUneditedTotal_RejectsAndCleansUp(bool disc)
    {
        string source = Copy("flac");
        Track seed = new(source) { TrackNumber = 1, TrackTotal = 2, DiscNumber = 1, DiscTotal = 2 };
        Assert.That(seed.Save(), Is.True);
        string hash = Hash(source);
        MetadataEdit edit = disc ? new(DiscNumber: 3) : new(TrackNumber: 3);
        Assert.That(async () => await new AtlMetadataEditStager().StageAsync(new Uri(source), edit),
            Throws.TypeOf<ArgumentException>());
        Assert.That(Hash(source), Is.EqualTo(hash));
        Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { source }));
    }

    [TestCaseSource(nameof(Formats))]
    public async Task StageAsync_ClearTitle_ValidatesRawEmptyTitleWithoutGlobalSettings(string extension)
    {
        string source = Copy(extension);
        await using IStagedMetadataEdit staged = await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: ""));
        using FileStream stream = File.OpenRead(staged.StagedFile.LocalPath);
        Assert.That(new Track(stream, "." + extension).Title, Is.Empty);
        Assert.That(new Track(source).Title, Is.EqualTo("Fixture Tone"));
    }

    [TestCaseSource(nameof(Formats))]
    public async Task StageAsync_ClearYearAndTrackNumber_ValidatesAbsentAsZero(string extension)
    {
        string source = Copy(extension);
        await using IStagedMetadataEdit staged = await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Year: 0, TrackNumber: 0));
        using FileStream stream = File.OpenRead(staged.StagedFile.LocalPath);
        Track read = new(stream, "." + extension);
        Assert.That(read.Year.GetValueOrDefault(), Is.Zero);
        Assert.That(read.TrackNumber.GetValueOrDefault(), Is.Zero);
    }

    [TestCaseSource(nameof(Formats))]
    [Category("MetadataWriteDecode")]
    public async Task StageAsync_MetadataEdit_PreservesDecodedAudio(string extension)
    {
        if (Environment.GetEnvironmentVariable("AIBELL_METADATA_WRITE_TESTS") != "1") Assert.Ignore("Enable FFmpeg metadata decode tests.");
        string source = Copy(extension);
        string original = await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source);
        await using IStagedMetadataEdit staged = await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "Staged title", AlbumArtist: "Artist", Composer: "Composer", Conductor: "Conductor",
                Copyright: "2026", TrackTotal: 12, DiscNumber: 1, DiscTotal: 2, BeatsPerMinute: 120));
        Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(staged.StagedFile.LocalPath), Is.EqualTo(original));
    }

    [Test]
    public void StageAsync_WriterFails_RemovesStageAndPreservesSource()
    {
        string source = Copy("flac");
        string hash = Hash(source);
        AtlMetadataEditStager stager = new((path, _, _, _) =>
        {
            using FileStream stream = File.OpenWrite(path);
            stream.SetLength(0);
            throw new IOException("Injected write failure.");
        });
        Assert.That(async () => await stager.StageAsync(new Uri(source), new(Title: "Edited")), Throws.TypeOf<IOException>());
        Assert.That(Hash(source), Is.EqualTo(hash));
        Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
        Assert.That(Directory.GetFiles(directory, "*.json"), Is.Empty);
        Assert.That(Directory.GetFiles(directory, "*.lease"), Is.Empty);
    }

    [Test]
    public void StageAsync_CancelledDuringWrite_RemovesStageAndPreservesSource()
    {
        string source = Copy("flac");
        string hash = Hash(source);
        using CancellationTokenSource cancellation = new();
        AtlMetadataEditStager stager = new((path, _, _, token) =>
        {
            using FileStream stream = File.OpenWrite(path);
            stream.SetLength(0);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        });
        Assert.That(async () => await stager.StageAsync(new Uri(source), new(Title: "Edited"), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Hash(source), Is.EqualTo(hash));
        Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
        Assert.That(Directory.GetFiles(directory, "*.json"), Is.Empty);
        Assert.That(Directory.GetFiles(directory, "*.lease"), Is.Empty);
    }

    [Test]
    public void StageAsync_SourceChangedDuringWrite_RejectsWithoutOverwritingExternalChange()
    {
        string source = Copy("flac");
        AtlMetadataEditStager stager = new((_, _, _, _) => File.WriteAllText(source, "external edit"));
        Assert.That(async () => await stager.StageAsync(new Uri(source), new(Title: "Edited")), Throws.TypeOf<IOException>());
        Assert.That(File.ReadAllText(source), Is.EqualTo("external edit"));
        Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task VerifyUnchangedAsync_SourceOrStageModified_Rejects(bool changeSource)
    {
        string source = Copy("flac");
        await using IStagedMetadataEdit staged = await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "Edited"));
        await File.WriteAllTextAsync(changeSource ? source : staged.StagedFile.LocalPath, "external edit");
        Assert.That(async () => await staged.VerifyUnchangedAsync(), Throws.TypeOf<IOException>());
    }

    [TestCase("aiff")]
    public void StageAsync_UnsupportedFormat_RejectsBeforeCreatingStage(string extension)
    {
        string source = Path.Combine(directory, "unsupported." + extension);
        File.WriteAllText(source, "unsupported");
        Assert.That(async () => await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "Edited")),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
    }

    [Test]
    public void StageAsync_MultipleTagFormats_RejectsAndPreservesSource()
    {
        string source = Copy("flac");
        Track tagged = new(source);
        Assert.That(tagged.Save(ATL.AudioData.MetaDataIOFactory.TagType.ID3V2), Is.True);
        Assert.That(new Track(source).MetadataFormats.Count, Is.GreaterThan(1));
        string hash = Hash(source);
        Assert.That(async () => await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "Edited")),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(Hash(source), Is.EqualTo(hash));
        Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
    }

    [Test]
    public void StageAsync_CorruptMedia_RejectsAndRemovesStage()
    {
        string source = Path.Combine(directory, "corrupt.flac");
        File.WriteAllText(source, "corrupt media");
        Assert.That(async () => await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "Edited")),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(File.ReadAllText(source), Is.EqualTo("corrupt media"));
        Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
    }

    [Test]
    public void StageAsync_InvalidEdit_RejectsBeforeCreatingStage()
    {
        string source = Copy("flac");
        foreach (MetadataEdit edit in new[] { new MetadataEdit(), new(Year: 10000), new(TrackNumber: -1), new(Title: "bad\0text"), new(Composer: "bad\0text"), new(TrackTotal: -1),
            new(DiscNumber: -1), new(DiscTotal: -1), new(BeatsPerMinute: float.NaN),
            new(BeatsPerMinute: float.PositiveInfinity), new(BeatsPerMinute: -1),
            new(TrackNumber: 3, TrackTotal: 2), new(DiscNumber: 3, DiscTotal: 2), new(Artwork: new()), new(Artwork: new(0, "invalid")),
            new(Artwork: new(ImageData: [1, 2, 3])), new(Artwork: new(-1, new string('A', 64))) })
            Assert.That(async () => await new AtlMetadataEditStager().StageAsync(new Uri(source), edit), Throws.InstanceOf<ArgumentException>());
        Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
    }

    [Test]
    public void StageAsync_PreCancelled_DoesNotCreateStage()
    {
        string source = Copy("flac");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.That(async () => await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "Edited"), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
    }

    [Test]
    public void StageAsync_RemoteUri_RejectsBeforeCreatingStage()
    {
        Assert.That(async () => await new AtlMetadataEditStager().StageAsync(new Uri("https://example.com/track.flac"), new(Title: "Edited")),
            Throws.InstanceOf<ArgumentException>());
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    [Platform("Linux")]
    public void StageAsync_SymbolicLink_RejectsAndLeavesTargetIntact()
    {
        string source = Copy("flac");
        string hash = Hash(source);
        string link = Path.Combine(directory, "link.flac");
        File.CreateSymbolicLink(link, source);
        Assert.That(async () => await new AtlMetadataEditStager().StageAsync(new Uri(link), new(Title: "Edited")),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(Hash(source), Is.EqualTo(hash));
        Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
    }

    private string Copy(string extension)
    {
        string path = Path.Combine(directory, "fixture." + extension);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), path);
        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
