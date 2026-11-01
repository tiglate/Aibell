using System.Text;
using System.Text.Json;
using Aibell.Application.Discs;
using Aibell.Application.Enrichment;

namespace Aibell.Metadata.MusicBrainz;

/// <summary>
/// Album and artist details from MusicBrainz: release by disc table of contents, release by name, and basic artist facts
/// (type, area, years active, genres, links). MusicBrainz has no biographies, so none are offered. Expected failures
/// (unknown disc, offline, bad response, timeout) return empty results; the caller's cancellation propagates.
/// </summary>
internal sealed class MusicBrainzLookupProvider : IReleaseLookupProvider, IArtistInfoProvider
{
    internal const string ProviderId = "musicbrainz";

    private const string ReleaseIncludes = "recordings+artist-credits+genres+release-groups";
    private const int MaxDetailedReleases = 2;
    private const int MaxCoverReleases = 5;
    private const int MaxLinks = 8;
    private const int MaxGenres = 5;
    private const int MaxQueryLength = 256;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly MusicBrainzClient client;
    private readonly TimeSpan requestTimeout;

    public MusicBrainzLookupProvider(MusicBrainzClient client, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
        this.requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        Descriptor = new EnrichmentProviderDescriptor(ProviderId, "MusicBrainz", RequiresNetwork: true, IsEnabled: true);
    }

    public EnrichmentProviderDescriptor Descriptor { get; }

    public Task<IReadOnlyList<ReleaseCandidate>> LookupDiscAsync(DiscToc toc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toc);
        string discId = DiscId.Compute(toc);
        return GuardAsync<IReadOnlyList<ReleaseCandidate>>(async token =>
        {
            // The exact ID first; when MusicBrainz does not know it, "-" with the table of contents finds close matches.
            MusicBrainzDiscResponse? exact = await client.GetAsync<MusicBrainzDiscResponse>(
                $"discid/{Uri.EscapeDataString(discId)}?inc={ReleaseIncludes}&fmt=json", token).ConfigureAwait(false);
            bool isExact = exact?.Releases is { Count: > 0 };
            MusicBrainzDiscResponse? found = isExact ? exact : await client.GetAsync<MusicBrainzDiscResponse>(
                $"discid/-?toc={DiscId.TocQuery(toc)}&inc={ReleaseIncludes}&fmt=json", token).ConfigureAwait(false);
            List<ReleaseCandidate> result = [];
            foreach (MusicBrainzDetailedRelease release in (found?.Releases ?? []).OfType<MusicBrainzDetailedRelease>().Take(5))
            {
                ReleaseCandidate? candidate = await ToCandidateAsync(release, discId, toc.TrackCount, isExact ? 1.0 : 0.6, token, cancellationToken).ConfigureAwait(false);
                if (candidate is not null) result.Add(candidate);
            }
            return result;
        }, [], cancellationToken);
    }

    public Task<IReadOnlyList<ReleaseCandidate>> LookupAlbumAsync(string album, string? artist, bool includeTracks = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(album) || album.Length > MaxQueryLength || artist?.Length > MaxQueryLength)
            return Task.FromResult<IReadOnlyList<ReleaseCandidate>>([]);
        return GuardAsync<IReadOnlyList<ReleaseCandidate>>(async token =>
        {
            StringBuilder query = new StringBuilder("release:\"").Append(Escape(album)).Append('"');
            if (!string.IsNullOrWhiteSpace(artist)) query.Append(" AND artist:\"").Append(Escape(artist)).Append('"');
            MusicBrainzReleaseSearchResponse? search = await client.GetAsync<MusicBrainzReleaseSearchResponse>(
                $"release?query={Uri.EscapeDataString(query.ToString())}&limit=5&fmt=json", token).ConfigureAwait(false);
            var ranked = (search?.Releases ?? []).OfType<MusicBrainzDetailedRelease>()
                .Where(release => !string.IsNullOrWhiteSpace(release.Id) && release.Title is { Length: <= 1024 })
                .Select(release => (Release: release, Score: Score(album, artist, release)))
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Release.Id, StringComparer.Ordinal)
                .Take(includeTracks ? MaxDetailedReleases : MaxCoverReleases)
                .ToArray();
            // Each release needs its own detail request (rate limited) and cover lookup (not limited); run them side by side.
            ReleaseCandidate?[] candidates = await Task.WhenAll(ranked.Select(async match =>
            {
                MusicBrainzDetailedRelease? detailed = includeTracks ? await client.GetAsync<MusicBrainzDetailedRelease>(
                    $"release/{Uri.EscapeDataString(match.Release.Id!)}?inc={ReleaseIncludes}&fmt=json", token).ConfigureAwait(false) : null;
                return await ToCandidateAsync(detailed ?? match.Release, null, 0, match.Score, token, cancellationToken).ConfigureAwait(false);
            })).ConfigureAwait(false);
            return (IReadOnlyList<ReleaseCandidate>)[.. candidates.OfType<ReleaseCandidate>()];
        }, [], cancellationToken);
    }

    public Task<ArtistInfo?> LookupArtistAsync(string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName) || artistName.Length > MaxQueryLength) return Task.FromResult<ArtistInfo?>(null);
        return GuardAsync<ArtistInfo?>(async token =>
        {
            MusicBrainzArtistSearchResponse? search = await client.GetAsync<MusicBrainzArtistSearchResponse>(
                $"artist?query={Uri.EscapeDataString("artist:\"" + Escape(artistName) + "\"")}&limit=5&fmt=json", token).ConfigureAwait(false);
            MusicBrainzArtistDetail? best = (search?.Artists ?? []).OfType<MusicBrainzArtistDetail>()
                .Where(artist => !string.IsNullOrWhiteSpace(artist.Id) && artist.Name is { Length: <= 1024 })
                .Select(artist => (Artist: artist, Similarity: StringSimilarity.Compute(artistName, artist.Name)))
                .Where(match => match.Similarity >= 0.85)
                .OrderByDescending(match => match.Similarity)
                .Select(match => match.Artist)
                .FirstOrDefault();
            if (best is null) return null;
            MusicBrainzArtistDetail detail = await client.GetAsync<MusicBrainzArtistDetail>(
                $"artist/{Uri.EscapeDataString(best.Id!)}?inc=genres+url-rels&fmt=json", token).ConfigureAwait(false) ?? best;
            return new ArtistInfo(ProviderId, best.Id!, detail.Name ?? best.Name ?? artistName, Clean(detail.Type), Clean(detail.Area?.Name),
                ParseYear(detail.LifeSpan?.Begin), ParseYear(detail.LifeSpan?.End), Clean(detail.Disambiguation),
                TopGenres(detail.Genres), Links(detail.Relations));
        }, null, cancellationToken);
    }

    private async Task<ReleaseCandidate?> ToCandidateAsync(MusicBrainzDetailedRelease release, string? discId, int expectedTracks, double confidence,
        CancellationToken requestToken, CancellationToken callerToken)
    {
        if (string.IsNullOrWhiteSpace(release.Id) || string.IsNullOrWhiteSpace(release.Title)) return null;
        MusicBrainzMedium? medium = release.Media?.OfType<MusicBrainzMedium>()
            .FirstOrDefault(candidate => discId is not null && candidate.Discs?.Any(disc => disc?.Id == discId) == true)
            ?? release.Media?.OfType<MusicBrainzMedium>().FirstOrDefault(candidate => expectedTracks > 0 && candidate.Tracks?.Count == expectedTracks)
            ?? release.Media?.OfType<MusicBrainzMedium>().FirstOrDefault();
        List<ReleaseTrack> tracks = [];
        foreach (MusicBrainzTrack track in (medium?.Tracks ?? []).OfType<MusicBrainzTrack>().Take(99))
            tracks.Add(new ReleaseTrack(track.Position, Clean(track.Title) ?? $"Track {track.Position}", BuildArtistCredit(track.ArtistCredit),
                track.Length is > 0 ? TimeSpan.FromMilliseconds(track.Length.Value) : null));
        Uri? cover = await client.GetFrontCoverAsync(release.Id, requestToken, callerToken).ConfigureAwait(false);
        return new ReleaseCandidate(ProviderId, release.Id, release.Title.Trim(), BuildArtistCredit(release.ArtistCredit), ParseYear(release.Date),
            TopGenres(release.Genres is { Count: > 0 } ? release.Genres : release.ReleaseGroup?.Genres).FirstOrDefault(), cover, confidence, tracks);
    }

    private async Task<T> GuardAsync<T>(Func<CancellationToken, Task<T>> operation, T fallback, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(requestTimeout);
        try
        {
            return await operation(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or JsonException)
        {
            return fallback;
        }
    }

    private static double Score(string album, string? artist, MusicBrainzDetailedRelease release)
    {
        double title = StringSimilarity.Compute(album, release.Title);
        return string.IsNullOrWhiteSpace(artist) ? title : title * 0.7 + StringSimilarity.Compute(artist, BuildArtistCredit(release.ArtistCredit)) * 0.3;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? ParseYear(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out int year) && year > 0 ? year : null;

    private static string[] TopGenres(List<MusicBrainzGenre>? genres) =>
        [.. (genres ?? []).OfType<MusicBrainzGenre>().Where(genre => !string.IsNullOrWhiteSpace(genre.Name))
            .OrderByDescending(genre => genre.Count).ThenBy(genre => genre.Name, StringComparer.Ordinal).Take(MaxGenres).Select(genre => genre.Name!.Trim())];

    private static Uri[] Links(List<MusicBrainzRelation>? relations) =>
        [.. (relations ?? []).OfType<MusicBrainzRelation>()
            .Select(relation => Uri.TryCreate(relation.Url?.Resource, UriKind.Absolute, out Uri? uri) ? uri : null)
            .OfType<Uri>().Where(uri => uri.Scheme is "https" or "http").DistinctBy(uri => uri.AbsoluteUri).Take(MaxLinks)];

    private static string? BuildArtistCredit(List<MusicBrainzArtistCredit>? credits)
    {
        string text = string.Concat((credits ?? []).OfType<MusicBrainzArtistCredit>().Select(credit => (credit.Name ?? credit.Artist?.Name) + credit.JoinPhrase)).Trim();
        return text.Length == 0 ? null : text;
    }
}
