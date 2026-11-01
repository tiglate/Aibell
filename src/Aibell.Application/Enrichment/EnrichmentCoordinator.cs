using System.Diagnostics;

namespace Aibell.Application.Enrichment;

public sealed class EnrichmentCoordinator(
    IEnumerable<IEnrichmentProvider> providers,
    TimeSpan? providerTimeout = null) : IEnrichmentCoordinator
{
    private static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(8);

    private readonly TimeSpan _providerTimeout = providerTimeout ?? DefaultProviderTimeout;

    public async Task<EnrichmentResult> EnrichAsync(EnrichmentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        List<Task<(IReadOnlyList<EnrichmentCandidate> Candidates, EnrichmentProviderOutcome Outcome)>> runs = [];
        List<EnrichmentProviderOutcome> skipped = [];

        foreach (IEnrichmentProvider provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!provider.Descriptor.IsEnabled)
            {
                skipped.Add(new EnrichmentProviderOutcome(provider.Descriptor.Id, EnrichmentProviderStatus.Disabled, 0, TimeSpan.Zero));
                continue;
            }

            runs.Add(RunProviderAsync(provider, query, cancellationToken));
        }

        (IReadOnlyList<EnrichmentCandidate> Candidates, EnrichmentProviderOutcome Outcome)[] completed =
            await Task.WhenAll(runs).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        List<EnrichmentCandidate> candidates = [];
        List<EnrichmentProviderOutcome> outcomes = [.. skipped];
        foreach ((IReadOnlyList<EnrichmentCandidate> providerCandidates, EnrichmentProviderOutcome outcome) in completed)
        {
            candidates.AddRange(providerCandidates);
            outcomes.Add(outcome);
        }

        IReadOnlyList<EnrichmentCandidate> ranked = candidates
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.ProviderId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.RecordId, StringComparer.Ordinal)
            .ToArray();

        return new EnrichmentResult(ranked, outcomes);
    }

    private async Task<(IReadOnlyList<EnrichmentCandidate> Candidates, EnrichmentProviderOutcome Outcome)> RunProviderAsync(
        IEnrichmentProvider provider,
        EnrichmentQuery query,
        CancellationToken cancellationToken)
    {
        string providerId = provider.Descriptor.Id;
        Stopwatch stopwatch = Stopwatch.StartNew();
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_providerTimeout);

        try
        {
            Task<IReadOnlyList<EnrichmentCandidate>> lookup = provider.LookupAsync(query, timeoutSource.Token);

            // Providers may ignore cancellation and fault after we stop waiting.
            // Observe that failure without delaying the result or accepting late candidates.
            _ = lookup.ContinueWith(
                static task => { _ = task.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            IReadOnlyList<EnrichmentCandidate> candidates = await lookup
                .WaitAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            return (candidates, new EnrichmentProviderOutcome(providerId, EnrichmentProviderStatus.Succeeded, candidates.Count, stopwatch.Elapsed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ([], new EnrichmentProviderOutcome(providerId, EnrichmentProviderStatus.TimedOut, 0, stopwatch.Elapsed));
        }
        catch (Exception exception)
        {
            return ([], new EnrichmentProviderOutcome(providerId, EnrichmentProviderStatus.Faulted, 0, stopwatch.Elapsed, exception.Message));
        }
    }
}
