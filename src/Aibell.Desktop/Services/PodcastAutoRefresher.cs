using Aibell.Application.Podcasts;

namespace Aibell.Desktop.Services;

/// <summary>Something happened to the cached podcast catalog without the Podcasts view asking for it.</summary>
public interface IPodcastCatalogNotifications
{
    event EventHandler<PodcastRefreshAllResult>? PodcastCatalogRefreshed;
}

/// <summary>
/// Checks every subscription for new episodes shortly after start-up and then at a fixed interval while the application runs.
/// Disabled preferences skip the check without stopping the schedule. Failures such as being offline are ignored until the next turn.
/// </summary>
public sealed class PodcastAutoRefresher : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);

    private readonly Func<CancellationToken, Task<PodcastRefreshAllResult>> refresh;
    private readonly Func<bool> isEnabled;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeSpan initialDelay;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource stop = new();
    private Task? loop;

    public PodcastAutoRefresher(Func<CancellationToken, Task<PodcastRefreshAllResult>> refresh, Func<bool> isEnabled,
        TimeSpan? initialDelay = null, TimeSpan? interval = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        this.isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        this.initialDelay = initialDelay ?? DefaultInitialDelay;
        this.interval = interval ?? DefaultInterval;
        this.delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    /// <summary>Raised after a check that completed, on a worker thread.</summary>
    public event EventHandler<PodcastRefreshAllResult>? Refreshed;

    public void Start() => loop ??= RunAsync();

    private async Task RunAsync()
    {
        try
        {
            await delay(initialDelay, stop.Token).ConfigureAwait(false);
            while (!stop.IsCancellationRequested)
            {
                await CheckAsync().ConfigureAwait(false);
                await delay(interval, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One check now, honoring the preference. Returns null when disabled or when the check failed.</summary>
    public async Task<PodcastRefreshAllResult?> CheckAsync()
    {
        if (!isEnabled()) return null;
        try
        {
            PodcastRefreshAllResult result = await refresh(stop.Token).ConfigureAwait(false);
            Refreshed?.Invoke(this, result);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        try { stop.Cancel(); } catch (ObjectDisposedException) { }
        if (loop is not null) { try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        stop.Dispose();
    }
}
