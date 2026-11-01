using System.Net;
using Avalonia.Headless.NUnit;
using Aibell.Desktop.Services;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class CoverImageCacheTests
{
    // A valid 1x1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private string directory = null!;

    [SetUp] public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "aibell-covers-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [AvaloniaTest]
    public async Task LoadAsync_DownloadsOnceThenServesMemoryAndDisk()
    {
        StubHandler handler = new(_ => Reply("image/png", Png));
        using CoverImageCache cache = new(new HttpClient(handler), directory);
        Uri url = new("https://cdn.example.invalid/a.png");

        Assert.That(await cache.LoadAsync(url), Is.Not.Null);
        Assert.That(await cache.LoadAsync(url), Is.Not.Null);
        using CoverImageCache restarted = new(new HttpClient(handler), directory);
        Assert.That(await restarted.LoadAsync(url), Is.Not.Null);

        Assert.That(handler.Requests, Is.EqualTo(1), "The second session reads the disk cache.");
    }

    [AvaloniaTest]
    public async Task LoadAsync_OverTheDiskLimit_PrunesTheOldestCoversButKeepsTheNewOne()
    {
        Directory.CreateDirectory(directory);
        string oldest = Path.Combine(directory, "oldest.img"), newer = Path.Combine(directory, "newer.img");
        File.WriteAllBytes(oldest, new byte[600]); File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-3));
        File.WriteAllBytes(newer, new byte[600]); File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));
        StubHandler handler = new(_ => Reply("image/png", Png));
        using CoverImageCache cache = new(new HttpClient(handler), directory, diskLimitBytes: 800);

        Assert.That(await cache.LoadAsync(new("https://cdn.example.invalid/fresh.png")), Is.Not.Null);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(oldest), Is.False, "The oldest file goes first.");
            Assert.That(File.Exists(newer), Is.True, "Pruning stops once the cache fits.");
            Assert.That(Directory.GetFiles(directory, "*.img").Length, Is.EqualTo(2), "The cover just downloaded is kept.");
        });
    }

    [AvaloniaTest]
    public async Task LoadAsync_RejectsNonImagesOversizeAndBadStatus()
    {
        Dictionary<string, Func<HttpResponseMessage>> routes = new()
        {
            ["/html"] = () => Reply("text/html", Png),
            ["/svg"] = () => Reply("image/svg+xml", Png),
            ["/big"] = () => Reply("image/png", new byte[CoverImageCache.MaximumBytes + 1]),
            ["/missing"] = () => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        StubHandler handler = new(request => routes[request.RequestUri!.AbsolutePath]());
        using CoverImageCache cache = new(new HttpClient(handler), directory);

        foreach (string path in routes.Keys)
            Assert.That(await cache.LoadAsync(new("https://cdn.example.invalid" + path)), Is.Null, path);
        Assert.That(await cache.LoadAsync(new("ftp://cdn.example.invalid/a.png")), Is.Null);
        Assert.That(Directory.Exists(directory) ? Directory.GetFiles(directory) : [], Is.Empty, "Rejected downloads leave nothing behind.");
        int before = handler.Requests;
        Assert.That(await cache.LoadAsync(new("https://cdn.example.invalid/html")), Is.Null);
        Assert.That(handler.Requests, Is.EqualTo(before), "A failure is remembered for the session.");
    }

    private static HttpResponseMessage Reply(string type, byte[] body)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new(type);
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int requests;
        public int Requests => requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(respond(request));
        }
    }
}
