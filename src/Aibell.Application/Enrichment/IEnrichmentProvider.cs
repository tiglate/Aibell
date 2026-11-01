namespace Aibell.Application.Enrichment;

public interface IEnrichmentProvider
{
    EnrichmentProviderDescriptor Descriptor { get; }

    Task<IReadOnlyList<EnrichmentCandidate>> LookupAsync(
        EnrichmentQuery query,
        CancellationToken cancellationToken = default);
}
