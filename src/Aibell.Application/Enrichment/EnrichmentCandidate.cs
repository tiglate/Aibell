namespace Aibell.Application.Enrichment;

public sealed record EnrichmentCandidate(
    string ProviderId,
    string RecordId,
    string? Title,
    string? Artist,
    string? Album = null,
    string? AlbumArtist = null,
    int? Year = null,
    int? TrackNumber = null,
    double Confidence = 0,
    Uri? ArtworkUri = null);
