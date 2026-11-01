using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Podcasts;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class PodcastsSearchTests
{
    private const int Episodes = 600;
    private string directory = null!;
    private SqliteTrackRepository repository = null!;
    private PodcastsViewModel model = null!;
    private PodcastsWindow window = null!;
    private BlockingHandoff handoff = null!;
    private Guid first, second;

    [SetUp]
    public async Task SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "aibell-podcast-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        repository = new(Path.Combine(directory, "library.db"));
        PodcastDownloadStore store = new(Path.Combine(directory, "downloads"));
        first = Guid.NewGuid(); second = Guid.NewGuid();
        List<StoredPodcastEpisode> many = [];
        for (int index = 0; index < Episodes; index++)
            many.Add(new(first, "e" + index, index % 50 == 7 ? $"Interview with Zoë #{index}" : $"Daily briefing {index}", new Uri($"https://example.invalid/{index}.mp3")));
        await repository.AddSubscriptionAsync(new(first, new Uri("https://example.invalid/one"), "Big show", "", DateTimeOffset.UtcNow), many);
        await repository.AddSubscriptionAsync(new(second, new Uri("https://example.invalid/two"), "Other show", "", DateTimeOffset.UtcNow),
            [new(second, "x", "interview in another show", new Uri("https://example.invalid/x.mp3"))]);
        // One downloaded episode so Play can be exercised without any network.
        Uri file = store.PrepareDestination(first, "e1.wav");
        await File.WriteAllBytesAsync(file.LocalPath, [1, 2, 3]);
        await repository.RecordDownloadAsync(first, "e1", file.LocalPath, 3, DateTimeOffset.UtcNow);
        handoff = new();
        model = new(new PodcastService(repository, new ForbiddenTransport(), new ForbiddenTransport(), store), handoff);
        window = new(model);
        window.Show();
        await UntilAsync(() => model.Subscriptions.Count == 2 && model.Episodes.Count > 0);
        // The subscription list is ordered by the repository; select the big show explicitly.
        model.SelectedSubscription = model.Subscriptions.Single(item => item.Id == first);
        await model.WhenEpisodesLoadedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        handoff.Release.TrySetResult();
        await window.ShutdownAsync(); window.Close();
        await repository.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }

    [AvaloniaTest]
    public void Search_IsCaseInsensitiveScopedToSelectedShowAndShowsCounts()
    {
        Assert.That(model.Episodes, Has.Count.EqualTo(Episodes));
        Assert.That(model.EpisodeCountText, Is.EqualTo($"{Episodes} episodes"));
        model.SearchText = "iNTERVIEW WITH zoË";
        int matches = Enumerable.Range(0, Episodes).Count(index => index % 50 == 7);
        Assert.Multiple(() =>
        {
            Assert.That(model.Episodes, Has.Count.EqualTo(matches));
            Assert.That(model.Episodes.All(item => item.Title.StartsWith("Interview with", StringComparison.Ordinal)), Is.True);
            Assert.That(model.Episodes.All(item => item.Episode.SubscriptionId == first), Is.True, "Another show's matching title never leaks in.");
            Assert.That(model.EpisodeCountText, Is.EqualTo($"{matches} of {Episodes} episodes"));
            Assert.That(model.HasNoMatches, Is.False);
        });
        model.SearchText = "  nothing like this ";
        Assert.Multiple(() =>
        {
            Assert.That(model.Episodes, Is.Empty);
            Assert.That(model.HasNoMatches, Is.True);
            Assert.That(model.HasEpisodes, Is.True, "No matches is distinct from an empty cache.");
            Assert.That(model.NoMatchesText, Does.Contain("nothing like this").And.Contain(Episodes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        });
        model.ClearSearch();
        Assert.Multiple(() =>
        {
            Assert.That(model.Episodes, Has.Count.EqualTo(Episodes));
            Assert.That(model.HasSearch, Is.False);
            Assert.That(model.HasNoMatches, Is.False);
        });
    }

    [AvaloniaTest]
    public async Task Search_FollowsSelectedShowAndNeverTouchesTheNetwork()
    {
        model.SearchText = "interview";
        model.SelectedSubscription = model.Subscriptions.Single(item => item.Id == second);
        await model.WhenEpisodesLoadedAsync();
        Assert.That(model.Episodes.Select(item => item.EpisodeId), Is.EqualTo(new List<string> { "x" }));
        Assert.That(model.EpisodeCountText, Is.EqualTo("1 of 1 episodes"));
        // ForbiddenTransport throws on any feed or media request; reaching here proves the filter stayed local.
    }

    [AvaloniaTest]
    public async Task Search_HidingThePlayingEpisode_DoesNotCancelAcceptedPlay()
    {
        PodcastEpisodeItem episode = model.Episodes.Single(item => item.EpisodeId == "e1");
        model.SelectedEpisode = episode;
        Task play = model.PlayAsync(episode);
        await handoff.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        model.SearchText = "Interview";
        Assert.Multiple(() =>
        {
            Assert.That(model.Episodes.Contains(episode), Is.False);
            Assert.That(handoff.Token.IsCancellationRequested, Is.False, "Typing in the search box must not cancel playback that was already accepted.");
            Assert.That(model.IsPositionBusy, Is.True);
        });
        model.ClearSearch();
        handoff.Release.TrySetResult();
        await play;
        Assert.That(model.StatusMessage, Does.Contain("Sent").Or.Contain("Resuming"));
        Assert.That(handoff.Requests, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public void Search_KeepsASelectionThatStillMatchesAndEscapeClears()
    {
        PodcastEpisodeItem keep = model.Episodes.First(item => item.Title.StartsWith("Interview", StringComparison.Ordinal));
        model.SelectedEpisode = keep;
        model.SearchText = "Interview";
        Assert.That(model.SelectedEpisode, Is.SameAs(keep));
        TextBox box = window.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "PodcastEpisodeSearch");
        Assert.That(box.Text, Is.EqualTo("Interview"));
        box.Focus();
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.That(model.SearchText, Is.Empty);
        Assert.That(model.Episodes, Has.Count.EqualTo(Episodes));
    }

    [AvaloniaTest]
    public void EpisodeList_VirtualizesHundredsOfRows()
    {
        Dispatcher.UIThread.RunJobs();
        ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single(item => item.Name == "PodcastEpisodeList");
        int realized = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
        Assert.That(realized, Is.GreaterThan(0).And.LessThan(Episodes / 4), "Only the visible rows are realized.");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); }
    }

    private sealed class BlockingHandoff : IPodcastPlaybackHandoff
    {
        public CancellationToken Token { get; private set; }
        public List<PodcastPlaybackRequest> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; Requests.Add(request); Started.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class ForbiddenTransport : IPodcastFeedReader, IPodcastEpisodeDownloader
    {
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Searching cached episodes must stay offline.");
        public Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Searching cached episodes must stay offline.");
    }
}
