using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Aibell.Application.Library;
using Aibell.Application.Podcasts;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class PodcastPositionSelectionTests
{
    private string directory = null!;
    private SqliteTrackRepository repository = null!;
    private DelayedMetadata metadata = null!;
    private Player player = null!;
    private Transport transport = null!;
    private PodcastsViewModel model = null!;
    private PodcastsWindow window = null!;
    private readonly PodcastPlaybackProgress previous = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20), DateTimeOffset.UnixEpoch);

    [SetUp]
    public async Task SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "aibell-position-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        repository = new(Path.Combine(directory, "library.db"));
        string downloads = Path.Combine(directory, "downloads");
        PodcastDownloadStore store = new(downloads);
        for (int show = 0; show < 2; show++)
        {
            Guid id = Guid.NewGuid();
            PodcastSubscription subscription = new(id, new Uri($"https://example.invalid/feed/{show}"), "Show " + show, "", DateTimeOffset.UtcNow);
            StoredPodcastEpisode first = new(id, "first", "First episode", new Uri("https://example.invalid/first.wav"));
            StoredPodcastEpisode second = new(id, "second", "Second episode", new Uri("https://example.invalid/second.wav"));
            StoredPodcastEpisode remote = new(id, "remote", "Remote episode", new Uri("https://example.invalid/remote.wav"));
            await repository.AddSubscriptionAsync(subscription, [first, second, remote]);
            foreach (StoredPodcastEpisode episode in new[] { first, second })
            {
                Uri file = store.PrepareDestination(id, episode.EpisodeId + ".wav");
                await File.WriteAllBytesAsync(file.LocalPath, [1, 2, 3]);
                await repository.RecordDownloadAsync(id, episode.EpisodeId, file.LocalPath, 3, DateTimeOffset.UtcNow);
                await repository.SaveEpisodePositionAsync(id, episode.EpisodeId, file.LocalPath, previous);
            }
        }
        metadata = new(); player = new(); transport = new();
        model = new(new PodcastService(repository, transport, transport, store, positions: repository, metadata: metadata), player);
        window = new(model);
    }

    [TearDown]
    public async Task TearDown()
    {
        metadata.Release.TrySetResult(); transport.Release.TrySetResult();
        if (window is not null) { await window.ShutdownAsync(); window.Close(); }
        await repository.DisposeAsync();
        Directory.Delete(directory, true);
    }

    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task SelectionChange_DuringSaveOrResume_CancelsOnlyPendingPositionWithoutLateResults(bool resume, bool changeSubscription)
    {
        await OpenAsync();
        PodcastEpisodeItem original = model.SelectedEpisode!;
        player.Snapshot = new(new Uri(original.Episode.LocalPath!), TimeSpan.FromSeconds(8), true);
        Task pending = resume ? model.ResumeSelectedAsync() : model.SaveSelectedPositionAsync();
        await metadata.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (changeSubscription)
            {
                model.SelectedSubscription = model.Subscriptions.Last();
                Assert.That(model.SelectedEpisode, Is.Null, "An old episode must not remain selected while a different subscription loads.");
                await model.WhenEpisodesLoadedAsync();
                model.SelectedEpisode = model.Episodes.Single(item => item.EpisodeId == "second");
            }
            else model.SelectedEpisode = model.Episodes.Single(item => item.EpisodeId == "second");
            Assert.That(metadata.Token.IsCancellationRequested, Is.True, "Selection changes invalidate accepted position work.");
            Assert.That(pending.IsCompleted, Is.False, "The cancellation-ignoring metadata read must still drain.");
            PodcastEpisodeItem current = model.SelectedEpisode!;
            model.StatusMessage = "Inspecting the newly selected episode.";
            metadata.Release.TrySetResult();
            await pending;
            Assert.That(player.Requests, Is.Empty, "A stale Resume never reaches the shared player.");
            Assert.That((await repository.ListEpisodesAsync(original.Episode.SubscriptionId)).Single(item => item.EpisodeId == original.EpisodeId).SavedProgress, Is.EqualTo(previous));
            Assert.That(original.Episode.SavedProgress, Is.EqualTo(previous));
            Assert.That(current.Episode.SavedProgress, Is.EqualTo(previous));
            Assert.That(model.SelectedEpisode, Is.SameAs(current));
            Assert.That(model.StatusMessage, Is.EqualTo("Inspecting the newly selected episode."), "A stale completion must not overwrite the new selection's status.");
            Assert.That(model.IsPositionBusy, Is.False);
        }
        finally { metadata.Release.TrySetResult(); await pending; }
    }

    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task Close_DuringSaveOrResume_DrainsAcceptedRead(bool resume, bool changeSelection)
    {
        await OpenAsync();
        PodcastEpisodeItem original = model.SelectedEpisode!;
        player.Snapshot = new(new Uri(original.Episode.LocalPath!), TimeSpan.FromSeconds(8), true);
        Task pending = resume ? model.ResumeSelectedAsync() : model.SaveSelectedPositionAsync();
        await metadata.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (changeSelection)
            {
                model.SelectedEpisode = model.Episodes.Single(item => item.EpisodeId == "second");
                Assert.That(metadata.Token.IsCancellationRequested, Is.True);
            }
            window.Close(); Dispatcher.UIThread.RunJobs();
            Assert.That(window.IsVisible, Is.True, "Closing retains ownership until the accepted metadata read drains.");
            Task shutdown = window.ShutdownAsync();
            Assert.That(metadata.Token.IsCancellationRequested, Is.True, "Shutdown signals the accepted operation before its reader can complete.");
            Assert.That(shutdown.IsCompleted, Is.False);
            string status = model.StatusMessage;
            metadata.Release.TrySetResult();
            await pending; await shutdown;
            await UntilAsync(() => !window.IsVisible);
            Assert.That(player.Requests, Is.Empty);
            Assert.That(model.StatusMessage, Is.EqualTo(status));
            Assert.That((await repository.ListEpisodesAsync(original.Episode.SubscriptionId)).Single(item => item.EpisodeId == original.EpisodeId).SavedProgress, Is.EqualTo(previous));
        }
        finally { metadata.Release.TrySetResult(); await pending; }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task SelectionChange_DuringFeedOrDownload_DoesNotCancelUnrelatedOperation(bool download)
    {
        await OpenAsync();
        Task pending = download ? model.DownloadAsync(model.Episodes.Single(item => item.EpisodeId == "remote")) : model.RefreshSelectedAsync();
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            model.SelectedEpisode = model.Episodes.Single(item => item.EpisodeId == "second");
            model.SelectedSubscription = model.Subscriptions.Last();
            await model.WhenEpisodesLoadedAsync();
            Assert.That(transport.Token.IsCancellationRequested, Is.False);
            Assert.That(pending.IsCompleted, Is.False);
            transport.Release.TrySetResult(); await pending;
            Assert.That(transport.Token.IsCancellationRequested, Is.False);
        }
        finally { transport.Release.TrySetResult(); await pending; }
    }

    private async Task OpenAsync()
    {
        window.Show();
        await UntilAsync(() => model.Subscriptions.Count == 2 && model.Episodes.Count == 3);
        model.SelectedEpisode = model.Episodes.Single(item => item.EpisodeId == "first");
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); }
    }
    private sealed class DelayedMetadata : IMediaMetadataReader
    {
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; Started.TrySetResult(); await Release.Task;
            return new("Episode", "Show", null, TimeSpan.FromSeconds(20));
        }
    }
    private sealed class Player : IPodcastPlaybackHandoff, IPodcastPositionSource
    {
        public PodcastPlaybackSnapshot? Snapshot { get; set; }
        public List<PodcastPlaybackRequest> Requests { get; } = [];
        public Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.CompletedTask; }
        public ValueTask<PodcastPlaybackSnapshot?> GetPodcastPlaybackAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
    }
    private sealed class Transport : IPodcastFeedReader, IPodcastEpisodeDownloader
    {
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; Started.TrySetResult(); await Release.Task;
            return new(null, [], NotModified: true);
        }
        public async Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; Started.TrySetResult(); await Release.Task;
            await File.WriteAllBytesAsync(request.Destination.LocalPath, [1, 2, 3], CancellationToken.None);
            return new(request.Destination, 3);
        }
    }
}
