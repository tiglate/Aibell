namespace Aibell.Application.Enrichment;

public sealed record EnrichmentProviderDescriptor(
    string Id,
    string DisplayName,
    bool RequiresNetwork,
    bool IsEnabled);
