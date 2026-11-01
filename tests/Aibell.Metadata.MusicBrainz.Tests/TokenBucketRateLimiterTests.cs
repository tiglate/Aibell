using System.Diagnostics;
using NUnit.Framework;

namespace Aibell.Metadata.MusicBrainz.Tests;

[TestFixture]
public sealed class TokenBucketRateLimiterTests
{
    [Test]
    public async Task WaitAsync_SecondCall_WaitsForConfiguredInterval()
    {
        using TokenBucketRateLimiter limiter = new(TimeSpan.FromMilliseconds(100));

        await limiter.WaitAsync(CancellationToken.None);
        Stopwatch stopwatch = Stopwatch.StartNew();
        await limiter.WaitAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.That(stopwatch.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(80));
    }

    [TestCase(-24)]
    [TestCase(24)]
    public async Task WaitAsync_WallClockJumps_PreservesMonotonicRequestSpacing(int hours)
    {
        ManualClock time = new();
        using TokenBucketRateLimiter limiter = new(TimeSpan.FromMilliseconds(100), time);
        await limiter.WaitAsync(CancellationToken.None);
        time.Offset = TimeSpan.FromHours(hours);
        Task pending = limiter.WaitAsync(CancellationToken.None);
        Assert.That(pending.IsCompleted, Is.False);
        time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.That(pending.IsCompleted, Is.False);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task WaitAsync_CancelledWait_ReleasesGateWithoutConsumingAnotherInterval()
    {
        using TokenBucketRateLimiter limiter = new(TimeSpan.FromMilliseconds(100));
        await limiter.WaitAsync(CancellationToken.None);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(20));
        await Assert.CatchAsync<OperationCanceledException>(async () => await limiter.WaitAsync(cancellation.Token));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));
        await limiter.WaitAsync(deadline.Token);
    }

    [Test]
    public async Task WaitAsync_CancelWhileQueued_DoesNotBlockRemainingCallers()
    {
        using TokenBucketRateLimiter limiter = new(TimeSpan.FromMilliseconds(100));
        await limiter.WaitAsync(CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        Task pending = limiter.WaitAsync(cancellation.Token);
        Task queued = limiter.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(async () => await pending);
        await Assert.CatchAsync<OperationCanceledException>(async () => await queued);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));
        await limiter.WaitAsync(deadline.Token);
    }

    [Test]
    public void Constructor_InvalidIntervals_RejectsThemImmediately()
    {
        Assert.That(() => new TokenBucketRateLimiter(TimeSpan.FromMilliseconds(-1)), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new TokenBucketRateLimiter(TimeSpan.MaxValue), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private long timestamp;
        public TimeSpan Offset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(timestamp) + Offset;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state);
            timers.Add(timer); timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            timestamp += elapsed.Ticks;
            foreach (ManualTimer timer in timers.ToArray()) timer.FireIfDue(timestamp);
        }

        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            private long? due;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("This fixture needs only one-shot timers.");
                due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.timestamp + dueTime.Ticks;
                return true;
            }
            public void FireIfDue(long now)
            {
                if (!disposed && due <= now)
                {
                    due = null;
                    callback(state);
                }
            }
            public void Dispose() { disposed = true; due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
