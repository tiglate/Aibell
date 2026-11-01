using System.Security.Cryptography;
using ATL;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class LocalArtworkCacheTests
{
    private static readonly string[] EmbeddedFormats = ["flac", "mp3", "ogg", "opus", "wav"];
    private string directory = null!;
    private string cacheDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-artwork-{Guid.NewGuid():N}");
        cacheDirectory = Path.Combine(directory, "cache");
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [TestCaseSource(nameof(EmbeddedFormats))]
    public async Task GetAsync_EmbeddedFrontCover_ReadsArtworkWithoutChangingAudio(string extension)
    {
        string path = CopyMedia(extension);
        byte[] front = Image("cover.png");
        byte[] back = Image("back.png");
        Track track = new(path);
        track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(back, PictureInfo.PIC_TYPE.Back));
        track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(front, PictureInfo.PIC_TYPE.Front));
        Assert.That(extension == "wav"
            ? track.Save(ATL.AudioData.MetaDataIOFactory.TagType.ID3V2)
            : track.Save(), Is.True);
        byte[] originalHash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        using LocalArtworkCache cache = new(cacheDirectory);

        Uri? artwork = await cache.GetAsync(new Uri(path));

        Assert.That(artwork, Is.Not.Null);
        Assert.That(await File.ReadAllBytesAsync(artwork!.LocalPath), Is.EqualTo(front));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(path)), Is.EqualTo(originalHash));
        Assert.That(new Track(path).DurationMs, Is.GreaterThan(0));
    }

    [Test]
    public async Task GetAsync_M4aGenericCover_ReadsFirstEmbeddedImage()
    {
        string path = CopyMedia("m4a");
        Track track = new(path);
        byte[] front = Image("cover.png");
        track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(front, PictureInfo.PIC_TYPE.Front));
        Assert.That(track.Save(), Is.True);
        byte[] originalHash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        using LocalArtworkCache cache = new(cacheDirectory);

        Uri? artwork = await cache.GetAsync(new Uri(path));

        Assert.That(artwork, Is.Not.Null);
        Assert.That(await File.ReadAllBytesAsync(artwork!.LocalPath), Is.EqualTo(front));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(path)), Is.EqualTo(originalHash));
    }

    [Test]
    public async Task GetAsync_EmbeddedArtwork_PrefersItToFolderCover()
    {
        string path = CopyMedia("flac");
        Track track = new(path);
        track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(Image("cover.png"), PictureInfo.PIC_TYPE.Back));
        Assert.That(track.Save(), Is.True);
        await File.WriteAllBytesAsync(Path.Combine(directory, "cover.png"), Image("back.png"));
        using LocalArtworkCache cache = new(cacheDirectory);

        Uri? artwork = await cache.GetAsync(new Uri(path));

        Assert.That(await File.ReadAllBytesAsync(artwork!.LocalPath), Is.EqualTo(Image("cover.png")));
    }

    [Test]
    public async Task GetAsync_FolderCovers_UsesCaseInsensitivePriorityAndDeduplicatesAcrossTracks()
    {
        string first = CopyMedia("flac");
        string second = CopyMedia("mp3");
        byte[] cover = Image("cover.png");
        await File.WriteAllBytesAsync(Path.Combine(directory, "COVER.PNG"), cover);
        await File.WriteAllBytesAsync(Path.Combine(directory, "folder.png"), Image("back.png"));
        using LocalArtworkCache cache = new(cacheDirectory);

        Uri? firstArtwork = await cache.GetAsync(new Uri(first));
        Uri? secondArtwork = await cache.GetAsync(new Uri(second));

        Assert.That(firstArtwork, Is.EqualTo(secondArtwork));
        Assert.That(await File.ReadAllBytesAsync(firstArtwork!.LocalPath), Is.EqualTo(cover));
        Assert.That(Directory.GetFiles(cacheDirectory, "*.image"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task GetAsync_ReopenedCacheAndCorruptCachedImage_ReusesThenRepairsContent()
    {
        string path = CopyMedia("flac");
        byte[] cover = Image("cover.png");
        await File.WriteAllBytesAsync(Path.Combine(directory, "cover.png"), cover);
        Uri? initial;
        using (LocalArtworkCache cache = new(cacheDirectory))
        {
            initial = await cache.GetAsync(new Uri(path));
        }
        DateTime written = File.GetLastWriteTimeUtc(initial!.LocalPath);
        using LocalArtworkCache reopened = new(cacheDirectory);
        Assert.That(await reopened.GetAsync(new Uri(path)), Is.EqualTo(initial));
        Assert.That(File.GetLastWriteTimeUtc(initial.LocalPath), Is.EqualTo(written));
        await File.WriteAllTextAsync(initial.LocalPath, "damaged cache");

        Assert.That(await reopened.GetAsync(new Uri(path)), Is.EqualTo(initial));
        Assert.That(await File.ReadAllBytesAsync(initial.LocalPath), Is.EqualTo(cover));
        Assert.That(Directory.GetFiles(cacheDirectory, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task GetAsync_MissingAddedChangedAndRemovedCover_InvalidatesPositiveAndNegativeCache()
    {
        string path = CopyMedia("flac");
        string coverPath = Path.Combine(directory, "cover.png");
        using LocalArtworkCache cache = new(cacheDirectory);
        Assert.That(await cache.GetAsync(new Uri(path)), Is.Null);
        await File.WriteAllBytesAsync(coverPath, Image("cover.png"));
        Uri? initial = await cache.GetAsync(new Uri(path));
        Assert.That(initial, Is.Not.Null);

        await File.WriteAllBytesAsync(coverPath, Image("back.png"));
        File.SetLastWriteTimeUtc(coverPath, DateTime.UtcNow.AddSeconds(5));
        Uri? changed = await cache.GetAsync(new Uri(path));
        Assert.That(changed, Is.Not.EqualTo(initial));
        Assert.That(await File.ReadAllBytesAsync(changed!.LocalPath), Is.EqualTo(Image("back.png")));
        File.Delete(coverPath);
        Assert.That(await cache.GetAsync(new Uri(path)), Is.Null);
    }

    [Test]
    public async Task GetAsync_ChangedEmbeddedCover_InvalidatesMediaFingerprint()
    {
        string path = CopyMedia("flac");
        Track track = new(path);
        track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(Image("cover.png"), PictureInfo.PIC_TYPE.Front));
        Assert.That(track.Save(), Is.True);
        using LocalArtworkCache cache = new(cacheDirectory);
        Uri? initial = await cache.GetAsync(new Uri(path));
        track = new Track(path);
        track.EmbeddedPictures.Clear();
        track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(Image("back.png"), PictureInfo.PIC_TYPE.Front));
        Assert.That(track.Save(), Is.True);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        Uri? changed = await cache.GetAsync(new Uri(path));

        Assert.That(changed, Is.Not.EqualTo(initial));
        Assert.That(await File.ReadAllBytesAsync(changed!.LocalPath), Is.EqualTo(Image("back.png")));
    }

    [Test]
    public async Task GetAsync_InvalidAndOversizedSidecars_SkipsToValidFolderCover()
    {
        string path = CopyMedia("flac");
        await File.WriteAllTextAsync(Path.Combine(directory, "cover.jpg"), "invalid image");
        using (FileStream oversized = File.Create(Path.Combine(directory, "cover.png")))
        {
            oversized.SetLength(16 * 1024 * 1024 + 1);
        }
        await File.WriteAllBytesAsync(Path.Combine(directory, "folder.png"), Image("back.png"));
        using LocalArtworkCache cache = new(cacheDirectory);

        Uri? artwork = await cache.GetAsync(new Uri(path));

        Assert.That(await File.ReadAllBytesAsync(artwork!.LocalPath), Is.EqualTo(Image("back.png")));
    }

    [Test]
    public async Task GetAsync_MalformedOrUnsafeIndex_RebuildsWithoutFollowingCachePath()
    {
        string path = CopyMedia("flac");
        await File.WriteAllBytesAsync(Path.Combine(directory, "cover.png"), Image("cover.png"));
        using LocalArtworkCache cache = new(cacheDirectory);
        Uri? artwork = await cache.GetAsync(new Uri(path));
        string index = Directory.GetFiles(cacheDirectory, "*.json").Single();
        await File.WriteAllTextAsync(index, "{bad json");
        Assert.That(await cache.GetAsync(new Uri(path)), Is.EqualTo(artwork));
        string validJson = await File.ReadAllTextAsync(index);
        await File.WriteAllTextAsync(index, validJson.Replace(Path.GetFileNameWithoutExtension(artwork!.LocalPath), "../../outside", StringComparison.Ordinal));
        Assert.That(await cache.GetAsync(new Uri(path)), Is.EqualTo(artwork));
    }

    [Test]
    public async Task GetAsync_MissingOrRemoteMedia_ReturnsNoArtwork()
    {
        using LocalArtworkCache cache = new(cacheDirectory);
        Assert.That(await cache.GetAsync(new Uri(Path.Combine(directory, "missing.flac"))), Is.Null);
        Assert.That(await cache.GetAsync(new Uri("https://example.com/radio")), Is.Null);
        Assert.That(Directory.Exists(cacheDirectory), Is.False);
    }

    [Test]
    public void GetAsync_CancelledRequest_DoesNotCreateCache()
    {
        string path = CopyMedia("flac");
        using LocalArtworkCache cache = new(cacheDirectory);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.That(async () => await cache.GetAsync(new Uri(path), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Directory.Exists(cacheDirectory), Is.False);
    }

    private string CopyMedia(string extension)
    {
        string path = Path.Combine(directory, $"fixture.{extension}");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", $"fixture.{extension}"), path);
        return path;
    }

    private static byte[] Image(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "artwork", name));
}
