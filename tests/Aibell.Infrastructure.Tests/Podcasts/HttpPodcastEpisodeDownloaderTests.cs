using System.Net;
using System.Text;
using System.Security.Cryptography;
using Aibell.Application.Podcasts;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Podcasts;

[TestFixture]
public sealed class HttpPodcastEpisodeDownloaderTests
{
    private string directory = null!;
    private Uri destination = null!;
    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-podcast-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        destination = new Uri(Path.Combine(directory, "episode.mp3"));
    }
    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [Test]
    public async Task DownloadAsync_OwnedHttp_WritesExactBytesAndNeverOverwritesExistingFile()
    {
        byte[] audio = await File.ReadAllBytesAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "generated", "fixture.mp3"));
        await using LoopbackPodcastServer server = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, audio, "audio/mpeg"));
        using HttpClient client = new();
        HttpPodcastEpisodeDownloader downloader = new(client);
        PodcastDownloadRequest request = new(server.Address, destination, ExpectedLength: audio.Length);

        PodcastDownloadResult result = await downloader.DownloadAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(new PodcastDownloadResult(destination, audio.Length)));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
            Assert.That(server.Requests, Is.EqualTo(1));
        });
        byte[] bytes = await File.ReadAllBytesAsync(destination.LocalPath);
        Assert.That(SHA256.HashData(bytes), Is.EqualTo(SHA256.HashData(audio)));
        Assert.That((await new AtlMediaMetadataReader().ReadAsync(destination)).Title, Is.EqualTo("Fixture Tone"));
        if (!OperatingSystem.IsWindows())
            Assert.That(File.GetUnixFileMode(destination.LocalPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        await Assert.ThatAsync(async () => await downloader.DownloadAsync(request), Throws.TypeOf<IOException>());
        Assert.That(server.Requests, Is.EqualTo(1));
        Assert.That(await File.ReadAllBytesAsync(destination.LocalPath), Is.EqualTo(audio));
    }

    [Test]
    public async Task DownloadAsync_Progress_ReportsGrowingBytesEndingAtTheFullLength()
    {
        byte[] audio = new byte[300 * 1024];
        await using LoopbackPodcastServer server = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, audio, "audio/mpeg"));
        using HttpClient client = new();
        List<PodcastDownloadProgress> reports = [];
        await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(server.Address, destination, Progress: new ImmediateProgress(reports)));
        Assert.Multiple(() =>
        {
            Assert.That(reports, Is.Not.Empty);
            Assert.That(reports.Select(report => report.Received), Is.Ordered);
            Assert.That(reports[^1], Is.EqualTo(new PodcastDownloadProgress(audio.Length, audio.Length)));
        });
    }

    private sealed class ImmediateProgress(List<PodcastDownloadProgress> sink) : IProgress<PodcastDownloadProgress>
    {
        public void Report(PodcastDownloadProgress value) => sink.Add(value);
    }

    [Test]
    public async Task DownloadAsync_CancelledBody_DeletesOwnedPartialAndAllowsRetry()
    {
        await using LoopbackPodcastServer blocked = new(async (stream, token) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nContent-Length: 4096\r\nConnection: close\r\n\r\na"), token);
            await Task.Delay(Timeout.Infinite, token);
        });
        using HttpClient client = new();
        HttpPodcastEpisodeDownloader downloader = new(client);
        using CancellationTokenSource cancellation = new();
        Task<PodcastDownloadResult> pending = downloader.DownloadAsync(new(blocked.Address, destination), cancellation.Token);
        await UntilAsync(() => blocked.Requests == 1 && Directory.GetFiles(directory, "*.partial").Length == 1);
        cancellation.Cancel();
        await Assert.ThatAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Directory.GetFiles(directory), Is.Empty);
        await using LoopbackPodcastServer retry = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [1, 2, 3], "audio/mpeg"));
        Assert.That((await downloader.DownloadAsync(new(retry.Address, destination))).BytesWritten, Is.EqualTo(3));
    }

    [Test]
    public async Task DownloadAsync_TruncatedTransport_RemovesPartialAndPreservesOtherFiles()
    {
        string sentinel = Path.Combine(directory, "keep.partial");
        await File.WriteAllTextAsync(sentinel, "another operation owns this");
        await using LoopbackPodcastServer server = new((stream, token) => stream.WriteAsync(
            Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nContent-Length: 100\r\nConnection: close\r\n\r\nshort"), token).AsTask());
        using HttpClient client = new();
        await Assert.ThatAsync(async () => await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(server.Address, destination)),
            Throws.InstanceOf<HttpRequestException>().Or.InstanceOf<IOException>());
        Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        Assert.That(await File.ReadAllTextAsync(sentinel), Is.EqualTo("another operation owns this"));
        Assert.That(File.Exists(destination.LocalPath), Is.False);
        Assert.That(server.Requests, Is.EqualTo(3));
    }

    [TestCase("audio/mpeg", "200 OK", 2, null)]
    [TestCase("audio/mpeg", "200 OK", 100, 4)]
    [TestCase("text/html", "200 OK", 100, null)]
    [TestCase("video/mp4", "200 OK", 100, null)]
    [TestCase("audio/mpeg", "206 Partial Content", 100, null)]
    public async Task DownloadAsync_InvalidResponseOrLength_DoesNotPublishAFile(string contentType, string status, long maximumBytes, long? expectedLength)
    {
        await using LoopbackPodcastServer server = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [1, 2, 3], contentType, status));
        using HttpClient client = new();
        await Assert.ThatAsync(async () => await new HttpPodcastEpisodeDownloader(client).DownloadAsync(
            new(server.Address, destination, maximumBytes, expectedLength)), Throws.TypeOf<InvalidDataException>());
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public async Task DownloadAsync_ChunkedBodyExceedsLimit_DoesNotTrustMissingContentLength()
    {
        await using LoopbackPodcastServer server = new((stream, token) => stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n3\r\nabc\r\n3\r\ndef\r\n0\r\n\r\n"), token).AsTask());
        using HttpClient client = new();
        await Assert.ThatAsync(async () => await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(server.Address, destination, MaximumBytes: 4)),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public async Task DownloadAsync_EmptyResponseOrOwnTimeout_LeavesNoFile()
    {
        await using LoopbackPodcastServer empty = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [], "audio/mpeg"));
        using HttpClient client = new();
        await Assert.ThatAsync(async () => await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(empty.Address, destination)),
            Throws.TypeOf<InvalidDataException>());
        await using LoopbackPodcastServer blocked = new(async (_, token) => await Task.Delay(Timeout.Infinite, token));
        await Assert.ThatAsync(async () => await new HttpPodcastEpisodeDownloader(client, TimeSpan.FromMilliseconds(50)).DownloadAsync(new(blocked.Address, destination)),
            Throws.TypeOf<TimeoutException>());
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public async Task DownloadAsync_InvalidInputOrAlreadyCancelled_DoesNotContactServer()
    {
        await using LoopbackPodcastServer server = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [1], "audio/mpeg"));
        using HttpClient client = new();
        HttpPodcastEpisodeDownloader downloader = new(client);
        await Assert.ThatAsync(async () => await downloader.DownloadAsync(new(new Uri("file:///tmp/private.mp3"), destination)), Throws.TypeOf<ArgumentException>());
        await Assert.ThatAsync(async () => await downloader.DownloadAsync(new(server.Address, server.Address)), Throws.TypeOf<ArgumentException>());
        await Assert.ThatAsync(async () => await downloader.DownloadAsync(new(server.Address, destination, MaximumBytes: 0)), Throws.TypeOf<ArgumentException>());
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThatAsync(async () => await downloader.DownloadAsync(new(server.Address, destination), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(server.Requests, Is.Zero);
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DownloadAsync_InterruptedStronglyValidatedTransfer_ResumesOrRestartsWhenRangeIsIgnored(bool ignoreRange)
    {
        int attempt = 0;
        await using LoopbackPodcastServer server = new(async (stream, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nContent-Length: 6\r\nETag: \"version-1\"\r\nConnection: close\r\n\r\nabc"), token);
            else if (ignoreRange)
                await LoopbackPodcastServer.RespondAsync(stream, Encoding.ASCII.GetBytes("abcdef"), "audio/mpeg", extraHeaders: "ETag: \"version-1\"\r\n");
            else
                await LoopbackPodcastServer.RespondAsync(stream, Encoding.ASCII.GetBytes("def"), "audio/mpeg", "206 Partial Content",
                    "Content-Range: bytes 3-5/6\r\nETag: \"version-1\"\r\n");
        });
        using HttpClient client = new();
        PodcastDownloadResult result = await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(server.Address, destination));
        Assert.Multiple(() =>
        {
            Assert.That(result.BytesWritten, Is.EqualTo(6));
            Assert.That(server.Requests, Is.EqualTo(2));
            Assert.That(server.RequestHeaders.Last(), Does.Contain("Range: bytes=3-"));
            Assert.That(server.RequestHeaders.Last(), Does.Contain("If-Range: \"version-1\""));
        });
        Assert.That(await File.ReadAllTextAsync(destination.LocalPath), Is.EqualTo("abcdef"));
    }

    [TestCase(null)]
    [TestCase("W/\"version-1\"")]
    public async Task DownloadAsync_InterruptedWithoutStrongValidator_RestartsWithoutCombiningBytes(string? entityTag)
    {
        int attempt = 0;
        await using LoopbackPodcastServer server = new(async (stream, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nContent-Length: 6\r\n{(entityTag is null ? "" : "ETag: " + entityTag + "\r\n")}Connection: close\r\n\r\nabc"), token);
            else await LoopbackPodcastServer.RespondAsync(stream, Encoding.ASCII.GetBytes("new123"), "audio/mpeg");
        });
        using HttpClient client = new();
        await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(server.Address, destination));
        Assert.That(server.RequestHeaders.Last(), Does.Not.Contain("Range:"));
        Assert.That(await File.ReadAllTextAsync(destination.LocalPath), Is.EqualTo("new123"));
    }

    [TestCase("Content-Range: bytes 2-4/6\r\nETag: \"version-1\"\r\n")]
    [TestCase("Content-Range: bytes 3-5/7\r\nETag: \"version-1\"\r\n")]
    [TestCase("Content-Range: bytes 3-5/6\r\nETag: \"version-2\"\r\n")]
    [TestCase("Content-Range: bytes 3-5/6\r\n")]
    [TestCase("ETag: \"version-1\"\r\n")]
    public async Task DownloadAsync_ResumedResponseHasInvalidRangeOrValidator_RejectsItWithoutPublishing(string headers)
    {
        int attempt = 0;
        await using LoopbackPodcastServer server = new(async (stream, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nContent-Length: 6\r\nETag: \"version-1\"\r\nConnection: close\r\n\r\nabc"), token);
            else await LoopbackPodcastServer.RespondAsync(stream, Encoding.ASCII.GetBytes("def"), "audio/mpeg", "206 Partial Content", headers);
        });
        using HttpClient client = new();
        await Assert.ThatAsync(async () => await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(server.Address, destination)),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(Directory.GetFiles(directory), Is.Empty);
        Assert.That(server.Requests, Is.EqualTo(2));
    }

    [Test]
    public async Task DownloadAsync_TransientStatus_RetriesButPermanentFailureDoesNot()
    {
        int attempt = 0;
        await using LoopbackPodcastServer transient = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [1, 2, 3], "audio/mpeg",
            Interlocked.Increment(ref attempt) == 1 ? "503 Service Unavailable" : "200 OK"));
        using HttpClient client = new();
        await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(transient.Address, destination));
        Assert.That(transient.Requests, Is.EqualTo(2));
        File.Delete(destination.LocalPath);
        await using LoopbackPodcastServer permanent = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [], status: "404 Not Found"));
        await Assert.ThatAsync(async () => await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(permanent.Address, destination)),
            Throws.TypeOf<HttpRequestException>());
        Assert.That(permanent.Requests, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public async Task DownloadAsync_RedirectTargetChangesDuringResume_DoesNotCombineDifferentResources()
    {
        await using LoopbackPodcastServer original = new((stream, token) => stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nContent-Length: 6\r\nETag: \"version-1\"\r\nConnection: close\r\n\r\nabc"), token).AsTask());
        await using LoopbackPodcastServer changed = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream,
            Encoding.ASCII.GetBytes("xyz"), "audio/mpeg", "206 Partial Content", "ETag: \"version-1\"\r\nContent-Range: bytes 3-5/6\r\n"));
        int attempt = 0;
        await using LoopbackPodcastServer redirect = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [], status: "302 Found",
            extraHeaders: $"Location: {(Interlocked.Increment(ref attempt) == 1 ? original.Address : changed.Address)}\r\n"));
        using HttpClient client = new();
        await Assert.ThatAsync(async () => await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(redirect.Address, destination)),
            Throws.TypeOf<InvalidDataException>());
        Assert.Multiple(() =>
        {
            Assert.That(original.Requests, Is.EqualTo(1));
            Assert.That(changed.Requests, Is.EqualTo(1));
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        });
    }

    [Test]
    public async Task DownloadAsync_RetryAfterLongDeferral_ReturnsFailureWithoutEarlyRetry()
    {
        await using LoopbackPodcastServer server = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [],
            status: "503 Service Unavailable", extraHeaders: "Retry-After: 600\r\n"));
        using HttpClient client = new();
        await Assert.ThatAsync(async () => await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(server.Address, destination))
            .WaitAsync(TimeSpan.FromSeconds(2)), Throws.TypeOf<HttpRequestException>());
        Assert.That(server.Requests, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public async Task DownloadAsync_RetryAfterShortDelay_DoesNotRetryEarly()
    {
        int attempt = 0;
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan first = default, second = default;
        await using LoopbackPodcastServer server = new((stream, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                first = clock.Elapsed;
                return LoopbackPodcastServer.RespondAsync(stream, [], status: "503 Service Unavailable", extraHeaders: "Retry-After: 1\r\n");
            }
            second = clock.Elapsed;
            return LoopbackPodcastServer.RespondAsync(stream, [1, 2, 3], "audio/mpeg");
        });
        using HttpClient client = new();
        await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(server.Address, destination));
        Assert.That(second - first, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(950)));
    }

    [Test]
    public async Task DownloadAsync_CancelDuringRetryWait_DoesNotMakeAnotherRequest()
    {
        await using LoopbackPodcastServer server = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [],
            status: "503 Service Unavailable", extraHeaders: "Retry-After: 4\r\n"));
        using HttpClient client = new();
        using CancellationTokenSource cancellation = new();
        Task<PodcastDownloadResult> request = new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(server.Address, destination), cancellation.Token);
        await UntilAsync(() => server.Requests == 1);
        cancellation.Cancel();
        await Assert.ThatAsync(async () => await request.WaitAsync(TimeSpan.FromSeconds(2)), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(server.Requests, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public async Task DownloadAsync_HttpClientTimeout_IsReportedAsTimeoutAndCleansTemporaryFile()
    {
        await using LoopbackPodcastServer server = new(async (_, token) => await Task.Delay(Timeout.Infinite, token));
        using HttpClient client = new() { Timeout = TimeSpan.FromMilliseconds(50) };
        await Assert.ThatAsync(async () => await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(server.Address, destination)),
            Throws.TypeOf<TimeoutException>());
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public async Task DownloadAsync_DestinationCreatedDuringTransfer_DoesNotReplaceIt()
    {
        await using LoopbackPodcastServer server = new(async (stream, token) =>
        {
            await File.WriteAllTextAsync(destination.LocalPath, "Created by another operation", token);
            await LoopbackPodcastServer.RespondAsync(stream, [1, 2, 3], "audio/mpeg");
        });
        using HttpClient client = new();
        await Assert.ThatAsync(async () => await new HttpPodcastEpisodeDownloader(client).DownloadAsync(new(server.Address, destination)),
            Throws.TypeOf<IOException>());
        Assert.That(await File.ReadAllTextAsync(destination.LocalPath), Is.EqualTo("Created by another operation"));
        Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
}
