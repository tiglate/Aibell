using System.Security.Cryptography;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Podcasts;

[TestFixture]
public sealed class PodcastWorkflowStorageTests
{
    private string directory = null!;
    [SetUp] public void SetUp() => directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-podcast-workflow-" + Guid.NewGuid().ToString("N"))).FullName;
    [TearDown] public void TearDown() => Directory.Delete(directory, true);

    [Test]
    public async Task ImageUrl_RoundTripsRefreshesAndRejectsUnsupportedSchemes()
    {
        Guid id = Guid.NewGuid();
        PodcastSubscription subscription = new(id, new Uri("https://example.invalid/feed"), "Show", "", DateTimeOffset.UtcNow);
        StoredPodcastEpisode episode = new(id, "episode", "Episode", new Uri("https://example.invalid/audio.mp3"),
            ImageUrl: new Uri("https://example.invalid/one.jpg"));
        string database = Path.Combine(directory, "images.db");
        await using (SqliteTrackRepository repository = new(database))
        {
            await repository.AddSubscriptionAsync(subscription, [episode]);
            Assert.That((await repository.ListEpisodesAsync(id)).Single().ImageUrl, Is.EqualTo(episode.ImageUrl));
            await repository.ApplyRefreshAsync(subscription, [episode with { ImageUrl = new Uri("https://example.invalid/two.jpg") }]);
            Assert.That((await repository.ListEpisodesAsync(id)).Single().ImageUrl, Is.EqualTo(new Uri("https://example.invalid/two.jpg")));
            await repository.ApplyRefreshAsync(subscription, [episode with { ImageUrl = null }]);
            Assert.That((await repository.ListEpisodesAsync(id)).Single().ImageUrl, Is.Null, "A feed that drops the cover clears it.");
            Assert.That(() => repository.ApplyRefreshAsync(subscription, [episode with { ImageUrl = new Uri("file:///tmp/x.png") }]),
                Throws.TypeOf<ArgumentException>());
        }
    }

    [Test]
    public async Task Rating_RefreshReopenDownloadClearAndSubscriptionRemoval_PreserveOnlyOwnedState()
    {
        string database = Path.Combine(directory, "profile.db");
        Guid id = Guid.NewGuid();
        PodcastSubscription subscription = new(id, new Uri("https://example.invalid/feed"), "Show", "", DateTimeOffset.UtcNow);
        StoredPodcastEpisode episode = new(id, "episode", "Episode", new Uri("https://example.invalid/audio.mp3"));
        await using (SqliteTrackRepository repository = new(database))
        {
            await repository.AddSubscriptionAsync(subscription, [episode]);
            Assert.That(await repository.SetEpisodeRatingAsync(id, episode.EpisodeId, 5), Is.True);
            await repository.RecordDownloadAsync(id, episode.EpisodeId, Path.Combine(directory, "keep.mp3"), 123, DateTimeOffset.UtcNow);
            await repository.SaveEpisodePositionAsync(id, episode.EpisodeId, Path.Combine(directory, "keep.mp3"),
                new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow));
            await repository.ApplyRefreshAsync(subscription, [episode with { Title = "New title", Rating = 1 }]);
            await repository.ApplyRefreshAsync(subscription, []);
            await repository.ApplyRefreshAsync(subscription, null);
            Assert.That((await repository.ListEpisodesAsync(id)).Single().Rating, Is.EqualTo(5));
            await repository.ClearDownloadAsync(id, episode.EpisodeId);
        }
        await using SqliteTrackRepository reopened = new(database);
        StoredPodcastEpisode restored = (await reopened.ListEpisodesAsync(id)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(restored.Rating, Is.EqualTo(5));
            Assert.That(restored.Title, Is.EqualTo("New title"));
            Assert.That(restored.LocalPath, Is.Null);
            Assert.That(restored.SavedProgress, Is.Null);
        });
        Assert.That(await reopened.SetEpisodeRatingAsync(id, episode.EpisodeId, 0), Is.True);
        Assert.That((await reopened.ListEpisodesAsync(id)).Single().Rating, Is.Zero);
        Assert.That(await reopened.SetEpisodeRatingAsync(id, "missing", 4), Is.False);
        Assert.That(async () => await reopened.SetEpisodeRatingAsync(id, episode.EpisodeId, 6), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => (episode with { Rating = -1 }).Validate(), Throws.TypeOf<ArgumentOutOfRangeException>());
        await reopened.RemoveSubscriptionAsync(id);
        Assert.That(await reopened.ListEpisodesAsync(id), Is.Empty);
    }

    [Test]
    public void PreparedConfiguration_FailedValidationAndDeferredApply_KeepPreviousRoot()
    {
        string original = Path.Combine(directory, "old");
        string proposed = Path.Combine(directory, "new");
        PodcastDownloadStore store = new(original);
        Assert.That(Directory.Exists(original), Is.False, "startup never creates the configured directory");
        PodcastDownloadConfiguration prepared = PodcastDownloadStore.PrepareConfiguration(proposed, [original]);
        Assert.That(store.Root, Is.EqualTo(original), "validation alone cannot redirect live downloads");
        Assert.That(Directory.GetFiles(proposed), Is.Empty, "writability probe is removed");
        Assert.That(() => PodcastDownloadStore.PrepareConfiguration("relative/downloads"), Throws.TypeOf<ArgumentException>());
        string blocker = Path.Combine(directory, "file"); File.WriteAllText(blocker, "keep");
        Assert.That(() => PodcastDownloadStore.PrepareConfiguration(blocker), Throws.TypeOf<IOException>());
        Assert.That(store.Root, Is.EqualTo(original));
        // Applying a previously validated immutable config is memory only, even if the device went away meanwhile.
        Directory.Delete(proposed);
        store.ApplyConfiguration(prepared);
        Assert.That(store.Root, Is.EqualTo(proposed));
        Assert.That(Directory.Exists(proposed), Is.False);
        Assert.That(prepared.AllowedRoots, Is.EqualTo(new[] { proposed, original }));
    }

    [Test]
    [Platform("Linux")]
    public void FolderAndDeletion_AncestorSymlinkOrDanglingFile_NeverTouchOutsideFiles()
    {
        string outside = Directory.CreateDirectory(Path.Combine(directory, "outside")).FullName;
        string alias = Path.Combine(directory, "alias"); Directory.CreateSymbolicLink(alias, outside);
        Assert.That(() => PodcastDownloadStore.PrepareConfiguration(Path.Combine(alias, "nested")), Throws.TypeOf<IOException>());
        Assert.That(Directory.Exists(Path.Combine(outside, "nested")), Is.False);
        PodcastDownloadStore store = new(Path.Combine(directory, "safe"));
        Guid id = Guid.NewGuid(); const string episode = "episode";
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(episode)))[..16].ToLowerInvariant();
        string file = store.PrepareDestination(id, hash + ".mp3").LocalPath;
        File.CreateSymbolicLink(file, Path.Combine(outside, "not-created.mp3"));
        Assert.That(() => store.StageDelete(id, episode, file), Throws.TypeOf<IOException>());
        Assert.That(new FileInfo(file).LinkTarget, Is.Not.Null);
        Assert.That(store.ResolveExisting(file), Is.Null);
    }

    [Test]
    public void StageDelete_ArbitraryFileAndRollbackCollision_KeepAllContents()
    {
        PodcastDownloadStore store = new(Path.Combine(directory, "root"));
        Guid id = Guid.NewGuid(); const string episode = "episode";
        string arbitrary = store.PrepareDestination(id, "precious.mp3").LocalPath;
        File.WriteAllText(arbitrary, "Music");
        Assert.That(() => store.StageDelete(id, episode, arbitrary), Throws.TypeOf<IOException>());
        Assert.That(File.ReadAllText(arbitrary), Is.EqualTo("Music"));
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(episode)))[..16].ToLowerInvariant();
        string owned = store.PrepareDestination(id, hash + ".mp3").LocalPath;
        File.WriteAllText(owned, "download");
        var staged = store.StageDelete(id, episode, owned);
        File.WriteAllText(owned, "replacement");
        Assert.That(() => staged.Dispose(), Throws.TypeOf<IOException>().With.Message.Contains("preserved at"));
        Assert.That(File.ReadAllText(owned), Is.EqualTo("replacement"));
        string recovery = Directory.GetFiles(Path.GetDirectoryName(owned)!, "*.deleting").Single();
        Assert.That(File.ReadAllText(recovery), Is.EqualTo("download"));
    }
}
