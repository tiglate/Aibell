using System.Threading.Channels;
using Aibell.Application.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aibell.Desktop.Services;

public sealed class LibrarySynchronizedEventArgs(
    string sourceRoot,
    LibraryImportResult result) : EventArgs
{
    public string SourceRoot { get; } = sourceRoot;

    public LibraryImportResult Result { get; } = result;
}

public sealed class LibraryMonitoringService(
    ILibrarySourceRepository sourceRepository,
    LibraryImportService importService,
    ILogger<LibraryMonitoringService> logger) : BackgroundService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(5);
    private static readonly Action<ILogger, string, Exception?> LogSynchronizationFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, "LibrarySynchronizationFailure"),
            "Failed to synchronize music source {SourceRoot}");
    private static readonly Action<ILogger, string, Exception?> LogWatcherOverflow =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, "LibraryWatcherOverflow"),
            "Music source watcher overflowed for {SourceRoot}");
    private readonly Channel<string> pendingSources = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.Ordinal);
    private readonly HashSet<string> disabledSources = new(StringComparer.Ordinal);
    private readonly object watcherLock = new();
    private readonly SemaphoreSlim synchronizationLock = new(1, 1);

    public event EventHandler<LibrarySynchronizedEventArgs>? LibrarySynchronized;

    public void Watch(string sourceRoot)
    {
        string normalized = Path.GetFullPath(sourceRoot);
        lock (watcherLock)
        {
            disabledSources.Remove(normalized);
        }

        EnsureWatcher(normalized);
    }

    public async Task StopWatchingAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        string normalized = Path.GetFullPath(sourceRoot);
        lock (watcherLock)
        {
            disabledSources.Add(normalized);
            if (watchers.Remove(normalized, out FileSystemWatcher? watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
        }

        await synchronizationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        synchronizationLock.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<string> sources = await sourceRepository
            .ListAsync(stoppingToken)
            .ConfigureAwait(false);
        foreach (string source in sources)
        {
            Watch(source);
            pendingSources.Writer.TryWrite(source);
        }

        Task reconciliation = QueuePeriodicReconciliationAsync(stoppingToken);
        try
        {
            await ProcessChangesAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            await reconciliation.ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        lock (watcherLock)
        {
            foreach (FileSystemWatcher watcher in watchers.Values)
            {
                watcher.Dispose();
            }

            watchers.Clear();
        }

        base.Dispose();
    }

    private async Task ProcessChangesAsync(CancellationToken cancellationToken)
    {
        await foreach (string firstSource in pendingSources.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            await Task.Delay(DebounceDelay, cancellationToken).ConfigureAwait(false);
            HashSet<string> batch = new(StringComparer.Ordinal) { firstSource };
            while (pendingSources.Reader.TryRead(out string? source))
            {
                batch.Add(source);
            }

            foreach (string source in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await synchronizationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (IsDisabled(source))
                    {
                        continue;
                    }

                    EnsureWatcher(source);
                    LibraryImportResult result = await importService
                        .ImportAsync([source], cancellationToken)
                        .ConfigureAwait(false);
                    LibrarySynchronized?.Invoke(
                        this,
                        new LibrarySynchronizedEventArgs(source, result));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogSynchronizationFailure(logger, source, exception);
                }
                finally
                {
                    synchronizationLock.Release();
                }
            }
        }
    }

    private async Task QueuePeriodicReconciliationAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(ReconciliationInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<string> sources = await sourceRepository
                .ListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (string source in sources)
            {
                pendingSources.Writer.TryWrite(source);
            }
        }
    }

    private void EnsureWatcher(string sourceRoot)
    {
        lock (watcherLock)
        {
            if (disabledSources.Contains(sourceRoot) ||
                watchers.ContainsKey(sourceRoot) ||
                !Directory.Exists(sourceRoot))
            {
                return;
            }

            FileSystemWatcher watcher = new(sourceRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.DirectoryName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size,
                EnableRaisingEvents = false
            };
            FileSystemEventHandler changed = (_, _) => pendingSources.Writer.TryWrite(sourceRoot);
            RenamedEventHandler renamed = (_, _) => pendingSources.Writer.TryWrite(sourceRoot);
            ErrorEventHandler error = (_, eventArgs) =>
            {
                LogWatcherOverflow(logger, sourceRoot, eventArgs.GetException());
                pendingSources.Writer.TryWrite(sourceRoot);
            };
            watcher.Created += changed;
            watcher.Changed += changed;
            watcher.Deleted += changed;
            watcher.Renamed += renamed;
            watcher.Error += error;
            watcher.EnableRaisingEvents = true;
            watchers.Add(sourceRoot, watcher);
        }
    }

    private bool IsDisabled(string sourceRoot)
    {
        lock (watcherLock)
        {
            return disabledSources.Contains(sourceRoot);
        }
    }
}
