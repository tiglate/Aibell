using System.Net;
using System.Text;
using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Podcasts;

[TestFixture]
public sealed class HttpPodcastFeedReaderTests
{
    private const string Rss = """
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel><title>Synthetic Podcast</title><description>Feed &amp; description</description>
            <item><guid isPermaLink="false">opaque-guid</guid><title>First &amp; best</title>
              <description>&lt;p&gt;Plain text presentation&lt;/p&gt;</description>
              <pubDate>Thu, 15 Oct 2026 10:20:30 GMT</pubDate><itunes:duration>01:02:03</itunes:duration>
              <enclosure url="https://media.example.invalid/first.mp3" type="audio/mpeg" length="1234" />
            </item>
          </channel>
        </rss>
        """;

    [Test]
    public async Task ReadAsync_RssOverOwnedHttp_MapsMetadataAndRetainsCacheValidators()
    {
        await using LoopbackPodcastServer server = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, Encoding.UTF8.GetBytes(Rss),
            extraHeaders: "ETag: \"version-1\"\r\nLast-Modified: Thu, 15 Oct 2026 10:20:30 GMT\r\n"));
        using HttpClient client = new();
        PodcastFeedResult result = await new HttpPodcastFeedReader(client).ReadAsync(new(server.Address));
        PodcastEpisode episode = result.Feed!.Episodes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Feed.Source, Is.EqualTo(server.Address));
            Assert.That(result.Feed.Title, Is.EqualTo("Synthetic Podcast"));
            Assert.That(result.Feed.Description, Is.EqualTo("Feed & description"));
            Assert.That(episode.Id, Is.EqualTo("opaque-guid"));
            Assert.That(episode.Title, Is.EqualTo("First & best"));
            Assert.That(episode.Enclosure, Is.EqualTo(new Uri("https://media.example.invalid/first.mp3")));
            Assert.That(episode.Length, Is.EqualTo(1234));
            Assert.That(episode.Duration, Is.EqualTo(new TimeSpan(1, 2, 3)));
            Assert.That(episode.Published, Is.EqualTo(new DateTimeOffset(2026, 10, 15, 10, 20, 30, TimeSpan.Zero)));
            Assert.That(result.EntityTag, Is.EqualTo("\"version-1\""));
            Assert.That(result.NotModified, Is.False);
            Assert.That(result.Issues, Is.Empty);
            Assert.That(server.Requests, Is.EqualTo(1));
            Assert.That(server.RequestHeaders.Single(), Does.Contain("User-Agent: Aibell/"));
        });
    }

    [Test]
    public async Task ReadAsync_EpisodeImage_MapsOnlyPerEpisodeHttpCovers()
    {
        const string feed = """
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel><title>Covers</title><itunes:image href="https://cdn.example.invalid/show.jpg" />
                <item><guid>with</guid><title>With cover</title><itunes:image href="https://cdn.example.invalid/ep1.jpg" />
                  <enclosure url="https://media.example.invalid/1.mp3" type="audio/mpeg" /></item>
                <item><guid>relative</guid><title>Relative</title><itunes:image href="art/ep2.png" />
                  <enclosure url="https://media.example.invalid/2.mp3" type="audio/mpeg" /></item>
                <item><guid>bad</guid><title>Unsupported</title><itunes:image href="file:///etc/passwd" />
                  <enclosure url="https://media.example.invalid/3.mp3" type="audio/mpeg" /></item>
                <item><guid>none</guid><title>None</title>
                  <enclosure url="https://media.example.invalid/4.mp3" type="audio/mpeg" /></item>
              </channel>
            </rss>
            """;
        await using LoopbackPodcastServer server = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, Encoding.UTF8.GetBytes(feed)));
        using HttpClient client = new();
        PodcastFeedResult result = await new HttpPodcastFeedReader(client).ReadAsync(new(server.Address));
        Uri?[] images = result.Feed!.Episodes.Select(episode => episode.Image).ToArray();
        Assert.That(images[0], Is.EqualTo(new Uri("https://cdn.example.invalid/ep1.jpg")));
        Assert.That(images[1], Is.EqualTo(new Uri(server.Address, "art/ep2.png")));
        Assert.That(images[2], Is.Null.Or.Property("Scheme").EqualTo("file"), "The service, not the parser, rejects unsupported schemes.");
        Assert.That(images[3], Is.Null, "The show cover is deliberately not used as an episode fallback.");
    }

    [Test]
    public async Task ReadAsync_Atom_ResolvesInheritedXmlBaseAndFiltersNonAudioEnclosures()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://media.example.invalid/shows/">
              <title>Atom Show</title><subtitle>Show description</subtitle>
              <entry xml:base="season-2/"><id>tag:example.invalid,2026:episode-1</id><title>Episode</title>
                <published>2026-10-15T10:20:30Z</published><summary>Summary</summary>
                <link rel="enclosure" type="image/jpeg" href="cover.jpg" />
                <link rel="enclosure" type="audio/ogg" href="episode.ogg" length="456" />
              </entry>
            </feed>
            """;
        PodcastFeedResult result = await ReadAsync(atom);
        PodcastEpisode episode = result.Feed!.Episodes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Feed.Title, Is.EqualTo("Atom Show"));
            Assert.That(result.Feed.Description, Is.EqualTo("Show description"));
            Assert.That(episode.Id, Is.EqualTo("tag:example.invalid,2026:episode-1"));
            Assert.That(episode.Enclosure.AbsoluteUri, Is.EqualTo("https://media.example.invalid/shows/season-2/episode.ogg"));
            Assert.That(episode.Length, Is.EqualTo(456));
            Assert.That(episode.Description, Is.EqualTo("Summary"));
        });
    }

    [Test]
    public async Task ReadAsync_ConditionalNotModified_SendsValidatorsAndDoesNotParseBody()
    {
        DateTimeOffset modified = new(2026, 10, 15, 10, 20, 30, TimeSpan.Zero);
        await using LoopbackPodcastServer server = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [], status: "304 Not Modified"));
        using HttpClient client = new();
        PodcastFeedResult result = await new HttpPodcastFeedReader(client).ReadAsync(new(server.Address, "\"version-1\"", modified));
        Assert.Multiple(() =>
        {
            Assert.That(result.NotModified, Is.True);
            Assert.That(result.Feed, Is.Null);
            Assert.That(result.EntityTag, Is.EqualTo("\"version-1\""));
            Assert.That(result.LastModified, Is.EqualTo(modified));
            Assert.That(server.RequestHeaders.Single(), Does.Contain("If-None-Match: \"version-1\""));
            Assert.That(server.RequestHeaders.Single(), Does.Contain("If-Modified-Since: Thu, 15 Oct 2026 10:20:30 GMT"));
        });
    }

    [Test]
    public async Task ReadAsync_InvalidAndDuplicateEpisodes_ReportsIssuesAndUsesEnclosureAsMissingId()
    {
        const string xml = """
            <rss version="2.0"><channel><title>Show</title>
              <item><enclosure url="https://example.invalid/a.mp3" type="audio/mpeg" /></item>
              <item><enclosure url="https://example.invalid/a.mp3" /></item>
              <item><enclosure url="file:///tmp/private.mp3" type="audio/mpeg" /></item>
              <item><enclosure url="https://user:pass@example.invalid/private.mp3" type="audio/mpeg" /></item>
              <item><enclosure url="https://example.invalid/movie.mp4" type="video/mp4" /></item>
              <item><title>Text only</title></item>
            </channel></rss>
            """;
        PodcastFeedResult result = await ReadAsync(xml);
        Assert.Multiple(() =>
        {
            Assert.That(result.Feed!.Episodes, Has.Count.EqualTo(1));
            Assert.That(result.Feed.Episodes[0].Id, Is.EqualTo("https://example.invalid/a.mp3"));
            Assert.That(result.Feed.Episodes[0].Title, Is.EqualTo("Show"));
            Assert.That(result.Feed.Episodes[0].Length, Is.Null);
            Assert.That(result.Issues, Has.Count.EqualTo(5));
        });
    }

    [TestCase("123", 123)]
    [TestCase("02:03", 123)]
    [TestCase("0:02:03", 123)]
    [TestCase("25:00:00", 90000)]
    [TestCase("00:99:00", null)]
    [TestCase("01:60", null)]
    [TestCase("2147483647:59:59", null)]
    [TestCase("-1", null)]
    [TestCase("not-a-duration", null)]
    public async Task ReadAsync_ItunesDuration_ParsesSupportedFormsAndIgnoresInvalidValues(string duration, int? seconds)
    {
        PodcastFeedResult result = await ReadAsync(Rss.Replace("01:02:03", duration, StringComparison.Ordinal));
        Assert.That(result.Feed!.Episodes.Single().Duration, Is.EqualTo(seconds is { } value ? TimeSpan.FromSeconds(value) : (TimeSpan?)null));
    }

    [TestCase("<rss>", typeof(InvalidDataException))]
    [TestCase("<html><title>Not a feed</title></html>", typeof(NotSupportedException))]
    [TestCase("<rss version=\"2.0\"><channel /></rss>", typeof(InvalidDataException))]
    [TestCase("<rss version=\"2.0\" />", typeof(InvalidDataException))]
    public async Task ReadAsync_InvalidDocument_RejectsIt(string xml, Type error) =>
        await Assert.ThatAsync(async () => await ReadAsync(xml), Throws.TypeOf(error));

    [Test]
    public async Task ReadAsync_Dtd_RejectsBeforeContactingExternalEntity()
    {
        await using LoopbackPodcastServer external = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, Encoding.UTF8.GetBytes("private content")));
        string xml = $"<!DOCTYPE rss [<!ENTITY external SYSTEM '{external.Address}'>]><rss version='2.0'><channel><title>&external;</title></channel></rss>";
        await Assert.ThatAsync(async () => await ReadAsync(xml), Throws.TypeOf<InvalidDataException>());
        Assert.That(external.Requests, Is.Zero);
    }

    [Test]
    public async Task ReadAsync_OversizedDocumentOrEpisodeCount_RejectsIt()
    {
        await Assert.ThatAsync(async () => await ReadAsync(new string(' ', 32 * 1024 * 1024 + 1)), Throws.TypeOf<HttpRequestException>());
        string items = string.Concat(Enumerable.Repeat("<item />", 5001));
        await Assert.ThatAsync(async () => await ReadAsync($"<rss version='2.0'><channel><title>Show</title>{items}</channel></rss>"),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task ReadAsync_CallerCancellationAndOwnTimeout_DistinguishesTheCause()
    {
        using HttpClient client = new(new DelayedHandler());
        HttpPodcastFeedReader reader = new(client, TimeSpan.FromMilliseconds(30));
        await Assert.ThatAsync(async () => await reader.ReadAsync(new(new Uri("https://example.invalid/feed"))), Throws.TypeOf<TimeoutException>());
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThatAsync(async () => await reader.ReadAsync(new(new Uri("https://example.invalid/feed")), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        await Assert.ThatAsync(async () => await reader.ReadAsync(new(new Uri("file:///tmp/feed.xml"))), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task ReadAsync_RedirectedFeed_ResolvesRelativeEnclosureAgainstFinalResource()
    {
        const string xml = "<rss version='2.0'><channel><title>Redirected Show</title><item><enclosure url='episode.mp3' type='audio/mpeg' /></item></channel></rss>";
        await using LoopbackPodcastServer final = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, Encoding.UTF8.GetBytes(xml)));
        await using LoopbackPodcastServer redirect = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [],
            status: "302 Found", extraHeaders: $"Location: {new Uri(final.Address, "shows/feed.xml")}\r\n"));
        using HttpClient client = new();
        PodcastFeedResult result = await new HttpPodcastFeedReader(client).ReadAsync(new(redirect.Address));
        Assert.That(result.Feed!.Source, Is.EqualTo(redirect.Address));
        Assert.That(result.Feed.Episodes.Single().Enclosure, Is.EqualTo(new Uri(final.Address, "shows/episode.mp3")));
        Assert.That(redirect.Requests, Is.EqualTo(1));
        Assert.That(final.Requests, Is.EqualTo(1));
    }

    [Test]
    public async Task ReadAsync_ExistingSyntheticFixtureAndParameterizedMediaType_MapWithoutDownloadingEnclosures()
    {
        string xml = await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "podcast.xml"));
        PodcastFeedResult result = await ReadAsync(xml.Replace("audio/mpeg", "application/ogg; codecs=opus", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(result.Feed!.Title, Is.EqualTo("Aibell Fixture Podcast"));
            Assert.That(result.Feed.Episodes.Single().Id, Is.EqualTo("urn:aibell:fixture:episode-1"));
            Assert.That(result.Feed.Episodes.Single().Length, Is.EqualTo(4096));
            Assert.That(result.Issues, Is.Empty);
        });
    }

    [Test]
    public async Task ReadAsync_HttpClientTimeout_IsReportedAsTimeout()
    {
        using HttpClient client = new(new DelayedHandler()) { Timeout = TimeSpan.FromMilliseconds(30) };
        await Assert.ThatAsync(async () => await new HttpPodcastFeedReader(client).ReadAsync(new(new Uri("https://example.invalid/feed"))),
            Throws.TypeOf<TimeoutException>());
    }

    [TestCase("304 Not Modified")]
    [TestCase("206 Partial Content")]
    public async Task ReadAsync_UnsolicitedNotModifiedOrPartialResponse_RejectsIt(string status)
    {
        await using LoopbackPodcastServer server = new((stream, _) => LoopbackPodcastServer.RespondAsync(stream, [], status: status));
        using HttpClient client = new();
        await Assert.ThatAsync(async () => await new HttpPodcastFeedReader(client).ReadAsync(new(server.Address)), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task ReadAsync_FreshResponseWithoutValidators_ClearsPreviousCacheValidators()
    {
        using HttpClient client = new(new XmlHandler(Rss));
        PodcastFeedResult result = await new HttpPodcastFeedReader(client).ReadAsync(new(new Uri("https://example.invalid/feed"),
            "\"old-version\"", DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Multiple(() =>
        {
            Assert.That(result.NotModified, Is.False);
            Assert.That(result.Feed, Is.Not.Null);
            Assert.That(result.EntityTag, Is.Null);
            Assert.That(result.LastModified, Is.Null);
        });
    }

    [Test]
    public async Task ReadAsync_LargeFeedOfManyEpisodes_IsAccepted()
    {
        // Long-running shows publish multi-megabyte feeds (hundreds of episodes with long show notes).
        string notes = new('x', 12_000);
        StringBuilder feed = new("<rss version=\"2.0\"><channel><title>Big</title>");
        for (int index = 0; index < 700; index++)
            feed.Append(System.Globalization.CultureInfo.InvariantCulture, $"<item><guid>g{index}</guid><title>Episode {index}</title><description>{notes}</description>" +
                $"<enclosure url=\"https://media.example.invalid/{index}.mp3\" type=\"audio/mpeg\" /></item>");
        feed.Append("</channel></rss>");
        Assert.That(feed.Length, Is.GreaterThan(8 * 1024 * 1024));
        PodcastFeedResult result = await ReadAsync(feed.ToString());
        Assert.That(result.Feed!.Episodes, Has.Count.EqualTo(700));
    }

    private static async Task<PodcastFeedResult> ReadAsync(string xml)
    {
        using HttpClient client = new(new XmlHandler(xml));
        return await new HttpPodcastFeedReader(client).ReadAsync(new(new Uri("https://example.invalid/feed.xml")));
    }

    private sealed class XmlHandler(string xml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") });
    }
    private sealed class DelayedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
