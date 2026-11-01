using System.Net;
using Aibell.Application.Discs;
using Aibell.Application.Enrichment;
using NUnit.Framework;

namespace Aibell.Metadata.MusicBrainz.Tests;

[TestFixture]
public sealed class MusicBrainzLookupProviderTests
{
    private const string DiscIdValue = "49HHV7Eb8UKF3aQiNmu1GR8vKTY-";
    private static readonly DiscToc Toc = new([150, 15363, 32314, 46592, 63414, 80489], 95462);

    private const string DiscJson = """
        { "id": "49HHV7Eb8UKF3aQiNmu1GR8vKTY-", "releases": [ {
          "id": "rel-1", "title": "The Album", "date": "1999-05-04",
          "artist-credit": [ { "name": "The Band", "joinphrase": "" } ],
          "genres": [ { "name": "rock", "count": 3 }, { "name": "pop", "count": 9 } ],
          "media": [
            { "position": 1, "discs": [ { "id": "other" } ], "tracks": [ { "position": 1, "title": "Wrong disc", "length": 1000 } ] },
            { "position": 2, "discs": [ { "id": "49HHV7Eb8UKF3aQiNmu1GR8vKTY-" } ], "tracks": [
              { "position": 1, "title": "One", "length": 180000 },
              { "position": 2, "title": "Two", "length": 200000, "artist-credit": [ { "name": "Guest" } ] } ] } ] } ] }
        """;

    private const string CoverJson = """{ "images": [ { "image": "https://example.test/front.jpg", "front": true, "back": false, "types": ["Front"] } ] }""";

    private static (MusicBrainzLookupProvider Provider, FakeHttpMessageHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        FakeHttpMessageHandler handler = new((request, _) => Task.FromResult(respond(request)));
        MusicBrainzClient client = new(new HttpClient(handler), new NoDelay(), new Uri("https://mb.test/ws/2/"), new Uri("https://caa.test/"));
        return (new MusicBrainzLookupProvider(client), handler);
    }

    [Test]
    public async Task LookupDiscAsync_KnownDisc_ReturnsReleaseWithTracksGenreAndCover()
    {
        (MusicBrainzLookupProvider provider, FakeHttpMessageHandler handler) = Create(request =>
            request.RequestUri!.Host == "caa.test" ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, CoverJson)
            : FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, DiscJson));

        IReadOnlyList<ReleaseCandidate> found = await provider.LookupDiscAsync(Toc);

        Assert.That(found, Has.Count.EqualTo(1));
        ReleaseCandidate release = found[0];
        Assert.That(release.Title, Is.EqualTo("The Album"));
        Assert.That(release.Artist, Is.EqualTo("The Band"));
        Assert.That(release.Year, Is.EqualTo(1999));
        Assert.That(release.Genre, Is.EqualTo("pop"));
        Assert.That(release.Confidence, Is.EqualTo(1.0));
        Assert.That(release.ArtworkUri, Is.EqualTo(new Uri("https://example.test/front.jpg")));
        Assert.That(release.Tracks.Select(track => track.Title), Is.EqualTo(Items("One", "Two")));
        Assert.That(release.Tracks[1].Artist, Is.EqualTo("Guest"));
        Assert.That(release.Tracks[0].Duration, Is.EqualTo(TimeSpan.FromSeconds(180)));
        Assert.That(handler.Requests[0].RequestUri!.AbsoluteUri, Does.StartWith("https://mb.test/ws/2/discid/" + DiscIdValue));
        Assert.That(handler.Requests[0].Headers.UserAgent.ToString(), Does.Contain("Aibell"));
    }

    [Test]
    public async Task LookupDiscAsync_UnknownDisc_FallsBackToFuzzyTocSearchWithLowerConfidence()
    {
        (MusicBrainzLookupProvider provider, FakeHttpMessageHandler handler) = Create(request =>
            request.RequestUri!.AbsolutePath.Contains(DiscIdValue, StringComparison.Ordinal) ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.NotFound, "{}")
            : request.RequestUri.Host == "caa.test" ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.NotFound, "{}")
            : FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, DiscJson));

        IReadOnlyList<ReleaseCandidate> found = await provider.LookupDiscAsync(Toc);

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Confidence, Is.LessThan(1.0));
        Assert.That(found[0].ArtworkUri, Is.Null);
        Assert.That(handler.Requests[1].RequestUri!.AbsoluteUri, Does.Contain("discid/-?toc=1+6+95462+150"));
    }

    [Test]
    public async Task LookupDiscAsync_ServerError_ReturnsEmpty()
    {
        (MusicBrainzLookupProvider provider, _) = Create(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.ServiceUnavailable, "{}"));
        Assert.That(await provider.LookupDiscAsync(Toc), Is.Empty);
    }

    [Test]
    public async Task LookupDiscAsync_MalformedJson_ReturnsEmpty()
    {
        (MusicBrainzLookupProvider provider, _) = Create(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, "{ not json"));
        Assert.That(await provider.LookupDiscAsync(Toc), Is.Empty);
    }

    [Test]
    public async Task LookupDiscAsync_CallerCancels_Propagates()
    {
        (MusicBrainzLookupProvider provider, _) = Create(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, DiscJson));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThatAsync(() => provider.LookupDiscAsync(Toc, cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task LookupAlbumAsync_Match_FetchesTrackDetailsForBestRelease()
    {
        (MusicBrainzLookupProvider provider, FakeHttpMessageHandler handler) = Create(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "caa.test") return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.NotFound, "{}");
            if (path.EndsWith("/release", StringComparison.Ordinal))
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{ "releases": [ { "id": "rel-1", "title": "The Album", "artist-credit": [ { "name": "The Band" } ] } ] }""");
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """
                { "id": "rel-1", "title": "The Album", "date": "1999", "artist-credit": [ { "name": "The Band" } ],
                  "release-group": { "genres": [ { "name": "rock", "count": 2 } ] },
                  "media": [ { "position": 1, "tracks": [ { "position": 1, "title": "One" } ] } ] }
                """);
        });

        IReadOnlyList<ReleaseCandidate> found = await provider.LookupAlbumAsync("The Album", "The Band");

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Genre, Is.EqualTo("rock"));
        Assert.That(found[0].Tracks, Has.Count.EqualTo(1));
        Assert.That(handler.Requests[0].RequestUri!.Query, Does.Contain("release%3A%22The%20Album%22"));
    }

    [Test]
    public async Task LookupAlbumAsync_WithoutTracks_SkipsDetailRequestsAndStillFindsCovers()
    {
        (MusicBrainzLookupProvider provider, FakeHttpMessageHandler handler) = Create(request =>
        {
            if (request.RequestUri!.Host == "caa.test") return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, CoverJson);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """
                { "releases": [ { "id": "r1", "title": "The Album", "artist-credit": [ { "name": "The Band" } ] },
                                { "id": "r2", "title": "The Album", "date": "2004", "artist-credit": [ { "name": "The Band" } ] },
                                { "id": "r3", "title": "The Album", "artist-credit": [ { "name": "The Band" } ] } ] }
                """);
        });

        IReadOnlyList<ReleaseCandidate> found = await provider.LookupAlbumAsync("The Album", "The Band", includeTracks: false);

        Assert.That(found, Has.Count.EqualTo(3));
        Assert.That(found.All(release => release.ArtworkUri == new Uri("https://example.test/front.jpg")), Is.True);
        Assert.That(found.All(release => release.Tracks.Count == 0), Is.True);
        Assert.That(handler.Requests.Count(request => request.RequestUri!.Host == "mb.test"), Is.EqualTo(1), "only the search, no per-release detail");
    }

    [Test]
    public async Task LookupAlbumAsync_BlankAlbum_StaysOffline()
    {
        (MusicBrainzLookupProvider provider, FakeHttpMessageHandler handler) = Create(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, "{}"));
        Assert.That(await provider.LookupAlbumAsync("  ", null), Is.Empty);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task LookupArtistAsync_CloseMatch_ReturnsFactsWithoutUnsafeLinks()
    {
        (MusicBrainzLookupProvider provider, FakeHttpMessageHandler handler) = Create(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/artist", StringComparison.Ordinal)
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{ "artists": [ { "id": "a-1", "name": "The Band" }, { "id": "a-2", "name": "Completely Different" } ] }""")
                : FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """
                    { "id": "a-1", "name": "The Band", "type": "Group", "disambiguation": "UK rock band",
                      "area": { "name": "United Kingdom" }, "life-span": { "begin": "1985-03", "end": "2001-12-31" },
                      "genres": [ { "name": "rock", "count": 4 } ],
                      "relations": [ { "type": "official homepage", "url": { "resource": "https://theband.example/" } },
                                     { "type": "other", "url": { "resource": "javascript:alert(1)" } },
                                     { "type": "other", "url": { "resource": "file:///etc/passwd" } } ] }
                    """));

        ArtistInfo? info = await provider.LookupArtistAsync("The Band");

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Name, Is.EqualTo("The Band"));
        Assert.That(info.Kind, Is.EqualTo("Group"));
        Assert.That(info.Area, Is.EqualTo("United Kingdom"));
        Assert.That(info.BeginYear, Is.EqualTo(1985));
        Assert.That(info.EndYear, Is.EqualTo(2001));
        Assert.That(info.Genres, Is.EqualTo(Items("rock")));
        Assert.That(info.Links, Is.EqualTo(Items(new Uri("https://theband.example/"))));
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task LookupArtistAsync_OnlyDissimilarNames_ReturnsNull()
    {
        (MusicBrainzLookupProvider provider, _) = Create(_ =>
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{ "artists": [ { "id": "a-2", "name": "Completely Different" } ] }"""));
        Assert.That(await provider.LookupArtistAsync("The Band"), Is.Null);
    }

    private sealed class NoDelay : IMusicBrainzRateLimiter
    {
        public Task WaitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static T[] Items<T>(params T[] items) => items;
}
