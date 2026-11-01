using System.Security.Cryptography;
using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Podcasts;

/// <summary>Independent integration acceptance at the filesystem / service / SQLite boundary.</summary>
[TestFixture]
public sealed class PodcastWorkflowAcceptanceTests
{
    private string directory = null!;
    private string database = null!;
    private string firstRoot = null!;
    private SqliteTrackRepository repository = null!;
    private PodcastDownloadStore store = null!;
    private PodcastService service = null!;
    private PodcastSubscription subscription = null!;
    private StoredPodcastEpisode downloaded = null!;
    private byte[] hash = null!;
    private static readonly PodcastPlaybackProgress Initial = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), DateTimeOffset.UnixEpoch);

    [SetUp]
    public async Task SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "aibell-podcast-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        database = Path.Combine(directory, "profile.db");
        firstRoot = Path.Combine(directory, "first");
        repository = new(database);
        store = new(firstRoot);
        service = CreateService();
        subscription = (await service.SubscribeAsync("https://example.invalid/feed")).Subscription;
        downloaded = await service.DownloadAsync(subscription.Id, "episode");
        await repository.SaveEpisodePositionAsync(subscription.Id, downloaded.EpisodeId, downloaded.LocalPath!, Initial);
        downloaded = await service.SetRatingAsync((await Current()), 4);
        hash = SHA256.HashData(await File.ReadAllBytesAsync(downloaded.LocalPath!));
    }

    [TearDown]
    public async Task TearDown()
    {
        await repository.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }

    [Test]
    public async Task FolderChange_ReopenedProfile_KeepsOldDownloadPlayableAndDeletableWithoutLosingEpisodeRating()
    {
        string secondRoot = Path.Combine(directory, "second");
        store.ApplyConfiguration(PodcastDownloadStore.PrepareConfiguration(secondRoot, [firstRoot]));
        Assert.That(service.ResolvePlayableFile(downloaded), Is.EqualTo(new Uri(downloaded.LocalPath!)));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(downloaded.LocalPath!)), Is.EqualTo(hash));
        await repository.DisposeAsync();
        repository = new(database);
        store = new(secondRoot, [firstRoot]);
        service = CreateService();
        StoredPodcastEpisode reopened = await Current();
        Assert.That(reopened.Rating, Is.EqualTo(4));
        Assert.That(reopened.SavedProgress, Is.EqualTo(Initial));
        RecordingHandoff handoff = new();
        await service.PlayAsync(subscription, reopened, handoff);
        Assert.That(handoff.Requests.Single().File, Is.EqualTo(new Uri(downloaded.LocalPath!)));
        Assert.That(await repository.ListAsync(), Is.Empty);
        StoredPodcastEpisode deleted = await service.DeleteDownloadAsync(reopened, new Source());
        Assert.That(File.Exists(downloaded.LocalPath), Is.False);
        Assert.That(deleted.LocalPath, Is.Null);
        Assert.That(deleted.SavedProgress, Is.Null);
        Assert.That((await Current()).Rating, Is.EqualTo(4));
        Assert.That(await repository.ListSubscriptionsAsync(), Has.Count.EqualTo(1));
        StoredPodcastEpisode redownloaded = await service.DownloadAsync(subscription.Id, "episode");
        Assert.That(Path.GetRelativePath(secondRoot, redownloaded.LocalPath!), Does.Not.StartWith(".."));
        Assert.That(redownloaded.Rating, Is.EqualTo(4));
        Assert.That(redownloaded.SavedProgress, Is.Null);
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(redownloaded.LocalPath!)), Is.EqualTo(hash));
    }

    [TestCase("timestamp")]
    [TestCase("bytes")]
    [TestCase("path")]
    public async Task AutomaticSave_ReplacedDownloadIdentity_CannotOverwriteNewSessionProgress(string changed)
    {
        string path = downloaded.LocalPath!;
        if (changed == "path")
        {
            path += ".replacement";
            File.Copy(downloaded.LocalPath!, path);
        }
        await repository.RecordDownloadAsync(subscription.Id, downloaded.EpisodeId, path,
            downloaded.DownloadedBytes!.Value + (changed == "bytes" ? 1 : 0),
            downloaded.DownloadedAt!.Value.AddTicks(changed == "timestamp" ? 1 : 0));
        StoredPodcastEpisode replacement = await Current();
        StoredPodcastEpisode saved = await service.SaveAutomaticPositionAsync(replacement, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(10));
        Assert.That(async () => await service.SaveAutomaticPositionAsync(downloaded, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That((await Current()).SavedProgress, Is.EqualTo(saved.SavedProgress));
        Assert.That((await Current()).Rating, Is.EqualTo(4));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(downloaded.LocalPath!)), Is.EqualTo(hash));
    }

    [Test]
    public async Task Delete_DatabaseAbort_RestoresOriginalBytesAndDownloadPositionRating()
    {
        await using (SqliteConnection connection = new($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_delete BEFORE UPDATE OF local_path ON podcast_episodes BEGIN SELECT RAISE(ABORT,'test database failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        Assert.That(async () => await service.DeleteDownloadAsync(downloaded, new Source()), Throws.TypeOf<SqliteException>());
        Assert.That(await Current(), Is.EqualTo(downloaded));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(downloaded.LocalPath!)), Is.EqualTo(hash));
        Assert.That(Directory.GetFiles(firstRoot, "*", SearchOption.AllDirectories), Is.EqualTo(new[] { downloaded.LocalPath }));
    }

    [Test]
    public async Task Delete_DuringAcceptedPlaybackHandoff_RejectsWithoutRemovingPendingInput()
    {
        Source source = new();
        RecordingHandoff handoff = new() { Source = source, Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Task play = service.PlayAsync(subscription, downloaded, handoff);
        await handoff.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task deletion = service.DeleteDownloadAsync(downloaded, source);
        try
        {
            Assert.That(async () => await deletion, Throws.TypeOf<InvalidOperationException>().With.Message.Contains("starting"));
        }
        finally { handoff.Release.TrySetResult(); }
        await play;

        Assert.That(await Current(), Is.EqualTo(downloaded));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(downloaded.LocalPath!)), Is.EqualTo(hash));
    }

    [Test]
    public async Task Play_SameEpisodeHandoffFlushesOutgoingAutomaticPosition_DoesNotDeadlock()
    {
        RecordingHandoff handoff = new()
        {
            OnPlay = () => service.SaveAutomaticPositionAsync(downloaded, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(10))
        };
        await service.PlayAsync(subscription, downloaded, handoff).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(handoff.Requests, Has.Count.EqualTo(1));
        Assert.That(handoff.Requests.Single().Episode!.EpisodeId, Is.EqualTo(downloaded.EpisodeId));
        Assert.That((await Current()).SavedProgress!.Position, Is.EqualTo(TimeSpan.FromSeconds(6)));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(downloaded.LocalPath!)), Is.EqualTo(hash));
    }

    [Test]
    public async Task Delete_AcceptedWhileAutomaticSaveQueued_SaveCannotRecreateDeletedProgress()
    {
        Source source = new() { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Task<StoredPodcastEpisode> deletion = service.DeleteDownloadAsync(downloaded, source);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task save = service.SaveAutomaticPositionAsync(downloaded, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10));
        try { Assert.That(save.IsCompleted, Is.False); }
        finally { source.Release.TrySetResult(); }
        await deletion;
        Assert.That(async () => await save, Throws.TypeOf<FileNotFoundException>());
        StoredPodcastEpisode current = await Current();
        Assert.That(current.LocalPath, Is.Null);
        Assert.That(current.SavedProgress, Is.Null);
        Assert.That(current.Rating, Is.EqualTo(4));
        Assert.That(File.Exists(downloaded.LocalPath), Is.False);
    }

    private PodcastService CreateService() => new(repository, new Feed(), new Downloader(), store, positions: repository);
    private async Task<StoredPodcastEpisode> Current() => (await repository.ListEpisodesAsync(subscription.Id)).Single();

    private sealed class Feed : IPodcastFeedReader
    {
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PodcastFeedResult(new PodcastFeed(request.Source, "Acceptance show", "", [new PodcastEpisode("episode", "Episode", new Uri("https://example.invalid/episode.wav"))]), []));
    }
    private sealed class Downloader : IPodcastEpisodeDownloader
    {
        public async Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default)
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.wav"), cancellationToken);
            await File.WriteAllBytesAsync(request.Destination.LocalPath, bytes, cancellationToken);
            return new(request.Destination, bytes.Length);
        }
    }
    private sealed class Source : IPodcastPositionSource
    {
        public PodcastPlaybackSnapshot? Snapshot { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; init; }
        public async ValueTask<PodcastPlaybackSnapshot?> GetPodcastPlaybackAsync(CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (Release is not null) await Release.Task;
            return Snapshot;
        }
    }
    private sealed class RecordingHandoff : IPodcastPlaybackHandoff
    {
        public List<PodcastPlaybackRequest> Requests { get; } = [];
        public Source? Source { get; init; }
        public Func<Task>? OnPlay { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; init; }
        public async Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (OnPlay is not null) await OnPlay();
            if (Source is not null) Source.Snapshot = new(request.File, TimeSpan.FromSeconds(1), true);
            Started.TrySetResult();
            if (Release is not null) await Release.Task;
        }
    }
}
