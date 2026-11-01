using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Aibell.Application.Podcasts;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class PodcastLifecycleTests
{
    private string directory = null!;
    private SqliteTrackRepository database = null!;
    private BlockingRepository repository = null!;
    private PodcastsViewModel viewModel = null!;
    private PodcastsWindow window = null!;

    [SetUp]
    public async Task SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-podcast-lifetime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        database = new(Path.Combine(directory, "library.db"));
        repository = new(database);
        foreach (string name in new[] { "First", "Second" })
        {
            PodcastSubscription subscription = new(Guid.NewGuid(), new Uri($"https://example.invalid/{name}"), name, "", DateTimeOffset.UtcNow);
            await database.AddSubscriptionAsync(subscription, [new(subscription.Id, "episode", name + " episode", new Uri("https://example.invalid/audio.mp3"))]);
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        repository.ReleaseAll();
        if (window is not null) { await window.ShutdownAsync(); window.Close(); }
        await database.DisposeAsync();
        Directory.Delete(directory, true);
    }

    [AvaloniaTest]
    public async Task Close_DuringInitialCacheRead_DrainsAndSuppressesLateCatalogMutation()
    {
        repository.BlockSubscriptions = true;
        Open();
        await UntilAsync(() => repository.Reads.Count == 1);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.That(window.IsVisible, Is.True, "The catalog still owns an active repository read.");
        // CancelAsync propagates to the linked initial-load token via asynchronous callbacks.
        await UntilAsync(() => repository.Reads[0].Token.IsCancellationRequested);
        Assert.That(repository.Reads[0].Token.IsCancellationRequested, Is.True);
        repository.Reads[0].Release.TrySetResult();
        await UntilAsync(() => !window.IsVisible);
        Assert.That(viewModel.Subscriptions, Is.Empty);
    }

    [AvaloniaTest]
    public async Task Shutdown_SupersededSelectionReads_DrainsEveryReadAndKeepsClosedListsUnchanged()
    {
        Open();
        await UntilAsync(() => viewModel.Subscriptions.Count == 2 && viewModel.Episodes.Count == 1);
        string initialEpisode = viewModel.Episodes.Single().Title;
        repository.BlockEpisodes = true;
        viewModel.SelectedSubscription = viewModel.Subscriptions.Last();
        viewModel.SelectedSubscription = viewModel.Subscriptions.First();
        Assert.That(repository.Reads, Has.Count.EqualTo(2));
        Task shutdown = window.ShutdownAsync();
        Assert.That(shutdown.IsCompleted, Is.False);
        Assert.That(repository.Reads.All(read => read.Token.IsCancellationRequested), Is.True);
        repository.Reads[1].Release.TrySetResult();
        await Task.Delay(20);
        Assert.That(shutdown.IsCompleted, Is.False, "A superseded read must drain too.");
        repository.Reads[0].Release.TrySetResult();
        await shutdown;
        Assert.That(viewModel.Episodes.Single().Title, Is.EqualTo(initialEpisode));
        Assert.That(viewModel.LoadAsync().IsCompletedSuccessfully, Is.True);
    }

    [AvaloniaTest]
    public async Task Shutdown_DuringPlaybackHandoff_DrainsAndSuppressesClosedStatusMutation()
    {
        PodcastSubscription subscription = (await database.ListSubscriptionsAsync())[0];
        string path = Path.Combine(directory, "downloads", "episode.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await database.RecordDownloadAsync(subscription.Id, "episode", path, 3, DateTimeOffset.UtcNow);
        BlockingHandoff handoff = new();
        Open(handoff);
        await UntilAsync(() => viewModel.Episodes.Count == 1);
        Task play = viewModel.PlayAsync(viewModel.Episodes.Single());
        await UntilAsync(() => handoff.Started);
        string status = viewModel.StatusMessage;
        Task shutdown = window.ShutdownAsync();
        Assert.That(shutdown.IsCompleted, Is.False);
        await UntilAsync(() => handoff.Token.IsCancellationRequested);
        Assert.That(handoff.Token.IsCancellationRequested, Is.True);
        handoff.Release.TrySetResult();
        await play;
        await shutdown;
        Assert.That(viewModel.StatusMessage, Is.EqualTo(status));
    }

    [AvaloniaTest]
    public async Task PlayAndDownload_StaleEpisodeFromDifferentSelection_DoNotStartWork()
    {
        BlockingHandoff handoff = new();
        Open(handoff);
        await UntilAsync(() => viewModel.Episodes.Count == 1);
        PodcastEpisodeItem stale = viewModel.Episodes.Single();
        viewModel.SelectedSubscription = viewModel.Subscriptions.Last();
        await viewModel.WhenEpisodesLoadedAsync();
        await viewModel.PlayAsync(stale);
        await viewModel.DownloadAsync(stale);
        Assert.That(handoff.Started, Is.False);
        Assert.That(stale.State, Is.EqualTo(PodcastEpisodeState.NotDownloaded));
    }

    private sealed class BlockingHandoff : IPodcastPlaybackHandoff
    {
        public bool Started { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default)
        { Started = true; Token = cancellationToken; await Release.Task; }
    }

    private void Open(IPodcastPlaybackHandoff? handoff = null)
    {
        OfflineTransport transport = new();
        PodcastService service = new(repository, transport, transport, new PodcastDownloadStore(Path.Combine(directory, "downloads")));
        viewModel = new(service, handoff);
        window = new(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); }
    }

    private sealed record PendingRead(CancellationToken Token)
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingRepository(IPodcastRepository inner) : IPodcastRepository
    {
        public bool BlockSubscriptions { get; set; }
        public bool BlockEpisodes { get; set; }
        public List<PendingRead> Reads { get; } = [];
        public void ReleaseAll() { foreach (PendingRead read in Reads) read.Release.TrySetResult(); }
        private async Task DelayAsync(CancellationToken token)
        {
            PendingRead read = new(token); Reads.Add(read);
            // Simulate a provider that acknowledges cancellation only after its I/O drains.
            await read.Release.Task;
        }
        public async Task<IReadOnlyList<PodcastSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken = default)
        { if (BlockSubscriptions) await DelayAsync(cancellationToken); return await inner.ListSubscriptionsAsync(CancellationToken.None); }
        public async Task<IReadOnlyList<StoredPodcastEpisode>> ListEpisodesAsync(Guid id, CancellationToken cancellationToken = default)
        { if (BlockEpisodes) await DelayAsync(cancellationToken); return await inner.ListEpisodesAsync(id, CancellationToken.None); }
        public Task<bool> AddSubscriptionAsync(PodcastSubscription subscription, IReadOnlyList<StoredPodcastEpisode> episodes, CancellationToken cancellationToken = default) => inner.AddSubscriptionAsync(subscription, episodes, cancellationToken);
        public Task<bool> ApplyRefreshAsync(PodcastSubscription subscription, IReadOnlyList<StoredPodcastEpisode>? episodes, CancellationToken cancellationToken = default) => inner.ApplyRefreshAsync(subscription, episodes, cancellationToken);
        public Task<bool> RemoveSubscriptionAsync(Guid id, CancellationToken cancellationToken = default) => inner.RemoveSubscriptionAsync(id, cancellationToken);
        public Task<bool> RecordDownloadAsync(Guid id, string episode, string path, long bytes, DateTimeOffset at, CancellationToken cancellationToken = default) => inner.RecordDownloadAsync(id, episode, path, bytes, at, cancellationToken);
        public Task<bool> ClearDownloadAsync(Guid id, string episode, CancellationToken cancellationToken = default) => inner.ClearDownloadAsync(id, episode, cancellationToken);
    }

    private sealed class OfflineTransport : IPodcastFeedReader, IPodcastEpisodeDownloader
    {
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default) => throw new AssertionException("Unexpected network request.");
        public Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default) => throw new AssertionException("Unexpected network request.");
    }
}
