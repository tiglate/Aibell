namespace Aibell.Application.Enrichment;

public sealed record EnrichmentQuery(
    string? Title,
    string? Artist,
    string? Album = null,
    string? AlbumArtist = null,
    TimeSpan? Duration = null,
    int? Year = null,
    int? TrackNumber = null,
    bool IncludeArtwork = true,
    int MaxCandidates = 5);
