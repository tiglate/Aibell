using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Podcasts;

[TestFixture]
public sealed partial class PodcastServiceTests
{
    private static readonly Uri FeedUrl = new("https://podcast.example/feed.xml");
    private string directory = null!;
    private string database = null!;
    private string downloads = null!;
    private FakeReader reader = null!;
    private FakeDownloader downloader = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-podcasts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        database = Path.Combine(directory, "library.db"); downloads = Path.Combine(directory, "podcasts");
        reader = new(); downloader = new();
    }

    [TearDown]
    public void TearDown() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }

    private PodcastService Service(SqliteTrackRepository repository) => new(repository, reader, downloader, new Aibell.Infrastructure.Podcasts.PodcastDownloadStore(downloads));

    [Test]
    public async Task Subscribe_PersistsFeedAndEpisodes_ThenListsThemAfterReopenWithoutNetwork()
    {
        reader.Next = Fresh("Show", Episode("a", "A", 2), Episode("b", "B", 1));
        Guid id;
        await using (SqliteTrackRepository repository = new(database))
        {
            PodcastSubscribeResult result = await Service(repository).SubscribeAsync("  https://podcast.example/feed.xml ");
            id = result.Subscription.Id;
            Assert.Multiple(() => { Assert.That(result.Added, Is.True); Assert.That(result.EpisodeCount, Is.EqualTo(2)); });
        }
        int reads = reader.Requests.Count;
        await using SqliteTrackRepository reopened = new(database);
        PodcastService service = Service(reopened);
        PodcastSubscription subscription = (await service.ListSubscriptionsAsync()).Single();
        var episodes = await service.ListEpisodesAsync(id);
        Assert.Multiple(() =>
        {
            Assert.That(subscription.Title, Is.EqualTo("Show")); Assert.That(subscription.EntityTag, Is.EqualTo("\"v1\""));
            Assert.That(episodes.Select(e => e.EpisodeId), Is.EqualTo(NewestFirst), "newest first");
            Assert.That(episodes[0].Duration, Is.EqualTo(TimeSpan.FromMinutes(3)));
            Assert.That(reader.Requests, Has.Count.EqualTo(reads), "listing is offline");
        });
    }

    [Test]
    public async Task Subscribe_DuplicateOrUnsafeUrl_DoesNotContactNetworkOrCreateSecondRow()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        await service.SubscribeAsync(FeedUrl.AbsoluteUri);
        PodcastSubscribeResult duplicate = await service.SubscribeAsync("HTTPS://PODCAST.EXAMPLE/feed.xml");

        Assert.Multiple(() =>
        {
            Assert.That(duplicate.Added, Is.False);
            Assert.That(reader.Requests, Has.Count.EqualTo(1));
            Assert.That(repository.ListSubscriptionsAsync().Result, Has.Count.EqualTo(1));
        });
        foreach (string bad in BadUrls)
            Assert.That(async () => await service.SubscribeAsync(bad), Throws.TypeOf<ArgumentException>(), bad);
        Assert.That(reader.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Subscribe_FailureOrCancellation_LeavesNoSubscription()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Failure = new HttpRequestException("offline");
        Assert.That(async () => await service.SubscribeAsync(FeedUrl.AbsoluteUri), Throws.TypeOf<HttpRequestException>());
        reader.Failure = null; reader.Next = Fresh("Show"); reader.Gate = new TaskCompletionSource();
        using CancellationTokenSource cancel = new();
        Task pending = service.SubscribeAsync(FeedUrl.AbsoluteUri, cancel.Token);
        await cancel.CancelAsync();
        Assert.That(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(await repository.ListSubscriptionsAsync(), Is.Empty);
    }

    [Test]
    public async Task Refresh_NotModified_SendsValidatorsKeepsEpisodesAndRecordsTime()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        reader.Next = new(null, [], NotModified: true);
        PodcastRefreshResult result = await service.RefreshAsync(id);

        PodcastFeedRequest sent = reader.Requests.Last();
        Assert.Multiple(() =>
        {
            Assert.That(result.NotModified, Is.True);
            Assert.That(sent.EntityTag, Is.EqualTo("\"v1\"")); Assert.That(sent.ModifiedSince, Is.EqualTo(Modified));
            Assert.That(repository.ListEpisodesAsync(id).Result, Has.Count.EqualTo(1));
            Assert.That(repository.ListSubscriptionsAsync().Result.Single().EntityTag, Is.EqualTo("\"v1\""), "validators kept on 304");
        });
    }

    [Test]
    public async Task Refresh_Modified_UpsertsKeepsDownloadedStateAndDroppedEpisodesAndReplacesValidators()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1), Episode("gone", "Gone", 0));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        StoredPodcastEpisode downloaded = await service.DownloadAsync(id, "a");

        reader.Next = new(new PodcastFeed(FeedUrl, "Renamed", "d", [Episode("a", "A edited", 1), Episode("new", "New", 5)]), [], EntityTag: null);
        PodcastRefreshResult result = await service.RefreshAsync(id);

        var episodes = await repository.ListEpisodesAsync(id);
        StoredPodcastEpisode a = episodes.Single(e => e.EpisodeId == "a");
        Assert.Multiple(() =>
        {
            Assert.That(result.NewEpisodes, Is.EqualTo(1));
            Assert.That(episodes.Select(e => e.EpisodeId), Is.EquivalentTo(AfterRefresh));
            Assert.That(a.Title, Is.EqualTo("A edited"));
            Assert.That(a.LocalPath, Is.EqualTo(downloaded.LocalPath)); Assert.That(a.DownloadState, Is.EqualTo(PodcastDownloadState.Downloaded));
            Assert.That(repository.ListSubscriptionsAsync().Result.Single(), Has.Property(nameof(PodcastSubscription.EntityTag)).Null
                .And.Property(nameof(PodcastSubscription.Title)).EqualTo("Renamed"));
        });
    }

    [Test]
    public async Task Refresh_ErrorOrBadFeed_ChangesNothing()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        PodcastSubscription before = (await repository.ListSubscriptionsAsync()).Single();
        reader.Failure = new TimeoutException("slow");
        Assert.That(async () => await service.RefreshAsync(id), Throws.TypeOf<TimeoutException>());
        reader.Failure = null; reader.Next = new(null, []);
        Assert.That(async () => await service.RefreshAsync(id), Throws.TypeOf<InvalidOperationException>());
        Assert.That((await repository.ListSubscriptionsAsync()).Single(), Is.EqualTo(before));
        Assert.That(await repository.ListEpisodesAsync(id), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Refresh_InvalidBatch_ChangesNothing()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        PodcastSubscription current = (await repository.ListSubscriptionsAsync()).Single();
        StoredPodcastEpisode ok = new(id, "ok", "Ok", new Uri("https://e.example/ok.mp3"));
        StoredPodcastEpisode bad = ok with { EpisodeId = "bad", Length = -5 }; // violates the length CHECK

        Assert.That(async () => await repository.ApplyRefreshAsync(current with { Title = "Changed" }, [ok, bad]), Throws.TypeOf<ArgumentException>());

        Assert.That((await repository.ListSubscriptionsAsync()).Single().Title, Is.EqualTo("Show"));
        Assert.That((await repository.ListEpisodesAsync(id)).Select(e => e.EpisodeId), Is.EqualTo(OnlyA));
    }

    [Test]
    public async Task Download_UsesApplicationChosenPathRecordsStateAndSurvivesReopen()
    {
        Guid id;
        StoredPodcastEpisode downloaded;
        await using (SqliteTrackRepository repository = new(database))
        {
            PodcastService service = Service(repository);
            reader.Next = Fresh("Show", Episode("../../etc/passwd?x=1", "Evil id", 1));
            id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
            downloaded = await service.DownloadAsync(id, "../../etc/passwd?x=1");
        }
        await using SqliteTrackRepository reopened = new(database);
        PodcastService again = Service(reopened);
        StoredPodcastEpisode stored = (await again.ListEpisodesAsync(id)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(Path.GetDirectoryName(downloaded.LocalPath), Is.EqualTo(Path.Combine(downloads, id.ToString("N"))));
            Assert.That(Path.GetExtension(downloaded.LocalPath), Is.EqualTo(".mp3"));
            Assert.That(stored.LocalPath, Is.EqualTo(downloaded.LocalPath)); Assert.That(stored.DownloadedBytes, Is.EqualTo(downloader.Payload.Length));
            Assert.That(downloader.Requests.Single().MaximumBytes, Is.EqualTo(1024L * 1024 * 1024));
            Assert.That(again.ResolvePlayableFile(stored), Is.EqualTo(new Uri(downloaded.LocalPath!)));
            Assert.That(Directory.GetFiles(downloads, "*", SearchOption.AllDirectories), Has.Length.EqualTo(1));
        });
        Assert.That(async () => await again.DownloadAsync(id, stored.EpisodeId), Throws.Nothing, "already downloaded");
        Assert.That(downloader.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Download_FailureOrCancellation_RecordsNothingAndLeavesNoFile()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;

        downloader.Failure = new HttpRequestException("503");
        Assert.That(async () => await service.DownloadAsync(id, "a"), Throws.TypeOf<HttpRequestException>());
        Assert.That(service.IsDownloading(id, "a"), Is.False);

        downloader.Failure = null; downloader.Gate = new TaskCompletionSource();
        using CancellationTokenSource cancel = new();
        Task pending = service.DownloadAsync(id, "a", cancellationToken: cancel.Token);
        await downloader.Started.Task;
        Assert.That(service.IsDownloading(id, "a"), Is.True);
        Assert.That(async () => await service.DownloadAsync(id, "a"), Throws.TypeOf<InvalidOperationException>(), "one download per episode");
        await cancel.CancelAsync();
        Assert.That(async () => await pending, Throws.InstanceOf<OperationCanceledException>());

        Assert.Multiple(() =>
        {
            Assert.That(repository.ListEpisodesAsync(id).Result.Single().DownloadState, Is.EqualTo(PodcastDownloadState.NotDownloaded));
            Assert.That(service.IsDownloading(id, "a"), Is.False);
            Assert.That(Directory.Exists(downloads) ? Directory.GetFiles(downloads, "*", SearchOption.AllDirectories) : [], Is.Empty);
        });
    }

    [Test]
    public async Task Download_ExistingUnrecordedFileAtDestination_IsNeverReplaced()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        downloader.Gate = null;
        await service.DownloadAsync(id, "a");
        string path = (await repository.ListEpisodesAsync(id)).Single().LocalPath!;
        await repository.ClearDownloadAsync(id, "a");
        await File.WriteAllTextAsync(path, "user data");

        Assert.That(async () => await service.DownloadAsync(id, "a"), Throws.TypeOf<IOException>());
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("user data"));
    }

    [Test]
    public async Task Remove_DeletesRowsOnlyAndLeavesDownloadedAndUnrelatedFiles()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        StoredPodcastEpisode episode = await service.DownloadAsync(id, "a");
        string unrelated = Path.Combine(directory, "music.mp3"); await File.WriteAllTextAsync(unrelated, "x");

        int kept = await service.RemoveAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(kept, Is.EqualTo(1));
            Assert.That(repository.ListSubscriptionsAsync().Result, Is.Empty);
            Assert.That(repository.ListEpisodesAsync(id).Result, Is.Empty);
            Assert.That(File.Exists(episode.LocalPath), Is.True); Assert.That(File.Exists(unrelated), Is.True);
        });
        Assert.That(async () => await service.RemoveAsync(id), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task Remove_DuringDownload_CancelsItAndLeavesNoPartialFile()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        downloader.Gate = new TaskCompletionSource();
        Task pending = service.DownloadAsync(id, "a");
        await downloader.Started.Task;
        await service.RemoveAsync(id);
        Assert.That(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Directory.GetFiles(downloads, "*", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task ResolvePlayableFile_MissingOrOutsideDownloadFolder_ReturnsNull()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        string outside = Path.Combine(directory, "other.mp3"); await File.WriteAllTextAsync(outside, "x");
        StoredPodcastEpisode episode = new(Guid.NewGuid(), "a", "A", new Uri("https://e.example/a.mp3"), LocalPath: outside);
        Assert.Multiple(() =>
        {
            Assert.That(service.ResolvePlayableFile(episode), Is.Null);
            Assert.That(service.ResolvePlayableFile(episode with { LocalPath = Path.Combine(downloads, "missing.mp3") }), Is.Null);
            Assert.That(service.ResolvePlayableFile(episode with { LocalPath = null }), Is.Null);
        });
    }

    [Test]
    public async Task Subscribe_LongOrControlTextIsTruncatedNotRejected()
    {
        await using SqliteTrackRepository repository = new(database);
        reader.Next = Fresh(new string('T', 900) + "\u0007", Episode("a", new string('E', 900), 1));
        PodcastSubscribeResult result = await Service(repository).SubscribeAsync(FeedUrl.AbsoluteUri);
        Assert.Multiple(() =>
        {
            Assert.That(result.Subscription.Title, Has.Length.EqualTo(500));
            Assert.That(repository.ListEpisodesAsync(result.Subscription.Id).Result.Single().Title, Has.Length.EqualTo(500));
        });
    }

    [Test]
    public async Task Initialize_VersionTenProfile_AddsEmptyPodcastTablesAndKeepsExistingData()
    {
        await using (SqliteTrackRepository first = new(database)) { await first.ListSubscriptionsAsync(); }
        await using (SqliteConnection connection = new($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX ix_radio_stations_catalog_id; ALTER TABLE radio_stations DROP COLUMN country; ALTER TABLE radio_stations DROP COLUMN country_code; ALTER TABLE radio_stations DROP COLUMN logo_url; ALTER TABLE radio_stations DROP COLUMN catalog_id;
                INSERT INTO radio_stations (id, name, stream) VALUES ('11111111-1111-1111-1111-111111111111', 'Keep', 'https://r.example/live');
                DROP TABLE podcast_playback_positions; DROP TRIGGER clear_podcast_position_after_download_change; DROP TABLE audiobook_shelf; DROP TABLE podcast_episodes; DROP TABLE podcast_subscriptions; PRAGMA user_version=10;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await using SqliteTrackRepository upgraded = new(database);
        Assert.That(await upgraded.ListSubscriptionsAsync(), Is.Empty);
        Assert.That((await upgraded.ListStationsAsync()).Single().Name, Is.EqualTo("Keep"));
    }

    private static readonly string[] NewestFirst = ["a", "b"], OnlyA = ["a"], AfterRefresh = ["a", "gone", "new"];
    private static readonly string[] BadUrls = ["", "ftp://x/feed", "https://user:pw@podcast.example/feed", "https://podcast.example/f#frag", "file:///etc/passwd", "not a url"];
    [Test]
    public async Task Download_RecordCannotBeStored_KeepsTheFinishedFileAndReportsItsPath()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        downloader.Gate = new TaskCompletionSource();
        Task pending = service.DownloadAsync(id, "a");
        await downloader.Started.Task;
        await repository.RemoveSubscriptionAsync(id); // bypass the service so the transfer is not cancelled
        downloader.Gate.SetResult();

        InvalidOperationException error = await CaptureAsync(pending);
        string[] files = Directory.GetFiles(downloads, "*", SearchOption.AllDirectories);
        Assert.Multiple(() =>
        {
            Assert.That(files, Has.Length.EqualTo(1));
            Assert.That(error.Message, Does.Contain(files[0]).And.Contain("kept"));
        });
    }

    [Test]
    public async Task Download_StoredPathOutsideFolderOrThroughLink_IsNotTreatedAsDownloaded()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        string outside = Path.Combine(directory, "precious.mp3"); await File.WriteAllTextAsync(outside, "keep");
        await repository.RecordDownloadAsync(id, "a", outside, 4, DateTimeOffset.UtcNow);

        StoredPodcastEpisode result = await service.DownloadAsync(id, "a");

        Assert.Multiple(() =>
        {
            Assert.That(downloader.Requests, Has.Count.EqualTo(1));
            Assert.That(result.LocalPath, Is.Not.EqualTo(outside));
            Assert.That(File.ReadAllText(outside), Is.EqualTo("keep"));
        });
    }

    [Test]
    public async Task Store_SymbolicLinkAncestorOrFile_IsRejectedWithoutWritingOutside()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Symlink creation needs privileges on Windows; junction behavior is a native Windows gate.");
        Aibell.Infrastructure.Podcasts.PodcastDownloadStore store = new(downloads);
        Directory.CreateDirectory(downloads);
        string outside = Path.Combine(directory, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "song.mp3"), "x");
        Guid sub = Guid.NewGuid();
        Directory.CreateSymbolicLink(Path.Combine(downloads, sub.ToString("N")), outside);

        Assert.That(() => store.PrepareDestination(sub, "a.mp3"), Throws.TypeOf<IOException>());
        Assert.That(store.ResolveExisting(Path.Combine(downloads, sub.ToString("N"), "song.mp3")), Is.Null);
        Assert.That(Directory.GetFiles(outside), Has.Length.EqualTo(1));

        Guid real = Guid.NewGuid();
        Uri destination = store.PrepareDestination(real, "ok.mp3");
        await File.WriteAllTextAsync(destination.LocalPath, "y");
        Assert.That(store.ResolveExisting(destination.LocalPath), Is.EqualTo(destination));
        string linkFile = Path.Combine(downloads, real.ToString("N"), "link.mp3");
        File.CreateSymbolicLink(linkFile, Path.Combine(outside, "song.mp3"));
        Assert.That(store.ResolveExisting(linkFile), Is.Null);
        Assert.That(() => store.PrepareDestination(real, "link.mp3"), Throws.TypeOf<IOException>());
        Assert.That(() => store.PrepareDestination(real, "../escape.mp3"), Throws.TypeOf<IOException>());
        Assert.That(store.ResolveExisting(Path.Combine(downloads, "..", "outside", "song.mp3")), Is.Null);
    }

    [Test]
    public async Task Repository_InvalidEpisodeBatch_IsRejectedBeforeAnyChange()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        reader.Next = Fresh("Show", Episode("a", "A", 1));
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        PodcastSubscription current = (await repository.ListSubscriptionsAsync()).Single();
        StoredPodcastEpisode ok = new(id, "ok", "Ok", new Uri("https://e.example/ok.mp3"));
        StoredPodcastEpisode[][] bad =
        [
            [ok, ok with { SubscriptionId = Guid.NewGuid(), EpisodeId = "other" }],
            [ok, ok],
            [ok, ok with { EpisodeId = "n", Duration = TimeSpan.FromSeconds(-1) }],
            [ok, ok with { EpisodeId = "c", Enclosure = new Uri("https://u:p@e.example/c.mp3") }],
            [ok, ok with { EpisodeId = new string('x', 4097) }],
            [.. Enumerable.Range(0, 5001).Select(n => ok with { EpisodeId = "e" + n })]
        ];
        foreach (StoredPodcastEpisode[] batch in bad)
            Assert.That(async () => await repository.ApplyRefreshAsync(current with { Title = "Changed" }, batch), Throws.TypeOf<ArgumentException>());
        Assert.That(async () => await repository.AddSubscriptionAsync(current with { Id = Guid.NewGuid(), FeedUrl = new Uri("https://other.example/f") }, bad[0]),
            Throws.TypeOf<ArgumentException>());

        Assert.That((await repository.ListSubscriptionsAsync()).Select(s => s.Title), Is.EqualTo(ShowOnly));
        Assert.That((await repository.ListEpisodesAsync(id)).Select(e => e.EpisodeId), Is.EqualTo(OnlyA));
    }

    [Test]
    public async Task Subscribe_OversizedUntrustedFeed_IsBoundedAndNegativeDurationOmitted()
    {
        await using SqliteTrackRepository repository = new(database);
        PodcastEpisode[] many = [.. Enumerable.Range(0, 5100).Select(n => Episode("e" + n, "T" + n, 1))];
        many[0] = many[0] with { Duration = TimeSpan.FromSeconds(-5) };
        reader.Next = Fresh("Show", many);
        PodcastSubscribeResult result = await Service(repository).SubscribeAsync(FeedUrl.AbsoluteUri);
        var stored = await repository.ListEpisodesAsync(result.Subscription.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.EpisodeCount, Is.EqualTo(5000)); Assert.That(stored, Has.Count.EqualTo(5000));
            Assert.That(stored.Single(e => e.EpisodeId == "e0").Duration, Is.Null);
        });
    }

    private static readonly string[] ShowOnly = ["Show"];
    private static async Task<InvalidOperationException> CaptureAsync(Task task)
    {
        try { await task; } catch (InvalidOperationException exception) { return exception; }
        throw new InvalidOperationException("The task did not fail.");
    }

    private static readonly DateTimeOffset Modified = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static PodcastEpisode Episode(string id, string title, int day) =>
        new(id, title, new Uri($"https://cdn.example/{day}.mp3"), "audio/mpeg", 100, new DateTimeOffset(2026, 9, 1 + day, 0, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(3), "notes");

    private static PodcastFeedResult Fresh(string title, params PodcastEpisode[] episodes) =>
        new(new PodcastFeed(FeedUrl, title, "About", episodes), [], EntityTag: "\"v1\"", LastModified: Modified);

    private sealed class FakeReader : IPodcastFeedReader
    {
        public List<PodcastFeedRequest> Requests { get; } = [];
        public PodcastFeedResult? Next { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public async Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Failure is not null) throw Failure;
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            return Next!;
        }
    }

    private sealed class FakeDownloader : IPodcastEpisodeDownloader
    {
        public byte[] Payload { get; } = [1, 2, 3, 4, 5];
        public List<PodcastDownloadRequest> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnStarted { get; set; }
        public async Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Failure is not null) throw Failure;
            Started.TrySetResult();
            OnStarted?.Invoke();
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            await File.WriteAllBytesAsync(request.Destination.LocalPath, Payload, cancellationToken);
            return new(request.Destination, Payload.Length);
        }
    }
}
