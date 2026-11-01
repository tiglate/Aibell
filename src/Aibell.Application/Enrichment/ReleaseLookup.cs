using Aibell.Application.Discs;

namespace Aibell.Application.Enrichment;

public sealed record ReleaseTrack(int Number, string Title, string? Artist = null, TimeSpan? Duration = null);

/// <summary>One possible album for a disc or a name search. Cover art is a location only; the host decides whether to download it.</summary>
public sealed record ReleaseCandidate(
    string ProviderId,
    string RecordId,
    string Title,
    string? Artist,
    int? Year,
    string? Genre,
    Uri? ArtworkUri,
    double Confidence,
    IReadOnlyList<ReleaseTrack> Tracks);

public sealed record ArtistInfo(
    string ProviderId,
    string RecordId,
    string Name,
    string? Kind = null,
    string? Area = null,
    int? BeginYear = null,
    int? EndYear = null,
    string? Disambiguation = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<Uri>? Links = null);

/// <summary>Finds album details. Like <see cref="IEnrichmentProvider"/>, expected failures return empty results instead of throwing.</summary>
public interface IReleaseLookupProvider
{
    EnrichmentProviderDescriptor Descriptor { get; }

    Task<IReadOnlyList<ReleaseCandidate>> LookupDiscAsync(DiscToc toc, CancellationToken cancellationToken = default);

    /// <param name="includeTracks">False when only the album, artist, year and cover are needed; the provider can then skip the
    /// per-release request that carries the track list and return more candidates faster.</param>
    Task<IReadOnlyList<ReleaseCandidate>> LookupAlbumAsync(string album, string? artist, bool includeTracks = true, CancellationToken cancellationToken = default);
}

public interface IArtistInfoProvider
{
    EnrichmentProviderDescriptor Descriptor { get; }

    Task<ArtistInfo?> LookupArtistAsync(string artistName, CancellationToken cancellationToken = default);
}
