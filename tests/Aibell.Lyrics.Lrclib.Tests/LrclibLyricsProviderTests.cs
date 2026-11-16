using NUnit.Framework;
using System.Net;
using System.Text;
using Aibell.Application.Lyrics;

namespace Aibell.Lyrics.Lrclib.Tests;

[TestFixture]
public sealed class LrclibLyricsProviderTests
{
    private const string Record = """{"id":7,"trackName":"Deutschland","artistName":"Rammstein","albumName":"Rammstein","duration":323.4,"instrumental":false,"plainLyrics":"a\nb","syncedLyrics":"[00:01.00]a\n[00:02.50]b"}""";

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await responder(request).ConfigureAwait(false);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (LrclibLyricsProvider Provider, Handler Handler) Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        Handler handler = new(responder);
        return (new LrclibLyricsProvider(new HttpClient(handler)), handler);
    }

    private static readonly LyricsQuery Query = new("Deutschland", "Rammstein", "Rammstein", TimeSpan.FromSeconds(323.4));

    [Test]
    public async Task FindSendsTheTrackDetailsAndAnIdentifyingUserAgent()
    {
        (LrclibLyricsProvider provider, Handler handler) = Create(_ => Task.FromResult(Json(HttpStatusCode.OK, Record)));
        LyricsCandidate? candidate = await provider.FindAsync(Query);
        Uri uri = handler.Requests.Single().RequestUri!;
        Assert.That(uri.AbsolutePath, Is.EqualTo("/api/get"));
        Assert.That(uri.Query, Does.Contain("track_name=Deutschland").And.Contain("artist_name=Rammstein").And.Contain("album_name=Rammstein").And.Contain("duration=323"));
        Assert.That(handler.Requests.Single().Headers.UserAgent.ToString(), Does.StartWith("Aibell/"));
        Assert.That(candidate, Is.Not.Null);
        Assert.That(candidate!.ExternalId, Is.EqualTo("7"));
        Assert.That(candidate.HasSyncedText, Is.True);
        Assert.That(candidate.PlainText, Is.EqualTo("a\nb"));
    }

    [Test]
    public async Task FindReturnsNullForNotFoundAndOtherErrors()
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
        {
            (LrclibLyricsProvider provider, _) = Create(_ => Task.FromResult(Json(status, """{"name":"TrackNotFound"}""")));
            Assert.That(await provider.FindAsync(Query), Is.Null, status.ToString());
        }
    }

    [Test]
    public async Task FindReturnsNullWhenOfflineOrMalformed()
    {
        (LrclibLyricsProvider offline, _) = Create(_ => throw new HttpRequestException("offline"));
        Assert.That(await offline.FindAsync(Query), Is.Null);
        (LrclibLyricsProvider malformed, _) = Create(_ => Task.FromResult(Json(HttpStatusCode.OK, "{not json")));
        Assert.That(await malformed.FindAsync(Query), Is.Null);
        (LrclibLyricsProvider notJson, _) = Create(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello", Encoding.UTF8, "text/plain") }));
        Assert.That(await notJson.FindAsync(Query), Is.Null);
    }

    [Test]
    public async Task FindDoesNotCallTheNetworkWithoutTitleAndArtist()
    {
        (LrclibLyricsProvider provider, Handler handler) = Create(_ => Task.FromResult(Json(HttpStatusCode.OK, Record)));
        Assert.That(await provider.FindAsync(new LyricsQuery("", "Rammstein", "", TimeSpan.Zero)), Is.Null);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task FindOmitsAnOutOfRangeDurationAndTheEmptyAlbum()
    {
        (LrclibLyricsProvider provider, Handler handler) = Create(_ => Task.FromResult(Json(HttpStatusCode.NotFound, "{}")));
        await provider.FindAsync(new LyricsQuery("A b", "C", "", TimeSpan.Zero));
        Assert.That(handler.Requests.Single().RequestUri!.Query, Does.Not.Contain("duration").And.Not.Contain("album_name").And.Contain("track_name=A%20b"));
    }

    [Test]
    public async Task CallerCancellationPropagatesButAServerTimeoutIsNoResult()
    {
        using CancellationTokenSource cancelled = new();
        (LrclibLyricsProvider provider, _) = Create(async _ => { await cancelled.CancelAsync(); cancelled.Token.ThrowIfCancellationRequested(); return Json(HttpStatusCode.OK, Record); });
        Assert.That(async () => await provider.FindAsync(Query, cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
        (LrclibLyricsProvider timedOut, _) = Create(_ => throw new TaskCanceledException("timeout"));
        Assert.That(await timedOut.FindAsync(Query), Is.Null);
    }

    [Test]
    public async Task SearchReturnsEveryRecordAndInstrumentalFlag()
    {
        string json = "[" + Record + ",{\"id\":8,\"trackName\":\"X\",\"artistName\":\"Y\",\"albumName\":null,\"duration\":10,\"instrumental\":true,\"plainLyrics\":null,\"syncedLyrics\":null}]";
        (LrclibLyricsProvider provider, Handler handler) = Create(_ => Task.FromResult(Json(HttpStatusCode.OK, json)));
        IReadOnlyList<LyricsCandidate> results = await provider.SearchAsync(new LyricsQuery("Deutschland", "Rammstein", "", TimeSpan.Zero));
        Assert.That(handler.Requests.Single().RequestUri!.AbsolutePath, Is.EqualTo("/api/search"));
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[1].IsInstrumental, Is.True);
        Assert.That(results[1].Album, Is.Empty);
    }

    [Test]
    public async Task SearchWithOnlyOneFieldUsesTheFreeTextQueryAndEmptyInputMakesNoRequest()
    {
        (LrclibLyricsProvider provider, Handler handler) = Create(_ => Task.FromResult(Json(HttpStatusCode.OK, "[]")));
        Assert.That(await provider.SearchAsync(new LyricsQuery("", "Rammstein", "", TimeSpan.Zero)), Is.Empty);
        Assert.That(handler.Requests.Single().RequestUri!.Query, Does.Contain("q=Rammstein"));
        Assert.That(await provider.SearchAsync(new LyricsQuery("", "", "", TimeSpan.Zero)), Is.Empty);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public void PluginDeclaresItsProviderAndWhatItSends()
    {
        LrclibPlugin plugin = new();
        Assert.That(plugin.ApiVersion, Is.EqualTo(Aibell.Application.Plugins.PluginApi.Version));
        Assert.That(plugin.Description, Does.Contain("lrclib.net").And.Contain("title"));
        Assert.That(plugin.Start(new Aibell.Application.Plugins.PluginContext("x")).LyricsProviders, Has.Count.EqualTo(1));
    }
}
