using Aibell.Application.Podcasts;
using Aibell.Desktop.Services;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.Podcasts;

[TestFixture]
public sealed class PodcastAutoRefresherTests
{
    private static readonly PodcastRefreshAllResult Quiet = new(2, 2, 0, []);

    [Test]
    public async Task Start_WaitsTheInitialDelayThenChecksEveryInterval()
    {
        ManualClock clock = new();
        int checks = 0;
        await using PodcastAutoRefresher refresher = new(_ => { checks++; return Task.FromResult(Quiet); }, () => true,
            TimeSpan.FromSeconds(45), TimeSpan.FromHours(1), clock.Delay);
        refresher.Start();
        await clock.WaitForPendingAsync(TimeSpan.FromSeconds(45));
        Assert.That(checks, Is.Zero, "Nothing happens before the initial delay.");

        await clock.ElapseAsync();
        await clock.WaitForPendingAsync(TimeSpan.FromHours(1));
        Assert.That(checks, Is.EqualTo(1));
        await clock.ElapseAsync();
        await clock.WaitForPendingAsync(TimeSpan.FromHours(1));
        Assert.That(checks, Is.EqualTo(2));
    }

    [Test]
    public async Task CheckAsync_DisabledPreferenceSkipsTheNetworkButKeepsTheSchedule()
    {
        bool enabled = false;
        int checks = 0;
        await using PodcastAutoRefresher refresher = new(_ => { checks++; return Task.FromResult(Quiet); }, () => enabled);
        Assert.That(await refresher.CheckAsync(), Is.Null);
        Assert.That(checks, Is.Zero);
        enabled = true;
        Assert.That(await refresher.CheckAsync(), Is.EqualTo(Quiet));
        Assert.That(checks, Is.EqualTo(1));
    }

    [Test]
    public async Task CheckAsync_OfflineFailureIsIgnoredAndSuccessIsAnnounced()
    {
        bool offline = true;
        List<PodcastRefreshAllResult> announced = [];
        await using PodcastAutoRefresher refresher = new(_ => offline ? throw new HttpRequestException("offline") : Task.FromResult(new PodcastRefreshAllResult(1, 1, 3, [])),
            () => true);
        refresher.Refreshed += (_, result) => announced.Add(result);

        Assert.That(await refresher.CheckAsync(), Is.Null);
        Assert.That(announced, Is.Empty);
        offline = false;
        Assert.That((await refresher.CheckAsync())!.NewEpisodes, Is.EqualTo(3));
        Assert.That(announced.Single().NewEpisodes, Is.EqualTo(3));
    }

    [Test]
    public async Task DisposeAsync_CancelsAPendingCheckAndStopsTheLoop()
    {
        ManualClock clock = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        PodcastAutoRefresher refresher = new(async token =>
        {
            seen = token; started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Quiet;
        }, () => true, TimeSpan.Zero, TimeSpan.FromHours(1), clock.Delay);
        refresher.Start();
        await clock.WaitForPendingAsync(TimeSpan.Zero);
        await clock.ElapseAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await refresher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(seen.IsCancellationRequested, Is.True);
    }

    private sealed class ManualClock
    {
        private readonly object gate = new();
        private TaskCompletionSource? current;
        private TimeSpan requested;

        public async Task Delay(TimeSpan span, CancellationToken token)
        {
            TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) { current = source; requested = span; }
            await using (token.Register(() => source.TrySetCanceled(token))) await source.Task;
        }

        public async Task WaitForPendingAsync(TimeSpan expected)
        {
            for (int attempt = 0; attempt < 500; attempt++)
            {
                lock (gate) if (current is { Task.IsCompleted: false } && requested == expected) return;
                await Task.Delay(10);
            }
            Assert.Fail($"No {expected} delay became pending.");
        }

        public Task ElapseAsync()
        {
            lock (gate) current!.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
