using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;
using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;

namespace Aibell.Infrastructure.Podcasts;

public sealed class HttpPodcastFeedReader(HttpClient client, TimeSpan? requestTimeout = null) : IPodcastFeedReader
{
    private const long MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumEpisodes = 5000;
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private readonly TimeSpan timeout = requestTimeout ?? TimeSpan.FromSeconds(60);

    public async Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!PodcastLocation.IsSupported(request.Source)) throw new ArgumentException("Use a complete HTTP or HTTPS feed URL without credentials or a fragment.", nameof(request));
        using HttpRequestMessage message = new(HttpMethod.Get, request.Source);
        message.Headers.UserAgent.ParseAdd("Aibell/0.1 (https://github.com/tiglate/Aibell)");
        message.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml, text/xml");
        if (request.EntityTag is not null) message.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(request.EntityTag));
        message.Headers.IfModifiedSince = request.ModifiedSince;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            string? entityTag = response.Headers.ETag?.ToString() ?? request.EntityTag;
            DateTimeOffset? lastModified = response.Content.Headers.LastModified ?? request.ModifiedSince;
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (request.EntityTag is null && request.ModifiedSince is null)
                    throw new InvalidDataException("The server returned no feed data for an unconditional request.");
                return new(null, [], NotModified: true, EntityTag: entityTag, LastModified: lastModified);
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("A complete podcast feed response is required.");
            await response.Content.LoadIntoBufferAsync(MaximumBytes, deadline.Token).ConfigureAwait(false);
            await using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
            XDocument document = await XDocument.LoadAsync(reader, LoadOptions.None, deadline.Token).ConfigureAwait(false);
            Uri effectiveSource = response.RequestMessage?.RequestUri ?? request.Source;
            PodcastFeedResult result = Parse(document, request.Source, effectiveSource, deadline.Token);
            cancellationToken.ThrowIfCancellationRequested();
            return result with { EntityTag = response.Headers.ETag?.ToString(), LastModified = response.Content.Headers.LastModified };
        }
        catch (XmlException exception) { throw new InvalidDataException("The podcast feed is not supported well-formed XML. DTDs are prohibited.", exception); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The podcast feed request timed out.", exception); }
    }

    private static PodcastFeedResult Parse(XDocument document, Uri source, Uri effectiveSource, CancellationToken cancellationToken)
    {
        XElement root = document.Root ?? throw new InvalidDataException("The podcast feed has no root element.");
        bool atom = root.Name == Atom + "feed";
        XElement channel = atom ? root : root.Name == "rss" && (string?)root.Attribute("version") == "2.0"
            ? root.Element("channel") ?? throw new InvalidDataException("The RSS channel is missing.")
            : throw new NotSupportedException("Only RSS 2.0 and Atom podcast feeds are supported.");
        XNamespace ns = atom ? Atom : XNamespace.None;
        string title = Text(channel.Element(ns + "title"));
        if (title.Length == 0) throw new InvalidDataException("The podcast feed has no title.");
        List<PodcastEpisode> episodes = [];
        List<PodcastFeedIssue> issues = [];
        HashSet<string> identifiers = new(StringComparer.Ordinal);
        int entry = 0;
        foreach (XElement item in channel.Elements(ns + (atom ? "entry" : "item")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entry > MaximumEpisodes) throw new InvalidDataException("The podcast feed exceeds the 5,000-entry limit.");
            IEnumerable<XElement> enclosures = atom
                ? item.Elements(Atom + "link").Where(link => (string?)link.Attribute("rel") == "enclosure")
                : item.Elements("enclosure");
            var media = enclosures.Select(enclosure => new
            {
                Location = Resolve(enclosure, atom ? "href" : "url", effectiveSource),
                Type = ((string?)enclosure.Attribute("type"))?.Trim(),
                Length = ParseLength((string?)enclosure.Attribute("length"))
            }).FirstOrDefault(enclosure => PodcastLocation.IsSupported(enclosure.Location) && IsAudioType(enclosure.Type));
            if (media is null)
            {
                issues.Add(new(entry, "No supported HTTP or HTTPS audio enclosure was found."));
                continue;
            }
            string id = Text(item.Element(ns + (atom ? "id" : "guid")));
            if (id.Length == 0) id = media.Location!.AbsoluteUri;
            if (!identifiers.Add(id)) { issues.Add(new(entry, "A duplicate episode identifier was skipped.")); continue; }
            string episodeTitle = Text(item.Element(ns + "title"));
            string? date = (string?)item.Element(ns + (atom ? "published" : "pubDate"));
            if (atom && string.IsNullOrWhiteSpace(date)) date = (string?)item.Element(Atom + "updated");
            DateTimeOffset? published = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) ? parsed : null;
            episodes.Add(new(id, episodeTitle.Length == 0 ? title : episodeTitle, media.Location!, media.Type,
                media.Length, published, ParseDuration((string?)item.Element(Itunes + "duration")),
                Text(item.Element(ns + (atom ? "summary" : "description"))),
                item.Element(Itunes + "image") is { } image ? Resolve(image, "href", effectiveSource) : null));
        }
        return new(new PodcastFeed(source, title, Text(channel.Element(ns + (atom ? "subtitle" : "description"))), episodes), issues);
    }

    internal static bool IsAudioType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return true;
        if (!MediaTypeHeaderValue.TryParse(type, out MediaTypeHeaderValue? parsed)) return false;
        string? mediaType = parsed.MediaType;
        return mediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(mediaType, "application/ogg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static string Text(XElement? element) => element?.Value.Trim() ?? "";
    private static long? ParseLength(string? text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long length) && length >= 0 ? length : null;

    private static Uri? Resolve(XElement enclosure, string attribute, Uri source)
    {
        Uri basis = source;
        foreach (XElement ancestor in enclosure.AncestorsAndSelf().Reverse())
        {
            string? xmlBase = (string?)ancestor.Attribute(XNamespace.Xml + "base");
            if (xmlBase is not null && !Uri.TryCreate(basis, xmlBase, out basis!)) return null;
        }
        string? value = (string?)enclosure.Attribute(attribute);
        return !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(basis, value, out Uri? resolved) ? resolved : null;
    }

    private static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return null;
        long seconds = 0;
        for (int index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
                index > 0 && value > 59) return null;
            seconds = seconds * 60 + value;
        }
        return seconds <= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
    }
}
