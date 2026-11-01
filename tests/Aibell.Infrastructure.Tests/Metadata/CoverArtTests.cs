using System.Net;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class CoverArtTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1];
    private string folder = "";

    [SetUp]
    public void CreateFolder() => folder = Directory.CreateTempSubdirectory("aibell-cover").FullName;

    [TearDown]
    public void DeleteFolder() => Directory.Delete(folder, recursive: true);

    private Uri Track(string name = "song.mp3") => new(Path.Combine(folder, name));

    [Test]
    public async Task Save_Jpeg_WritesCoverJpgBesideTheTrackAndLeavesNoTemporaryFile()
    {
        string path = await new AlbumCoverFolderWriter().SaveAsync(Track(), Jpeg);

        Assert.That(path, Is.EqualTo(Path.Combine(folder, "cover.jpg")));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(Jpeg));
        Assert.That(Directory.GetFiles(folder).Select(Path.GetFileName), Is.EqualTo(Items("cover.jpg")));
    }

    [Test]
    public async Task Save_Png_UsesThePngExtension() =>
        Assert.That(Path.GetFileName(await new AlbumCoverFolderWriter().SaveAsync(Track(), Png)), Is.EqualTo("cover.png"));

    [TestCase("cover.jpg")]
    [TestCase("cover.png")]
    [TestCase("folder.jpg")]
    [TestCase("front.jpeg")]
    public async Task Save_ExistingCoverOfAnyKnownName_IsNeverReplaced(string existing)
    {
        string existingPath = Path.Combine(folder, existing);
        await File.WriteAllBytesAsync(existingPath, [1, 2, 3]);

        await Assert.ThatAsync(() => new AlbumCoverFolderWriter().SaveAsync(Track(), Jpeg),
            Throws.InstanceOf<IOException>().With.Message.Contains(existing));

        Assert.That(File.ReadAllBytes(existingPath), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(Directory.GetFiles(folder), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Save_RejectsNonImagesRemoteTracksAndMissingFolders()
    {
        AlbumCoverFolderWriter writer = new();
        Assert.That(() => writer.SaveAsync(Track(), [1, 2, 3]), Throws.ArgumentException);
        Assert.That(() => writer.SaveAsync(new Uri("cdda:///dev/sr0?track=1"), Jpeg), Throws.ArgumentException);
        await Assert.ThatAsync(() => writer.SaveAsync(new Uri(Path.Combine(folder, "gone", "x.mp3")), Jpeg), Throws.InstanceOf<DirectoryNotFoundException>());
    }

    [Test]
    public async Task Download_Image_ReturnsTheBytes()
    {
        CoverArtDownloader downloader = new(Client(_ => Reply(Jpeg)));
        Assert.That(await downloader.DownloadAsync(new Uri("https://example.test/cover.jpg")), Is.EqualTo(Jpeg));
    }

    [Test]
    public void Download_NotAWebAddress_IsRefusedWithoutAnyRequest()
    {
        int calls = 0;
        CoverArtDownloader downloader = new(Client(_ => { calls++; return Reply(Jpeg); }));

        Assert.That(() => downloader.DownloadAsync(new Uri("ftp://example.test/cover.jpg")), Throws.ArgumentException);
        Assert.That(() => downloader.DownloadAsync(new Uri("file:///etc/passwd")), Throws.ArgumentException);
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public async Task Download_PlainHttpAddress_IsUpgradedToHttps()
    {
        List<Uri> requested = [];
        CoverArtDownloader downloader = new(Client(request => { requested.Add(request.RequestUri!); return Reply(Jpeg); }));

        await downloader.DownloadAsync(new Uri("http://coverartarchive.org/release/x/1-500.jpg"));

        Assert.That(requested.Select(uri => uri.AbsoluteUri), Is.EqualTo(Items("https://coverartarchive.org/release/x/1-500.jpg")));
    }

    [Test]
    public async Task Download_NotFound_FailsAtOnceWithoutRetrying()
    {
        int calls = 0;
        CoverArtDownloader downloader = new(Client(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.NotFound); }), TimeSpan.Zero);

        await Assert.ThatAsync(() => downloader.DownloadAsync(new Uri("https://example.test/a.jpg")), Throws.InstanceOf<IOException>().With.Message.Contains("404"));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Download_BusyServer_IsRetriedAndThenSucceeds()
    {
        int calls = 0;
        CoverArtDownloader downloader = new(Client(_ => ++calls < 3 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Reply(Jpeg)), TimeSpan.Zero);

        Assert.That(await downloader.DownloadAsync(new Uri("https://example.test/a.jpg")), Is.EqualTo(Jpeg));
        Assert.That(calls, Is.EqualTo(3));
    }

    [Test]
    public async Task Download_ServerStaysBusy_GivesUpAfterThreeAttemptsWithAFriendlyMessage()
    {
        int calls = 0;
        CoverArtDownloader downloader = new(Client(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.InternalServerError); }), TimeSpan.Zero);

        await Assert.ThatAsync(() => downloader.DownloadAsync(new Uri("https://example.test/a.jpg")),
            Throws.InstanceOf<IOException>().With.Message.Contains("500").And.Message.Contains("try again"));
        Assert.That(calls, Is.EqualTo(3));
    }

    [Test]
    public async Task Download_NotAnImage_IsRejected() =>
        await Assert.ThatAsync(() => new CoverArtDownloader(Client(_ => Reply("<html>sorry</html>"u8.ToArray()))).DownloadAsync(new Uri("https://example.test/a.jpg")),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("PNG or JPEG"));

    [Test]
    public async Task Download_TooLarge_IsRejectedWhetherOrNotTheServerDeclaresTheSize()
    {
        byte[] huge = new byte[ArtworkChange.MaximumImageBytes + 1];
        Array.Copy(Jpeg, huge, Jpeg.Length);

        await Assert.ThatAsync(() => new CoverArtDownloader(Client(_ => Reply(huge))).DownloadAsync(new Uri("https://example.test/a.jpg")),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("too large"));
        await Assert.ThatAsync(() => new CoverArtDownloader(Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UndeclaredLengthContent(huge) })).DownloadAsync(new Uri("https://example.test/a.jpg")),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("too large"));
    }

    [Test]
    public async Task Download_CallerCancels_Propagates()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThatAsync(() => new CoverArtDownloader(Client(_ => Reply(Jpeg))).DownloadAsync(new Uri("https://example.test/a.jpg"), cancelled.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Download_NetworkFailure_BecomesAnIoError() =>
        await Assert.ThatAsync(() => new CoverArtDownloader(Client(_ => throw new HttpRequestException("no route")), TimeSpan.Zero).DownloadAsync(new Uri("https://example.test/a.jpg")),
            Throws.InstanceOf<IOException>().With.Message.Contains("no route"));

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new StubHandler(respond));

    private static HttpResponseMessage Reply(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static T[] Items<T>(params T[] items) => items;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>A body whose length is unknown up front, like a chunked response.</summary>
    private sealed class UndeclaredLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
