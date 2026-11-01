using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aibell.Application.Enrichment;

namespace Aibell.Metadata.MusicBrainz;

public sealed class MusicBrainzEnrichmentProvider : IEnrichmentProvider
{
    private const string ProviderId = "musicbrainz";
    private const string ProviderDisplayName = "MusicBrainz";

    // A recording search can report up to MaxCandidates releases; resolving cover art for
    // every one of them would multiply outgoing requests well past the service's etiquette.
    private const int MaxArtworkLookups = 3;
    private const int MaxMatchTextLength = 1024;
    private const int MaxInspectedRecordings = 100;

    private static readonly Uri DefaultMusicBrainzBaseUri = new("https://musicbrainz.org/ws/2/");
    private static readonly Uri DefaultCoverArtArchiveBaseUri = new("https://coverartarchive.org/");
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRequestInterval = TimeSpan.FromSeconds(1);

    private readonly MusicBrainzClient _client;
    private readonly TimeSpan _requestTimeout;

    public MusicBrainzEnrichmentProvider(
        HttpClient httpClient,
        bool isEnabled = true,
        Uri? musicBrainzBaseUri = null,
        Uri? coverArtArchiveBaseUri = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? requestInterval = null)
        : this(
            httpClient,
            new TokenBucketRateLimiter(requestInterval ?? DefaultRequestInterval),
            isEnabled,
            musicBrainzBaseUri,
            coverArtArchiveBaseUri,
            requestTimeout)
    {
    }

    internal MusicBrainzEnrichmentProvider(
        HttpClient httpClient,
        IMusicBrainzRateLimiter rateLimiter,
        bool isEnabled = true,
        Uri? musicBrainzBaseUri = null,
        Uri? coverArtArchiveBaseUri = null,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(rateLimiter);

        _client = new MusicBrainzClient(httpClient, rateLimiter, musicBrainzBaseUri ?? DefaultMusicBrainzBaseUri,
            coverArtArchiveBaseUri ?? DefaultCoverArtArchiveBaseUri);
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;

        Descriptor = new EnrichmentProviderDescriptor(ProviderId, ProviderDisplayName, RequiresNetwork: true, isEnabled);
    }

    public EnrichmentProviderDescriptor Descriptor { get; }

    public async Task<IReadOnlyList<EnrichmentCandidate>> LookupAsync(
        EnrichmentQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Descriptor.IsEnabled || string.IsNullOrWhiteSpace(query.Title) ||
            query.Title.Length > MaxMatchTextLength || query.Artist?.Length > MaxMatchTextLength)
        {
            return [];
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_requestTimeout);

        try
        {
            MusicBrainzSearchResponse? response = await SearchRecordingsAsync(query, timeoutSource.Token).ConfigureAwait(false);
            List<MusicBrainzRecording> recordings = response?.Recordings ?? [];
            if (recordings.Count == 0)
            {
                return [];
            }

            List<EnrichmentCandidate> candidates = [];
            int artworkLookupsRemaining = query.IncludeArtwork ? MaxArtworkLookups : 0;

            var matches = recordings.OfType<MusicBrainzRecording>()
                .Take(MaxInspectedRecordings)
                .Where(recording => !string.IsNullOrWhiteSpace(recording.Id) &&
                    (recording.Title is null || recording.Title.Length <= MaxMatchTextLength))
                .Select(recording =>
                {
                    string? artist = BuildArtistCredit(recording.ArtistCredit);
                    return (Recording: recording, Artist: artist);
                })
                .Where(match => match.Artist is null || match.Artist.Length <= MaxMatchTextLength)
                .Select(match => (match.Recording, match.Artist, Confidence: ComputeConfidence(query, match.Recording.Title, match.Artist)))
                .OrderByDescending(match => match.Confidence)
                .ThenBy(match => match.Recording.Id, StringComparer.Ordinal)
                .Take(Math.Clamp(query.MaxCandidates, 1, 25));

            foreach (var match in matches)
            {
                MusicBrainzRecording recording = match.Recording;
                MusicBrainzRelease? release = recording.Releases?.OfType<MusicBrainzRelease>().FirstOrDefault();

                Uri? artworkUri = null;
                if (artworkLookupsRemaining > 0 && !string.IsNullOrWhiteSpace(release?.Id))
                {
                    artworkLookupsRemaining--;
                    artworkUri = await _client.GetFrontCoverAsync(release!.Id!, timeoutSource.Token, cancellationToken).ConfigureAwait(false);
                }

                candidates.Add(new EnrichmentCandidate(
                    ProviderId,
                    recording.Id!,
                    recording.Title,
                    match.Artist,
                    release?.Title,
                    AlbumArtist: match.Artist,
                    Year: ParseYear(release?.Date ?? recording.FirstReleaseDate),
                    TrackNumber: null,
                    Confidence: match.Confidence,
                    ArtworkUri: artworkUri));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return candidates;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Task<MusicBrainzSearchResponse?> SearchRecordingsAsync(EnrichmentQuery query, CancellationToken cancellationToken)
    {
        int limit = Math.Clamp(query.MaxCandidates, 1, 25);
        return _client.GetAsync<MusicBrainzSearchResponse>(
            $"recording?query={Uri.EscapeDataString(BuildSearchQuery(query))}&limit={limit}&fmt=json", cancellationToken);
    }

    private static string BuildSearchQuery(EnrichmentQuery query)
    {
        StringBuilder builder = new();
        builder.Append("recording:\"").Append(EscapeLucene(query.Title!)).Append('"');

        if (!string.IsNullOrWhiteSpace(query.Artist))
        {
            builder.Append(" AND artist:\"").Append(EscapeLucene(query.Artist)).Append('"');
        }

        return builder.ToString();
    }

    private static string EscapeLucene(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string? BuildArtistCredit(List<MusicBrainzArtistCredit>? credits)
    {
        if (credits is null || credits.Count == 0)
        {
            return null;
        }

        StringBuilder builder = new();
        foreach (MusicBrainzArtistCredit credit in credits.OfType<MusicBrainzArtistCredit>())
        {
            builder.Append(credit.Name ?? credit.Artist?.Name);
            builder.Append(credit.JoinPhrase);
        }

        string result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
        {
            return null;
        }

        return int.TryParse(date.AsSpan(0, 4), out int year) ? year : null;
    }

    private static double ComputeConfidence(EnrichmentQuery query, string? candidateTitle, string? candidateArtist)
    {
        double titleSimilarity = StringSimilarity.Compute(query.Title, candidateTitle);

        if (string.IsNullOrWhiteSpace(query.Artist))
        {
            return titleSimilarity;
        }

        double artistSimilarity = StringSimilarity.Compute(query.Artist, candidateArtist);
        return (titleSimilarity * 0.7) + (artistSimilarity * 0.3);
    }
}
