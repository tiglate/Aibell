using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Podcasts;

public sealed partial class PodcastServiceTests
{
    [Test]
    public async Task NewEpisodes_OnlyRefreshInsertionsAreNewAndSeenIsPersistent()
    {
        reader.Next = Fresh("Show", Episode("a", "A", 2), Episode("b", "B", 1));
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = Service(repository);
        Guid id = (await service.SubscribeAsync(FeedUrl.AbsoluteUri)).Subscription.Id;
        Assert.That(await service.CountNewEpisodesAsync(), Is.Empty, "The first subscribe is not 'new'.");

        reader.Next = Fresh("Show", Episode("a", "A", 2), Episode("b", "B", 1), Episode("c", "C", 3), Episode("d", "D", 4));
        await service.RefreshAsync(id);
        Assert.That((await service.CountNewEpisodesAsync())[id], Is.EqualTo(2));
        IReadOnlyList<StoredPodcastEpisode> listed = await service.ListEpisodesAsync(id);
        Assert.That(listed.Where(item => item.IsNew).Select(item => item.EpisodeId), Is.EquivalentTo(new List<string> { "c", "d" }));

        reader.Next = Fresh("Show", Episode("a", "A edited", 2), Episode("b", "B", 1), Episode("c", "C", 3), Episode("d", "D", 4));
        await service.RefreshAsync(id);
        Assert.That((await service.CountNewEpisodesAsync())[id], Is.EqualTo(2), "Updating an existing row never changes its flag.");

        Assert.That(await service.MarkEpisodesSeenAsync(id, ["c", "unknown"]), Is.EqualTo(1));
        Assert.That((await service.CountNewEpisodesAsync())[id], Is.EqualTo(1));
        Assert.That(await service.MarkEpisodesSeenAsync(id, []), Is.Zero);
        Assert.That(await service.MarkEpisodesSeenAsync(id), Is.EqualTo(1), "Null clears the whole subscription.");
        Assert.That(await service.CountNewEpisodesAsync(), Is.Empty);
    }

    [Test]
    public async Task RefreshAll_RefreshesEveryFeedReportsProgressAndContinuesPastAFailure()
    {
        MultiReader feeds = new();
        Uri one = new("https://one.example/feed.xml"), two = new("https://two.example/feed.xml"), three = new("https://three.example/feed.xml");
        feeds.Feeds[one] = Feed(one, "One", Episode("a", "A", 1));
        feeds.Feeds[two] = Feed(two, "Two", Episode("x", "X", 1));
        feeds.Feeds[three] = Feed(three, "Three", Episode("m", "M", 1));
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = new(repository, feeds, downloader, new Aibell.Infrastructure.Podcasts.PodcastDownloadStore(downloads));
        foreach (Uri url in new[] { one, two, three }) await service.SubscribeAsync(url.AbsoluteUri);

        feeds.Feeds[one] = Feed(one, "One", Episode("a", "A", 1), Episode("b", "B", 2));
        feeds.Failures[two] = new HttpRequestException("offline");
        feeds.Feeds[three] = Feed(three, "Three", Episode("m", "M", 1), Episode("n", "N", 2), Episode("o", "O", 3));
        List<PodcastRefreshAllProgress> seen = [];
        PodcastRefreshAllResult result = await service.RefreshAllAsync(new Collecting(seen));

        Assert.Multiple(() =>
        {
            Assert.That(result.Subscriptions, Is.EqualTo(3));
            Assert.That(result.Refreshed, Is.EqualTo(2));
            Assert.That(result.NewEpisodes, Is.EqualTo(3));
            Assert.That(result.Failures.Single().Message, Is.EqualTo("offline"));
            Assert.That(seen.Select(item => item.Position), Is.EqualTo(new List<int> { 1, 2, 3 }));
            Assert.That(seen.All(item => item.Total == 3), Is.True);
        });
        Assert.That((await service.CountNewEpisodesAsync()).Values.Sum(), Is.EqualTo(3));
    }

    [Test]
    public async Task RefreshAll_Cancelled_StopsBetweenFeeds()
    {
        MultiReader feeds = new();
        Uri one = new("https://one.example/feed.xml"), two = new("https://two.example/feed.xml");
        feeds.Feeds[one] = Feed(one, "One", Episode("a", "A", 1));
        feeds.Feeds[two] = Feed(two, "Two", Episode("x", "X", 1));
        await using SqliteTrackRepository repository = new(database);
        PodcastService service = new(repository, feeds, downloader, new Aibell.Infrastructure.Podcasts.PodcastDownloadStore(downloads));
        await service.SubscribeAsync(one.AbsoluteUri); await service.SubscribeAsync(two.AbsoluteUri);
        using CancellationTokenSource cancellation = new();
        feeds.OnRead = () => cancellation.Cancel();
        int before = feeds.Reads;
        await Assert.ThatAsync(async () => await service.RefreshAllAsync(null, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(feeds.Reads - before, Is.EqualTo(1), "The second feed is never contacted.");
    }

    private static PodcastFeedResult Feed(Uri url, string title, params PodcastEpisode[] episodes) =>
        new(new PodcastFeed(url, title, "About", episodes), [], EntityTag: null, LastModified: null);

    private sealed class Collecting(List<PodcastRefreshAllProgress> sink) : IProgress<PodcastRefreshAllProgress>
    {
        public void Report(PodcastRefreshAllProgress value) => sink.Add(value);
    }

    private sealed class MultiReader : IPodcastFeedReader
    {
        public Dictionary<Uri, PodcastFeedResult> Feeds { get; } = [];
        public Dictionary<Uri, Exception> Failures { get; } = [];
        public Action? OnRead { get; set; }
        public int Reads { get; private set; }
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default)
        {
            Reads++;
            OnRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failures.TryGetValue(request.Source, out Exception? failure)) throw failure;
            return Task.FromResult(Feeds[request.Source]);
        }
    }
}
