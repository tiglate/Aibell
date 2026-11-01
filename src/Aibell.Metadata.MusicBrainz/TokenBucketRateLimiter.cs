namespace Aibell.Metadata.MusicBrainz;

internal sealed class TokenBucketRateLimiter : IMusicBrainzRateLimiter, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan interval;
    private readonly TimeProvider timeProvider;
    private long? lastRequestTimestamp;

    public TokenBucketRateLimiter(TimeSpan interval, TimeProvider? timeProvider = null)
    {
        if (interval < TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval), "Use a non-negative interval supported by the timer.");
        this.interval = interval;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (lastRequestTimestamp is { } previous)
            {
                TimeSpan delay = interval - timeProvider.GetElapsedTime(previous);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            lastRequestTimestamp = timeProvider.GetTimestamp();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
