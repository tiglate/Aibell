namespace Aibell.Application.Enrichment;

public enum EnrichmentProviderStatus
{
    Succeeded,
    Disabled,
    TimedOut,
    Faulted,
}

public sealed record EnrichmentProviderOutcome(
    string ProviderId,
    EnrichmentProviderStatus Status,
    int CandidateCount,
    TimeSpan Elapsed,
    string? FailureDetail = null);

public sealed record EnrichmentResult(
    IReadOnlyList<EnrichmentCandidate> Candidates,
    IReadOnlyList<EnrichmentProviderOutcome> ProviderOutcomes);
