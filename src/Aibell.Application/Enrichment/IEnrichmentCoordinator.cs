namespace Aibell.Application.Enrichment;

public interface IEnrichmentCoordinator
{
    Task<EnrichmentResult> EnrichAsync(EnrichmentQuery query, CancellationToken cancellationToken = default);
}
