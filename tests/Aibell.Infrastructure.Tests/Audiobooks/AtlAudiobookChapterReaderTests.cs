using System.Security.Cryptography;
using Aibell.Domain.Audiobooks;
using Aibell.Infrastructure.Audiobooks;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Audiobooks;

[TestFixture]
public sealed class AtlAudiobookChapterReaderTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-chapters-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [TestCase("fixture.mp3")]
    [TestCase("fixture.m4a")]
    public async Task ReadAsync_RealFileWithChapters_ReturnsThemWithoutWritingTheFile(string fixture)
    {
        string path = Prepare(fixture, ("Opening", 0), ("Middle", 500), ("Closing", 900));
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));
        DateTime modified = File.GetLastWriteTimeUtc(path);

        AudiobookChapterListing listing = await new AtlAudiobookChapterReader().ReadAsync(new Uri(path));

        Assert.That(listing.Duration, Is.GreaterThan(TimeSpan.FromMilliseconds(950)), "the generated fixtures are about one second long");
        Assert.That(listing.Chapters.Select(c => (c.Title, c.Start.TotalMilliseconds)),
            Is.EqualTo(new[] { ("Opening", 0.0), ("Middle", 500.0), ("Closing", 900.0) }));
        Assert.That(SHA256.HashData(File.ReadAllBytes(path)), Is.EqualTo(before));
        Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(modified));
    }

    [Test]
    public async Task ReadAsync_M4bFileWithMp4Chapters_ReturnsThemWithoutWritingTheFile()
    {
        string original = Prepare("fixture.m4a", ("Opening", 0), ("Middle", 500));
        string path = Path.ChangeExtension(original, ".m4b");
        File.Move(original, path);
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));

        AudiobookChapterListing listing = await new AtlAudiobookChapterReader().ReadAsync(new Uri(path));

        Assert.That(listing.Chapters.Select(chapter => (chapter.Title, chapter.Start.TotalMilliseconds)),
            Is.EqualTo(new[] { ("Opening", 0.0), ("Middle", 500.0) }));
        Assert.That(listing.Duration, Is.GreaterThan(TimeSpan.FromMilliseconds(950)));
        Assert.That(SHA256.HashData(File.ReadAllBytes(path)), Is.EqualTo(before));
    }

    [Test]
    public async Task ReadAsync_FileWithoutChapters_ReturnsEmptyList()
    {
        string path = Prepare("fixture.mp3");

        AudiobookChapterListing listing = await new AtlAudiobookChapterReader().ReadAsync(new Uri(path));

        Assert.That(listing.Chapters, Is.Empty);
        Assert.That(listing.Duration, Is.GreaterThan(TimeSpan.Zero));
    }

    [Test]
    public async Task ReadAsync_ThenNormalize_DropsMarkersBeyondTheRealDuration()
    {
        string path = Prepare("fixture.mp3", ("Start", 0), ("Far past the end", 3_600_000));

        AudiobookChapterCatalog catalog = AudiobookChapterRules.Normalize(await new AtlAudiobookChapterReader().ReadAsync(new Uri(path)));

        Assert.That(catalog.Chapters.Select(c => c.Title), Is.EqualTo(["Start"]));
        Assert.That(catalog.Skipped, Is.EqualTo(1));
    }

    [Test]
    public void ReadAsync_AlreadyCancelled_ThrowsWithoutReading()
    {
        string path = Prepare("fixture.mp3", ("A", 0));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Assert.That(async () => await new AtlAudiobookChapterReader().ReadAsync(new Uri(path), cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void ReadAsync_CorruptOrMissingOrRemoteInput_IsRejected()
    {
        AtlAudiobookChapterReader reader = new();
        string corrupt = Path.Combine(directory, "corrupt.mp3");
        File.Copy(Fixture("corrupt.mp3"), corrupt);

        Assert.That(async () => await reader.ReadAsync(new Uri(corrupt)), Throws.TypeOf<InvalidDataException>());
        Assert.That(async () => await reader.ReadAsync(new Uri(Path.Combine(directory, "missing.mp3"))), Throws.TypeOf<FileNotFoundException>());
        Assert.That(() => reader.ReadAsync(new Uri("http://example.invalid/book.mp3")), Throws.ArgumentException);
        Assert.That(() => reader.ReadAsync(new Uri(Path.Combine(directory, "book.txt"))), Throws.ArgumentException);
    }

    private string Prepare(string fixture, params (string Title, uint StartMs)[] chapters)
    {
        string path = Path.Combine(directory, fixture);
        File.Copy(Fixture("generated", fixture), path);
        if (chapters.Length == 0) return path;
        ATL.Track track = new(path);
        foreach ((string title, uint start) in chapters) track.Chapters.Add(new ATL.ChapterInfo { Title = title, StartTime = start });
        Assert.That(track.Save(), Is.True, "the owned fixture copy must accept chapters");
        return path;
    }

    private static string Fixture(params string[] segments) => segments.Aggregate(Path.Combine(AppContext.BaseDirectory, "fixtures"), Path.Combine);
}
