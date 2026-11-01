using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Podcasts;

public sealed partial class PodcastServiceTests
{
    [Test]
    public async Task Download_MissingPreviousFileAndConcurrentRatingRefresh_ReturnsAuthoritativeRowWithoutOldProgress()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "Old title", 1));
        PodcastSubscription subscription = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription;
        StoredPodcastEpisode first = await service.DownloadAsync(subscription.Id, "a");
        await repository.SaveEpisodePositionAsync(subscription.Id, "a", first.LocalPath!,
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), DateTimeOffset.UtcNow));
        await repository.SetEpisodeRatingAsync(subscription.Id, "a", 2);
        File.Delete(first.LocalPath!);
        downloader.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        downloader.OnStarted = () => secondStarted.TrySetResult();
        Task<StoredPodcastEpisode> pending = service.DownloadAsync(subscription.Id, "a");
        try
        {
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(downloader.Requests, Has.Count.EqualTo(2));
            await repository.SetEpisodeRatingAsync(subscription.Id, "a", 5);
            await repository.ApplyRefreshAsync(subscription,
                [new(subscription.Id, "a", "Refreshed title", new Uri("https://cdn.example/new.mp3"))]);
        }
        finally { downloader.Gate.TrySetResult(); }
        StoredPodcastEpisode downloaded = await pending;
        StoredPodcastEpisode current = (await repository.ListEpisodesAsync(subscription.Id)).Single();
        Assert.That(downloaded, Is.EqualTo(current));
        Assert.That(downloaded.Rating, Is.EqualTo(5));
        Assert.That(downloaded.Title, Is.EqualTo("Refreshed title"));
        Assert.That(downloaded.SavedProgress, Is.Null);
        Assert.That(downloaded.LocalPath, Is.EqualTo(first.LocalPath));
        Assert.That(File.Exists(downloaded.LocalPath), Is.True);
    }

    [TestCase("audio/mp4", ".m4a")]
    [TestCase("Audio/MP4; codecs=mp4a.40.2", ".m4a")]
    [TestCase("audio/x-m4a", ".m4a")]
    [TestCase("audio/m4a", ".m4a")]
    [TestCase("audio/aac", ".aac")]
    [TestCase("audio/ogg; codecs=vorbis", ".ogg")]
    [TestCase("audio/opus", ".opus")]
    [TestCase("audio/flac", ".flac")]
    [TestCase("audio/x-flac", ".flac")]
    [TestCase("audio/wav", ".wav")]
    [TestCase("audio/x-wav", ".wav")]
    [TestCase("application/octet-stream", ".mp3")]
    [TestCase("invalid media type", ".mp3")]
    [TestCase(null, ".mp3")]
    [TestCase("", ".mp3")]
    public async Task Download_ExtensionlessEnclosure_UsesPortableAudioTypeFileName(string? type, string extension)
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1) with { Enclosure = new Uri("https://cdn.example/download?id=1"), MediaType = type });
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        StoredPodcastEpisode episode = await service.DownloadAsync(id, "a");
        Assert.That(Path.GetExtension(episode.LocalPath), Is.EqualTo(extension));
        Assert.That(await File.ReadAllBytesAsync(episode.LocalPath!), Is.EqualTo(downloader.Payload));
        Assert.That((await repository.ListEpisodesAsync(id)).Single().LocalPath, Is.EqualTo(episode.LocalPath));
    }

    [Test]
    public async Task Download_RecognizedExtension_TakesPriorityOverGenericServerType()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1) with { Enclosure = new Uri("https://cdn.example/AUDIO.FLAC"), MediaType = "application/octet-stream" });
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        Assert.That(Path.GetExtension((await service.DownloadAsync(id, "a")).LocalPath), Is.EqualTo(".flac"));
    }

    [Test]
    public async Task Subscribe_ConcurrentSameFeed_ReportsOneCreatedSubscriptionAndOneExistingIdentity()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        reader.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PodcastSubscribeResult> first = service.SubscribeAsync(FeedUrl.AbsoluteUri);
        Task<PodcastSubscribeResult> second = service.SubscribeAsync(FeedUrl.AbsoluteUri);
        Assert.That(reader.Requests, Has.Count.EqualTo(2));
        reader.Gate.SetResult();
        PodcastSubscribeResult[] results = await Task.WhenAll(first, second);
        Assert.That(results.Count(result => result.Added), Is.EqualTo(1));
        Assert.That(results.Select(result => result.Subscription.Id).Distinct().Count(), Is.EqualTo(1));
        Assert.That(await repository.ListSubscriptionsAsync(), Has.Count.EqualTo(1));
        Assert.That(await repository.ListEpisodesAsync(results[0].Subscription.Id), Has.Count.EqualTo(1));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Refresh_ConcurrentRequestAndRemovalWhileReading_DoesNotResurrectSubscription(bool notModified)
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        reader.Next = notModified ? new(null, [], NotModified: true) : Fresh("Changed", Episode("b", "B", 1));
        reader.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = service.RefreshAsync(id);
        Assert.That(reader.Requests, Has.Count.EqualTo(2));
        Assert.That(async () => await service.RefreshAsync(id), Throws.TypeOf<InvalidOperationException>().With.Message.Contains("already refreshing"));
        await repository.RemoveSubscriptionAsync(id);
        reader.Gate.SetResult();
        Assert.That(async () => await pending, Throws.TypeOf<InvalidOperationException>().With.Message.Contains("no longer exists"));
        Assert.That(async () => await service.RefreshAsync(id), Throws.TypeOf<InvalidOperationException>().With.Message.Contains("no longer exists"));
        Assert.That(await repository.ListSubscriptionsAsync(), Is.Empty);
        Assert.That(await repository.ListEpisodesAsync(id), Is.Empty);
    }

    [Test]
    public async Task Subscribe_MissingFeedBody_DoesNotCreateAnEmptySubscription()
    {
        await using SqliteTrackRepository repository = new(database);
        reader.Next = new(null, []);
        Assert.That(async () => await Service(repository).SubscribeAsync(FeedUrl.AbsoluteUri), Throws.TypeOf<InvalidOperationException>());
        Assert.That(await repository.ListSubscriptionsAsync(), Is.Empty);
    }

    [Test]
    public async Task Play_MissingDownload_ReportsRecoveryWithoutCallingPlayer()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastSubscription subscription = new(Guid.NewGuid(), FeedUrl, "Show", "", DateTimeOffset.UtcNow);
        StoredPodcastEpisode episode = new(subscription.Id, "a", "A", new Uri("https://cdn.example/a.mp3"), LocalPath: Path.Combine(downloads, "missing.mp3"));
        Assert.That(async () => await Service(repository).PlayAsync(subscription, episode, new UnexpectedHandoff()),
            Throws.TypeOf<FileNotFoundException>().With.Message.Contains("Download the episode again"));
    }

    [Test]
    public async Task Download_RecordingThrows_KeepsCompletedBytesAndReportsRecoverablePath()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        await using (SqliteConnection connection = new($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            // A real destination write failure exercises recovery after the completed file is committed.
            command.CommandText = "CREATE TRIGGER reject_download BEFORE UPDATE OF local_path ON podcast_episodes BEGIN SELECT RAISE(ABORT, 'record unavailable'); END;";
            await command.ExecuteNonQueryAsync();
        }
        IOException? error = null;
        try { await service.DownloadAsync(id, "a"); }
        catch (IOException exception) { error = exception; }
        string path = Directory.GetFiles(downloads, "*", SearchOption.AllDirectories).Single();
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.InnerException, Is.TypeOf<SqliteException>());
        Assert.That(error.Message, Does.Contain(path).And.Contain("kept"));
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(downloader.Payload));
        Assert.That((await repository.ListEpisodesAsync(id)).Single().LocalPath, Is.Null);
        Assert.That(service.IsDownloading(id, "a"), Is.False);
    }

    [Test]
    public async Task Subscribe_UntrustedFeed_SkipsUnsafeIdentitiesAndNormalizesOptionalFields()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastEpisode basis = Episode("valid", "  T\aext\n\tvalue  ", 1) with { Length = -1, Duration = null, Description = null! };
        reader.Next = new(new(FeedUrl, null!, "  About\nshow\ttext\a  ",
        [basis with { Id = null! }, basis with { Id = " " }, basis with { Id = new string('x', 4097) },
         basis with { Id = "unsafe", Enclosure = new Uri("file:///etc/passwd") }, basis,
         basis with { Title = "Ignored duplicate" }, basis with { Id = "empty-title", Title = null!, Length = null, Duration = TimeSpan.Zero }]), []);
        PodcastSubscribeResult result = await Service(repository).SubscribeAsync(FeedUrl.AbsoluteUri);
        IReadOnlyList<StoredPodcastEpisode> episodes = await repository.ListEpisodesAsync(result.Subscription.Id);
        Assert.That(result.Subscription.Title, Is.EqualTo("Untitled podcast"));
        Assert.That(result.Subscription.Description, Is.EqualTo("About\nshow\ttext"));
        Assert.That(episodes, Has.Count.EqualTo(2));
        StoredPodcastEpisode valid = episodes.Single(item => item.EpisodeId == "valid");
        Assert.That(valid.Title, Is.EqualTo("Text\n\tvalue"));
        Assert.That(valid.Length, Is.Null);
        Assert.That(valid.Duration, Is.Null);
        Assert.That(valid.Description, Is.Empty);
        Assert.That(episodes.Single(item => item.EpisodeId == "empty-title").Title, Is.EqualTo("Untitled episode"));
    }

    private sealed class UnexpectedHandoff : IPodcastPlaybackHandoff
    {
        public Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("A missing episode must never reach the player.");
    }
}
