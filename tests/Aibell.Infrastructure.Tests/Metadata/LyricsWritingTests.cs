using System.Security.Cryptography;
using Aibell.Domain.Lyrics;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class LyricsWritingTests
{
    private static readonly string[] Formats = ["flac", "m4a", "ogg", "opus", "mp3"];
    private static readonly string[] SyncedLines = ["Hello wörld", "Second line"];
    private static readonly string[] PlainLines = ["Just words", "in two lines"];
    private const string Synced = "[00:01.50]Hello wörld\n[00:03.00]Second line";
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-lyrics-" + Guid.NewGuid().ToString("N"))).FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    private string Copy(string extension)
    {
        string source = Path.Combine(directory, "fixture." + extension);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), source);
        return source;
    }

    [TestCaseSource(nameof(Formats))]
    public async Task SyncedLyricsWrittenToTheFileAreReadBackAsSyncedAndRestoreReturnsTheOriginal(string extension)
    {
        string source = Copy(extension);
        string original = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        MetadataFileWriter writer = new(new AtlMetadataEditStager());
        var written = await writer.WriteAsync(new Uri(source), new(Lyrics: Synced));
        TrackLyrics? read = await new AtlLyricsFileReader().ReadAsync(new Uri(source));
        Assert.That(read, Is.Not.Null);
        Assert.That(read!.IsSynced, Is.True);
        Assert.That(read.Origin, Is.EqualTo(LyricsOrigin.Embedded));
        Assert.That(read.Lines.Select(line => line.Text), Is.EqualTo(SyncedLines));
        Assert.That(read.Lines[0].Start, Is.EqualTo(TimeSpan.FromSeconds(1.5)));
        await writer.RestoreAsync(written.Backup);
        Assert.That(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))), Is.EqualTo(original));
    }

    [TestCaseSource(nameof(Formats))]
    public async Task PlainLyricsReplaceEarlierOnesAndAnEmptyTextClearsThem(string extension)
    {
        string source = Copy(extension);
        MetadataFileWriter writer = new(new AtlMetadataEditStager());
        await writer.WriteAsync(new Uri(source), new(Lyrics: Synced));
        await writer.WriteAsync(new Uri(source), new(Lyrics: "Just words\r\nin two lines"));
        TrackLyrics? plain = await new AtlLyricsFileReader().ReadAsync(new Uri(source));
        Assert.That(plain!.IsSynced, Is.False);
        Assert.That(plain.Lines.Select(line => line.Text), Is.EqualTo(PlainLines));
        await writer.WriteAsync(new Uri(source), new(Lyrics: ""));
        Assert.That(await new AtlLyricsFileReader().ReadAsync(new Uri(source)), Is.Null);
    }

    [Test]
    public async Task ReaderReturnsNullForAFileWithoutLyricsAMissingFileAndANonFileLocation()
    {
        AtlLyricsFileReader reader = new();
        Assert.That(await reader.ReadAsync(new Uri(Copy("mp3"))), Is.Null);
        Assert.That(await reader.ReadAsync(new Uri(Path.Combine(directory, "missing.mp3"))), Is.Null);
        Assert.That(await reader.ReadAsync(new Uri("http://example.com/a.mp3")), Is.Null);
    }
}
