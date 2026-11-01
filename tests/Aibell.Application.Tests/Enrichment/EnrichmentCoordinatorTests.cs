using Aibell.Application.Enrichment;
using NUnit.Framework;

namespace Aibell.Application.Tests.Enrichment;

[TestFixture]
public sealed class EnrichmentCoordinatorTests
{
    [Test]
    public async Task EnrichAsync_MergesAndRanksCandidatesByConfidenceDescending()
    {
        FakeProvider low = FakeProvider.Returning("low", "low-record", 0.2);
        FakeProvider high = FakeProvider.Returning("high", "high-record", 0.9);
        EnrichmentCoordinator coordinator = new([low, high]);

        EnrichmentResult result = await coordinator.EnrichAsync(new EnrichmentQuery("Title", "Artist"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates, Has.Count.EqualTo(2));
            Assert.That(result.Candidates[0].ProviderId, Is.EqualTo("high"));
            Assert.That(result.Candidates[1].ProviderId, Is.EqualTo("low"));
            Assert.That(result.ProviderOutcomes, Has.Count.EqualTo(2));
            Assert.That(
                result.ProviderOutcomes.All(outcome => outcome.Status == EnrichmentProviderStatus.Succeeded),
                Is.True);
        });
    }

    [Test]
    public async Task EnrichAsync_NoRegisteredProviders_ReturnsEmptyResultWithoutCallingAnything()
    {
        EnrichmentCoordinator coordinator = new([]);

        EnrichmentResult result = await coordinator.EnrichAsync(new EnrichmentQuery("Title", "Artist"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates, Is.Empty);
            Assert.That(result.ProviderOutcomes, Is.Empty);
        });
    }

    [Test]
    public async Task EnrichAsync_DisabledProvider_IsSkippedAndReportedAsDisabled()
    {
        FakeProvider disabled = FakeProvider.Returning("disabled", "record", 0.5, isEnabled: false);
        EnrichmentCoordinator coordinator = new([disabled]);

        EnrichmentResult result = await coordinator.EnrichAsync(new EnrichmentQuery("Title", "Artist"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates, Is.Empty);
            Assert.That(disabled.WasCalled, Is.False);
            Assert.That(result.ProviderOutcomes.Single().Status, Is.EqualTo(EnrichmentProviderStatus.Disabled));
        });
    }

    [Test]
    public async Task EnrichAsync_SlowProvider_DoesNotBlockFastProviderResult()
    {
        FakeProvider slow = FakeProvider.NeverCompletes("slow");
        FakeProvider fast = FakeProvider.Returning("fast", "fast-record", 0.8);
        EnrichmentCoordinator coordinator = new([slow, fast], providerTimeout: TimeSpan.FromMilliseconds(50));

        EnrichmentResult result = await coordinator.EnrichAsync(new EnrichmentQuery("Title", "Artist"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates, Has.Count.EqualTo(1));
            Assert.That(result.Candidates[0].ProviderId, Is.EqualTo("fast"));

            EnrichmentProviderOutcome slowOutcome = result.ProviderOutcomes.Single(outcome => outcome.ProviderId == "slow");
            Assert.That(slowOutcome.Status, Is.EqualTo(EnrichmentProviderStatus.TimedOut));

            EnrichmentProviderOutcome fastOutcome = result.ProviderOutcomes.Single(outcome => outcome.ProviderId == "fast");
            Assert.That(fastOutcome.Status, Is.EqualTo(EnrichmentProviderStatus.Succeeded));
        });
    }

    [Test]
    public async Task EnrichAsync_ThrowingProvider_IsIsolatedFromOtherProviders()
    {
        FakeProvider throwing = FakeProvider.Throwing("throwing");
        FakeProvider healthy = FakeProvider.Returning("healthy", "healthy-record", 0.6);
        EnrichmentCoordinator coordinator = new([throwing, healthy]);

        EnrichmentResult result = await coordinator.EnrichAsync(new EnrichmentQuery("Title", "Artist"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates, Has.Count.EqualTo(1));
            Assert.That(result.Candidates[0].ProviderId, Is.EqualTo("healthy"));

            EnrichmentProviderOutcome faultedOutcome = result.ProviderOutcomes.Single(outcome => outcome.ProviderId == "throwing");
            Assert.That(faultedOutcome.Status, Is.EqualTo(EnrichmentProviderStatus.Faulted));
            Assert.That(faultedOutcome.FailureDetail, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task EnrichAsync_CallerCancellation_Propagates()
    {
        FakeProvider slow = FakeProvider.NeverCompletes("slow");
        EnrichmentCoordinator coordinator = new([slow]);
        using CancellationTokenSource callerCancellation = new(TimeSpan.FromMilliseconds(20));

        await Assert.CatchAsync<OperationCanceledException>(
            async () => await coordinator.EnrichAsync(new EnrichmentQuery("Title", "Artist"), callerCancellation.Token));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EnrichAsync_ProviderIgnoresCancellation_ReturnsTimeoutBeforeLateCompletion(bool failLate)
    {
        TaskCompletionSource<IReadOnlyList<EnrichmentCandidate>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeProvider uncooperative = FakeProvider.Controlled("uncooperative", completion.Task);
        FakeProvider healthy = FakeProvider.Returning("healthy", "record", 0.9);
        EnrichmentCoordinator coordinator = new([uncooperative, healthy], TimeSpan.FromMilliseconds(50));

        try
        {
            EnrichmentResult result = await coordinator.EnrichAsync(new EnrichmentQuery("Title", "Artist"))
                .WaitAsync(TimeSpan.FromSeconds(2));

            if (failLate)
            {
                completion.SetException(new InvalidOperationException("Late provider failure."));
            }
            else
            {
                completion.SetResult([new EnrichmentCandidate("uncooperative", "late", "Title", "Artist")]);
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Candidates, Has.Count.EqualTo(1));
                Assert.That(result.Candidates.Single().ProviderId, Is.EqualTo("healthy"));
                Assert.That(result.ProviderOutcomes.Single(outcome => outcome.ProviderId == "uncooperative").Status,
                    Is.EqualTo(EnrichmentProviderStatus.TimedOut));
            });
        }
        finally
        {
            completion.TrySetResult([]);
        }
    }

    [Test]
    public async Task EnrichAsync_ProviderIgnoresCancellation_CallerCanStillCancel()
    {
        TaskCompletionSource<IReadOnlyList<EnrichmentCandidate>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeProvider provider = FakeProvider.Controlled("uncooperative", completion.Task);
        EnrichmentCoordinator coordinator = new([provider], TimeSpan.FromMinutes(1));
        using CancellationTokenSource cancellation = new();
        Task<EnrichmentResult> request = coordinator.EnrichAsync(new EnrichmentQuery("Title", "Artist"), cancellation.Token);
        cancellation.Cancel();

        try
        {
            await Assert.CatchAsync<OperationCanceledException>(async () => await request.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            completion.TrySetResult([]);
        }
    }

    [Test]
    public async Task EnrichAsync_AlreadyCancelled_DoesNotInvokeProviders()
    {
        FakeProvider provider = FakeProvider.Returning("provider", "record", 0.8);
        EnrichmentCoordinator coordinator = new([provider]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.CatchAsync<OperationCanceledException>(
            async () => await coordinator.EnrichAsync(new EnrichmentQuery("Title", "Artist"), cancellation.Token));

        Assert.That(provider.WasCalled, Is.False);
    }

    [Test]
    public async Task EnrichAsync_AlreadyCancelledWithNoProviders_PropagatesCancellation()
    {
        EnrichmentCoordinator coordinator = new([]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.CatchAsync<OperationCanceledException>(
            async () => await coordinator.EnrichAsync(new EnrichmentQuery("Title", "Artist"), cancellation.Token));
    }

    private sealed class FakeProvider : IEnrichmentProvider
    {
        private readonly Func<EnrichmentQuery, CancellationToken, Task<IReadOnlyList<EnrichmentCandidate>>> _lookup;

        private FakeProvider(
            string id,
            bool isEnabled,
            Func<EnrichmentQuery, CancellationToken, Task<IReadOnlyList<EnrichmentCandidate>>> lookup)
        {
            Descriptor = new EnrichmentProviderDescriptor(id, id, RequiresNetwork: true, isEnabled);
            _lookup = lookup;
        }

        public EnrichmentProviderDescriptor Descriptor { get; }

        public bool WasCalled { get; private set; }

        public static FakeProvider Returning(string id, string recordId, double confidence, bool isEnabled = true)
        {
            return new FakeProvider(id, isEnabled, (_, _) =>
                Task.FromResult<IReadOnlyList<EnrichmentCandidate>>(
                    [new EnrichmentCandidate(id, recordId, "Title", "Artist", Confidence: confidence)]));
        }

        public static FakeProvider Throwing(string id)
        {
            return new FakeProvider(id, isEnabled: true, (_, _) =>
                throw new InvalidOperationException($"{id} failed."));
        }

        public static FakeProvider Controlled(string id, Task<IReadOnlyList<EnrichmentCandidate>> completion)
        {
            return new FakeProvider(id, isEnabled: true, (_, _) => completion);
        }

        public static FakeProvider NeverCompletes(string id)
        {
            return new FakeProvider(id, isEnabled: true, async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            });
        }

        public Task<IReadOnlyList<EnrichmentCandidate>> LookupAsync(
            EnrichmentQuery query,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return _lookup(query, cancellationToken);
        }
    }
}
