using System.Net;
using System.Text.Json.Nodes;
using Aibell.Application.Enrichment;
using NUnit.Framework;

namespace Aibell.Metadata.MusicBrainz.Tests;

[TestFixture]
public sealed class MusicBrainzEnrichmentProviderTests
{
    private const string RecordingSearchJson = """
        {
          "created": "2024-01-01T00:00:00.000Z",
          "count": 1,
          "offset": 0,
          "recordings": [
            {
              "id": "c9b2c34e-96dc-4f56-8f9e-1234567890ab",
              "score": 100,
              "title": "Bohemian Rhapsody",
              "length": 354000,
              "first-release-date": "1975-10-31",
              "artist-credit": [
                {
                  "name": "Queen",
                  "joinphrase": "",
                  "artist": {
                    "id": "0383dadf-2a4e-4d10-a46a-e9e041da8eb3",
                    "name": "Queen"
                  }
                }
              ],
              "releases": [
                {
                  "id": "0c22cf9b-a0e1-4b2a-9f6b-1234567890cd",
                  "title": "A Night at the Opera",
                  "date": "1975-11-21",
                  "release-group": {
                    "id": "f4f5f7b0-1111-2222-3333-444455556666",
                    "title": "A Night at the Opera"
                  }
                }
              ]
            }
          ]
        }
        """;

    private const string EmptyRecordingSearchJson = """
        { "created": "2024-01-01T00:00:00.000Z", "count": 0, "offset": 0, "recordings": [] }
        """;

    private const string CoverArtJson = """
        {
          "images": [
            {
              "image": "https://archive.org/download/mbid-0c22cf9b/front.jpg",
              "front": true,
              "back": false,
              "types": ["Front"],
              "thumbnails": {
                "250": "https://archive.org/download/mbid-0c22cf9b/front-250.jpg",
                "500": "https://archive.org/download/mbid-0c22cf9b/front-500.jpg",
                "1200": "https://archive.org/download/mbid-0c22cf9b/front-1200.jpg"
              }
            }
          ]
        }
        """;

    [Test]
    public async Task LookupAsync_HappyPath_MapsCandidateWithArtworkAndConfidence()
    {
        FakeHttpMessageHandler handler = new((request, _) => Task.FromResult(
            request.RequestUri!.Host == "musicbrainz.org"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, RecordingSearchJson)
                : FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, CoverArtJson)));
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler);

        IReadOnlyList<EnrichmentCandidate> candidates = await provider.LookupAsync(
            new EnrichmentQuery("Bohemian Rhapsody", "Queen"));

        Assert.Multiple(() =>
        {
            Assert.That(candidates, Has.Count.EqualTo(1));
            EnrichmentCandidate candidate = candidates[0];
            Assert.That(candidate.ProviderId, Is.EqualTo("musicbrainz"));
            Assert.That(candidate.RecordId, Is.EqualTo("c9b2c34e-96dc-4f56-8f9e-1234567890ab"));
            Assert.That(candidate.Title, Is.EqualTo("Bohemian Rhapsody"));
            Assert.That(candidate.Artist, Is.EqualTo("Queen"));
            Assert.That(candidate.Album, Is.EqualTo("A Night at the Opera"));
            Assert.That(candidate.Year, Is.EqualTo(1975));
            Assert.That(candidate.Confidence, Is.EqualTo(1.0).Within(0.0001));
            Assert.That(candidate.ArtworkUri, Is.EqualTo(new Uri("https://archive.org/download/mbid-0c22cf9b/front-500.jpg")));
        });
    }

    [Test]
    public async Task LookupAsync_SendsRequiredUserAgentHeader()
    {
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, EmptyRecordingSearchJson)));
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler);

        await provider.LookupAsync(new EnrichmentQuery("Anything", "Anyone"));

        string userAgent = handler.Requests.Single().Headers.UserAgent!.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(userAgent, Does.Contain("Aibell/"));
            Assert.That(userAgent, Does.Contain("github.com/tiglate/Aibell"));
        });
    }

    [Test]
    public async Task LookupAsync_NoResults_ReturnsEmpty()
    {
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, EmptyRecordingSearchJson)));
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler);

        IReadOnlyList<EnrichmentCandidate> candidates = await provider.LookupAsync(
            new EnrichmentQuery("Unknown Song", "Unknown Artist"));

        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public async Task LookupAsync_NonSuccessStatus_ReturnsEmpty()
    {
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler);

        IReadOnlyList<EnrichmentCandidate> candidates = await provider.LookupAsync(new EnrichmentQuery("Title", "Artist"));

        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public async Task LookupAsync_MalformedJson_ReturnsEmpty()
    {
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, "{ this is not valid json")));
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler);

        IReadOnlyList<EnrichmentCandidate> candidates = await provider.LookupAsync(new EnrichmentQuery("Title", "Artist"));

        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public async Task LookupAsync_OwnTimeoutElapses_ReturnsEmptyWithoutThrowing()
    {
        FakeHttpMessageHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, EmptyRecordingSearchJson);
        });
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler, requestTimeout: TimeSpan.FromMilliseconds(30));

        IReadOnlyList<EnrichmentCandidate> candidates = await provider.LookupAsync(new EnrichmentQuery("Title", "Artist"));

        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public async Task LookupAsync_CallerCancellation_Propagates()
    {
        FakeHttpMessageHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, EmptyRecordingSearchJson);
        });
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler);
        using CancellationTokenSource callerCancellation = new(TimeSpan.FromMilliseconds(20));

        await Assert.CatchAsync<OperationCanceledException>(
            async () => await provider.LookupAsync(new EnrichmentQuery("Title", "Artist"), callerCancellation.Token));
    }

    [Test]
    public async Task LookupAsync_CoverArtNotFound_StillReturnsCandidateWithoutArtwork()
    {
        FakeHttpMessageHandler handler = new((request, _) => Task.FromResult(
            request.RequestUri!.Host == "musicbrainz.org"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, RecordingSearchJson)
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler);

        IReadOnlyList<EnrichmentCandidate> candidates = await provider.LookupAsync(
            new EnrichmentQuery("Bohemian Rhapsody", "Queen"));

        Assert.Multiple(() =>
        {
            Assert.That(candidates, Has.Count.EqualTo(1));
            Assert.That(candidates[0].ArtworkUri, Is.Null);
            Assert.That(candidates[0].Title, Is.EqualTo("Bohemian Rhapsody"));
        });
    }

    [Test]
    public async Task LookupAsync_CoverArtMalformedJson_StillReturnsCandidateWithoutArtwork()
    {
        FakeHttpMessageHandler handler = new((request, _) => Task.FromResult(
            request.RequestUri!.Host == "musicbrainz.org"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, RecordingSearchJson)
                : FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, "not json")));
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler);

        IReadOnlyList<EnrichmentCandidate> candidates = await provider.LookupAsync(
            new EnrichmentQuery("Bohemian Rhapsody", "Queen"));

        Assert.Multiple(() =>
        {
            Assert.That(candidates, Has.Count.EqualTo(1));
            Assert.That(candidates[0].ArtworkUri, Is.Null);
        });
    }

    [Test]
    public async Task LookupAsync_ArtworkExcludedByRequest_SkipsCoverArtArchiveCall()
    {
        FakeHttpMessageHandler handler = new((request, _) => Task.FromResult(
            request.RequestUri!.Host == "musicbrainz.org"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, RecordingSearchJson)
                : FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, CoverArtJson)));
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler);

        IReadOnlyList<EnrichmentCandidate> candidates = await provider.LookupAsync(
            new EnrichmentQuery("Bohemian Rhapsody", "Queen", IncludeArtwork: false));

        Assert.Multiple(() =>
        {
            Assert.That(candidates[0].ArtworkUri, Is.Null);
            Assert.That(handler.Requests, Has.Count.EqualTo(1));
            Assert.That(handler.Requests.Single().RequestUri!.Host, Is.EqualTo("musicbrainz.org"));
        });
    }

    [Test]
    public async Task LookupAsync_BlankTitle_ReturnsEmptyWithoutCallingNetwork()
    {
        FakeHttpMessageHandler handler = new((request, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, EmptyRecordingSearchJson)));
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler);

        IReadOnlyList<EnrichmentCandidate> candidates = await provider.LookupAsync(new EnrichmentQuery(" ", "Artist"));

        Assert.Multiple(() =>
        {
            Assert.That(candidates, Is.Empty);
            Assert.That(handler.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task LookupAsync_SearchRequest_TargetsRecordingSearchWithEscapedLuceneQuery()
    {
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, EmptyRecordingSearchJson)));
        MusicBrainzEnrichmentProvider provider = CreateProvider(handler);

        await provider.LookupAsync(new EnrichmentQuery("Twist \"n\" Shout", "The Beatles"));

        string requestUri = handler.Requests.Single().RequestUri!.ToString();
        string expectedQuery = "recording:\"Twist \\\"n\\\" Shout\" AND artist:\"The Beatles\"";
        Assert.Multiple(() =>
        {
            Assert.That(requestUri, Does.StartWith("https://musicbrainz.org/ws/2/recording?query="));
            Assert.That(requestUri, Does.Contain("&limit=5&fmt=json"));
            Assert.That(Uri.UnescapeDataString(requestUri), Does.Contain(expectedQuery));
        });
    }

    [TestCase("not a URI")]
    [TestCase("/relative/front.jpg")]
    [TestCase("file:///tmp/front.jpg")]
    [TestCase("ftp://example.test/front.jpg")]
    [TestCase("data:image/png;base64,AAAA")]
    public async Task LookupAsync_InvalidArtworkUri_PreservesCandidateWithoutArtwork(string artworkUri)
    {
        JsonNode cover = JsonNode.Parse(CoverArtJson)!;
        cover["images"]![0]!["thumbnails"]!["500"] = artworkUri;
        FakeHttpMessageHandler handler = new((request, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                request.RequestUri!.Host == "musicbrainz.org" ? RecordingSearchJson : cover.ToJsonString())));

        IReadOnlyList<EnrichmentCandidate> candidates = await CreateProvider(handler).LookupAsync(
            new EnrichmentQuery("Bohemian Rhapsody", "Queen"));

        Assert.Multiple(() =>
        {
            Assert.That(candidates, Has.Count.EqualTo(1));
            Assert.That(candidates[0].ArtworkUri, Is.Null);
        });
    }

    [Test]
    public async Task LookupAsync_NullRecordings_IgnoresNullEntries()
    {
        JsonNode search = JsonNode.Parse(RecordingSearchJson)!;
        search["recordings"]!.AsArray().Insert(0, null);
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, search.ToJsonString())));

        IReadOnlyList<EnrichmentCandidate> candidates = await CreateProvider(handler).LookupAsync(
            new EnrichmentQuery("Bohemian Rhapsody", "Queen", IncludeArtwork: false));

        Assert.That(candidates, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task LookupAsync_NullNestedEntries_IgnoresNullArtistCreditsReleasesAndImages()
    {
        JsonNode search = JsonNode.Parse(RecordingSearchJson)!;
        search["recordings"]![0]!["artist-credit"]!.AsArray().Insert(0, null);
        search["recordings"]![0]!["releases"]!.AsArray().Insert(0, null);
        JsonNode cover = JsonNode.Parse(CoverArtJson)!;
        cover["images"]!.AsArray().Insert(0, null);
        FakeHttpMessageHandler handler = new((request, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                request.RequestUri!.Host == "musicbrainz.org" ? search.ToJsonString() : cover.ToJsonString())));

        IReadOnlyList<EnrichmentCandidate> candidates = await CreateProvider(handler).LookupAsync(
            new EnrichmentQuery("Bohemian Rhapsody", "Queen"));

        Assert.Multiple(() =>
        {
            Assert.That(candidates, Has.Count.EqualTo(1));
            Assert.That(candidates[0].Artist, Is.EqualTo("Queen"));
            Assert.That(candidates[0].Album, Is.EqualTo("A Night at the Opera"));
            Assert.That(candidates[0].ArtworkUri, Is.Not.Null);
        });
    }

    [Test]
    public async Task LookupAsync_ServerExceedsRequestedLimit_SelectsBestCandidateBeforeArtworkLookup()
    {
        JsonNode search = JsonNode.Parse(RecordingSearchJson)!;
        JsonNode weaker = search["recordings"]![0]!.DeepClone();
        weaker["id"] = "weaker-recording";
        weaker["title"] = "Different song";
        weaker["releases"]![0]!["id"] = "weaker-release";
        search["recordings"]!.AsArray().Insert(0, weaker);
        FakeHttpMessageHandler handler = new((request, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                request.RequestUri!.Host == "musicbrainz.org" ? search.ToJsonString() : CoverArtJson)));

        IReadOnlyList<EnrichmentCandidate> candidates = await CreateProvider(handler).LookupAsync(
            new EnrichmentQuery("Bohemian Rhapsody", "Queen", MaxCandidates: 1));

        Assert.Multiple(() =>
        {
            Assert.That(candidates, Has.Count.EqualTo(1));
            Assert.That(candidates[0].Title, Is.EqualTo("Bohemian Rhapsody"));
            Assert.That(handler.Requests, Has.Count.EqualTo(2));
            Assert.That(handler.Requests[1].RequestUri!.AbsolutePath,
                Is.EqualTo("/release/0c22cf9b-a0e1-4b2a-9f6b-1234567890cd/"));
        });
    }

    [Test]
    public async Task LookupAsync_DisabledProvider_DoesNotContactNetwork()
    {
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, RecordingSearchJson)));
        using HttpClient client = new(handler);
        MusicBrainzEnrichmentProvider provider = new(client, isEnabled: false);

        IReadOnlyList<EnrichmentCandidate> candidates = await provider.LookupAsync(new EnrichmentQuery("Title", "Artist"));

        Assert.Multiple(() =>
        {
            Assert.That(candidates, Is.Empty);
            Assert.That(handler.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task LookupAsync_AlreadyCancelledBlankQuery_PropagatesCancellation()
    {
        FakeHttpMessageHandler handler = new((_, _) => throw new InvalidOperationException("Unexpected network request."));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.CatchAsync<OperationCanceledException>(async () => await CreateProvider(handler).LookupAsync(
            new EnrichmentQuery(null, null), cancellation.Token));
        Assert.That(handler.Requests, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LookupAsync_OversizedResponse_IsRejectedWithoutDiscardingValidSearch(bool artworkResponse)
    {
        string oversized = new(' ', 2 * 1024 * 1024 + 1);
        FakeHttpMessageHandler handler = new((request, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                artworkResponse && request.RequestUri!.Host == "musicbrainz.org" ? RecordingSearchJson : oversized)));

        IReadOnlyList<EnrichmentCandidate> candidates = await CreateProvider(handler).LookupAsync(
            new EnrichmentQuery("Bohemian Rhapsody", "Queen"));

        Assert.That(candidates, Has.Count.EqualTo(artworkResponse ? 1 : 0));
        if (artworkResponse) Assert.That(candidates[0].ArtworkUri, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LookupAsync_OverlongQuery_DoesNotContactNetwork(bool artist)
    {
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, RecordingSearchJson)));
        string oversized = new('a', 1025);
        IReadOnlyList<EnrichmentCandidate> candidates = await CreateProvider(handler).LookupAsync(
            artist ? new EnrichmentQuery("Title", oversized) : new EnrichmentQuery(oversized, "Artist"));
        Assert.That(candidates, Is.Empty);
        Assert.That(handler.Requests, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LookupAsync_OverlongMatchText_DoesNotScoreOrFetchArtwork(bool artist)
    {
        JsonNode search = JsonNode.Parse(RecordingSearchJson)!;
        string oversized = new('a', 1025);
        if (artist) search["recordings"]![0]!["artist-credit"]![0]!["name"] = oversized;
        else search["recordings"]![0]!["title"] = oversized;
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, search.ToJsonString())));
        IReadOnlyList<EnrichmentCandidate> candidates = await CreateProvider(handler).LookupAsync(new EnrichmentQuery("Title", "Artist"));
        Assert.That(candidates, Is.Empty);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task LookupAsync_MaximumMatchLength_StillAllowsAnExactMatch()
    {
        string title = new('a', 1024);
        JsonNode search = JsonNode.Parse(RecordingSearchJson)!;
        search["recordings"]![0]!["title"] = title;
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, search.ToJsonString())));
        IReadOnlyList<EnrichmentCandidate> candidates = await CreateProvider(handler).LookupAsync(
            new EnrichmentQuery(title, "Queen", IncludeArtwork: false));
        Assert.That(candidates.Single().Confidence, Is.EqualTo(1));
    }

    [Test]
    public async Task LookupAsync_ServerReturnsExcessiveRecordings_BoundsScoringWork()
    {
        JsonNode search = JsonNode.Parse(RecordingSearchJson)!;
        JsonArray recordings = search["recordings"]!.AsArray();
        JsonNode best = recordings[0]!.DeepClone();
        recordings.Clear();
        for (int index = 0; index < 100; index++)
        {
            JsonNode weaker = best.DeepClone();
            weaker["id"] = "record-" + index;
            weaker["title"] = "Different song";
            recordings.Add(weaker);
        }
        recordings.Add(best);
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, search.ToJsonString())));
        IReadOnlyList<EnrichmentCandidate> candidates = await CreateProvider(handler).LookupAsync(
            new EnrichmentQuery("Bohemian Rhapsody", "Queen", IncludeArtwork: false, MaxCandidates: 25));
        Assert.That(candidates, Has.Count.EqualTo(25));
        Assert.That(candidates.All(candidate => candidate.Title == "Different song"), Is.True);
    }

    private static MusicBrainzEnrichmentProvider CreateProvider(HttpMessageHandler handler, TimeSpan? requestTimeout = null)
    {
        HttpClient httpClient = new(handler);
        return new MusicBrainzEnrichmentProvider(
            httpClient,
            new TokenBucketRateLimiter(TimeSpan.Zero),
            requestTimeout: requestTimeout);
    }
}
