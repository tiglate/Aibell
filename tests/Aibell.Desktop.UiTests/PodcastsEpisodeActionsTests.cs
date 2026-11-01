using System.Security.Cryptography;
using System.Text;
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

/// <summary>Stars, per-row progress and download-file deletion against real SQLite and the real strict download store.</summary>
[TestFixture]
public sealed class PodcastsEpisodeActionsTests
{
    private static readonly PodcastPlaybackProgress Saved = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), DateTimeOffset.UnixEpoch);
    private string directory = null!;
    private string downloads = null!;
    private SqliteTrackRepository repository = null!;
    private PodcastDownloadStore store = null!;
    private PodcastService service = null!;
    private PodcastsViewModel model = null!;
    private PodcastsWindow window = null!;
    private Player player = null!;
    private Guid show;

    [SetUp]
    public async Task SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "aibell-podcast-actions-" + Guid.NewGuid().ToString("N"));
        downloads = Path.Combine(directory, "downloads");
        Directory.CreateDirectory(downloads);
        repository = new(Path.Combine(directory, "library.db"));
        store = new(downloads);
        show = Guid.NewGuid();
        await repository.AddSubscriptionAsync(new(show, new Uri("https://example.invalid/feed"), "Show", "", DateTimeOffset.UtcNow),
            [new(show, "kept", "Kept episode", new Uri("https://example.invalid/kept.mp3")),
             new(show, "plain", "Plain episode", new Uri("https://example.invalid/plain.mp3")),
             new(show, "foreign", "Foreign episode", new Uri("https://example.invalid/foreign.mp3"))]);
        await DownloadAsync("kept", Saved);
        await DownloadAsync("plain", null);
        service = new(repository, new RefusingTransport(), new WritingDownloader(), store, positions: repository);
        player = new();
        model = new(service, player);
        window = new(model);
        window.Show();
        await UntilAsync(() => model.Episodes.Count == 3);
    }

    [TearDown]
    public async Task TearDown()
    {
        await window.ShutdownAsync(); window.Close();
        await repository.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }

    [AvaloniaTest]
    public async Task Rating_SetsClearsAndSurvivesFilterAndReopen()
    {
        PodcastEpisodeItem item = Row("kept");
        await model.SetRatingAsync(item, 4);
        Assert.Multiple(() =>
        {
            Assert.That(item.Rating, Is.EqualTo(4));
            Assert.That(item.RatingText, Is.EqualTo("★★★★☆"));
            Assert.That(item.RatingAutomationName, Is.EqualTo("Rating: 4 of 5 stars"));
            Assert.That(item.Episode.SavedProgress, Is.EqualTo(Saved), "Rating never disturbs progress.");
        });
        model.SearchText = "kept"; model.ClearSearch();
        Assert.That(Row("kept").Rating, Is.EqualTo(4), "Filtering keeps the rating.");
        Assert.That((await repository.ListEpisodesAsync(show)).Single(e => e.EpisodeId == "kept").Rating, Is.EqualTo(4));

        await using SqliteTrackRepository reopened = new(Path.Combine(directory, "library.db"));
        Assert.That((await reopened.ListEpisodesAsync(show)).Single(e => e.EpisodeId == "kept").Rating, Is.EqualTo(4), "Persists across restart.");

        await model.SetRatingAsync(item, 0);
        Assert.That(item.RatingAutomationName, Is.EqualTo("Rating: unrated"));
        Assert.That((await repository.ListEpisodesAsync(show)).Single(e => e.EpisodeId == "kept").Rating, Is.Zero);
        await model.SetRatingAsync(item, 6);
        Assert.That(item.Rating, Is.Zero, "Out-of-range values are ignored.");
    }

    [AvaloniaTest]
    public async Task Rating_DigitKeysOnTheSelectedRowSetStars()
    {
        ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == "PodcastEpisodeList");
        PodcastEpisodeItem item = Row("plain");
        model.SelectedEpisode = item;
        list.Focus();
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D3 });
        await UntilAsync(() => item.Rating == 3);
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.NumPad0 });
        await UntilAsync(() => item.Rating == 0);
        Button? rate = null;
        await UntilAsync(() => (rate = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "RateEpisode" && ReferenceEquals(button.DataContext, item))) is not null);
        Assert.That(Avalonia.Automation.AutomationProperties.GetName(rate!), Is.EqualTo("Rating: unrated"), "Screen readers get the value, not the glyphs.");
    }

    [AvaloniaTest]
    public async Task LateAutomaticProgress_UpdatesHiddenRowsButNeverResurrectsAfterDeleteOrRedownload()
    {
        PodcastEpisodeItem plain = Row("plain");
        StoredPodcastEpisode stored = plain.Episode;
        model.SearchText = "kept"; // "plain" is filtered out of view but still tracked
        PodcastPlaybackProgress progress = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(4), DateTimeOffset.UtcNow);
        model.ApplyAutomaticProgress(stored with { SavedProgress = progress });
        Assert.Multiple(() =>
        {
            Assert.That(plain.ProgressPercent, Is.EqualTo(25).Within(0.001));
            Assert.That(plain.CanResume, Is.True);
            Assert.That(plain.PlayText, Is.EqualTo("Resume"));
        });
        PodcastEpisodeItem kept = Row("kept");
        model.ApplyAutomaticProgress(stored with { EpisodeId = "kept", SavedProgress = progress });
        Assert.That(kept.Episode.SavedProgress, Is.EqualTo(Saved), "A notification for a different download identity is ignored.");

        await model.DeleteDownloadAsync(plain);
        Assert.That(plain.State, Is.EqualTo(PodcastEpisodeState.NotDownloaded));
        model.ApplyAutomaticProgress(stored with { SavedProgress = progress });
        Assert.That(plain.HasProgress, Is.False, "A late save after deletion cannot bring the bar back.");
    }

    [AvaloniaTest]
    public async Task DeleteDownload_RemovesOnlyTheFileKeepsEpisodeAndStarsAndAllowsRetry()
    {
        PodcastEpisodeItem item = Row("kept");
        await model.SetRatingAsync(item, 5);
        string path = item.Episode.LocalPath!;
        Assert.That(await model.FileRemovalBlockReasonAsync(item), Is.Null);
        await model.DeleteDownloadAsync(item);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(path), Is.False);
            Assert.That(item.State, Is.EqualTo(PodcastEpisodeState.NotDownloaded));
            Assert.That(item.CanDeleteDownload, Is.False);
            Assert.That(item.Rating, Is.EqualTo(5));
            Assert.That(item.HasProgress, Is.False);
            Assert.That(model.StatusMessage, Does.Contain("Deleted the download"));
        });
        StoredPodcastEpisode row = (await repository.ListEpisodesAsync(show)).Single(e => e.EpisodeId == "kept");
        int subscriptions = (await repository.ListSubscriptionsAsync()).Count;
        Assert.Multiple(() =>
        {
            Assert.That(row.LocalPath, Is.Null);
            Assert.That(row.SavedProgress, Is.Null);
            Assert.That(row.Rating, Is.EqualTo(5), "The star rating survives deleting the file.");
            Assert.That(subscriptions, Is.EqualTo(1));
        });

        await model.DownloadAsync(item);
        Assert.That(item.State, Is.EqualTo(PodcastEpisodeState.Downloaded), model.StatusMessage);
        Assert.That(File.Exists(item.Episode.LocalPath!), Is.True, "Retry downloads again after a delete.");
        Assert.That(item.Episode.SavedProgress, Is.Null);
    }

    [AvaloniaTest]
    public async Task DeleteDownload_WhilePlayingIsRefusedWithAnActionableMessageAndTheFileStays()
    {
        PodcastEpisodeItem item = Row("kept");
        string path = item.Episode.LocalPath!;
        player.Snapshot = new(new Uri(path), TimeSpan.FromSeconds(30), true);
        string? reason = await model.FileRemovalBlockReasonAsync(item);
        Assert.That(reason, Does.Contain("Stop it"));
        await model.DeleteDownloadAsync(item); // a stale confirmation reaches the backend guard
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(path), Is.True);
            Assert.That(item.State, Is.EqualTo(PodcastEpisodeState.Downloaded));
            Assert.That(item.Episode.SavedProgress, Is.EqualTo(Saved));
            Assert.That(model.StatusMessage, Does.Contain("Stop"));
        });
        player.Snapshot = null;
        await model.DeleteDownloadAsync(item);
        Assert.That(File.Exists(path), Is.False, "Stopping unblocks the delete.");
    }

    [AvaloniaTest]
    public async Task DeleteDownload_NeverTouchesAFileOutsideTheOwnedDownloadLayout()
    {
        string outside = Path.Combine(directory, "my-music.mp3");
        await File.WriteAllBytesAsync(outside, [9, 9, 9]);
        await repository.RecordDownloadAsync(show, "foreign", outside, 3, DateTimeOffset.UtcNow);
        await model.LoadAsync();
        await model.WhenEpisodesLoadedAsync();
        PodcastEpisodeItem item = Row("foreign");
        Assert.That(item.CanDeleteDownload, Is.True, "A dangling record can be cleared, but never a stranger's file.");
        await model.DeleteDownloadAsync(item);
        Assert.That(File.ReadAllBytes(outside), Is.EqualTo(new byte[] { 9, 9, 9 }));
        Assert.That(model.StatusMessage, Does.Contain("Could not delete"));
    }

    [AvaloniaTest]
    public void RowActions_AreReachableAtMinimumWidth()
    {
        Dispatcher.UIThread.RunJobs();
        window.Width = 360; Dispatcher.UIThread.RunJobs();
        Button[] names = window.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext == Row("kept") && b.IsVisible).ToArray();
        Assert.That(names.Select(b => b.Name), Is.SupersetOf(new List<string> { "PlayEpisode", "StartOverEpisode", "DeleteDownload", "RateEpisode" }));
        Assert.That(names.All(b => b.Bounds.Right <= window.ClientSize.Width + 1), Is.True, "WrapPanel keeps every row action inside the window.");
    }

    [AvaloniaTest]
    public async Task Play_RowShowsNoProgressButDatabaseHasAutosave_StartsAtTheStoredPosition()
    {
        PodcastService resuming = new(repository, new RefusingTransport(), new WritingDownloader(), store, positions: repository,
            metadata: new FixedDurationReader(TimeSpan.FromMinutes(10)));
        using PodcastsViewModel resumingModel = new(resuming, player);
        window.Close();
        window = new(resumingModel);
        window.Show();
        await UntilAsync(() => resumingModel.Episodes.Count == 3);
        PodcastEpisodeItem stale = resumingModel.Episodes.First(item => item.EpisodeId == "plain");
        // An automatic save the row never heard about, as when a notification is missed.
        PodcastPlaybackProgress autosaved = new(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);
        Assert.That(await repository.SaveEpisodePositionAsync(show, "plain", stale.Episode.LocalPath!, autosaved), Is.True);
        Assert.That(stale.CanResume, Is.False, "The row is stale: it never saw the automatic save.");

        await resumingModel.PlayAsync(stale);

        Assert.That(player.Requests.Single().StartPosition, Is.EqualTo(TimeSpan.FromMinutes(3)));
    }

    [AvaloniaTest]
    public async Task StartOver_WithStoredProgress_StartsAtTheBeginning()
    {
        PodcastService resuming = new(repository, new RefusingTransport(), new WritingDownloader(), store, positions: repository,
            metadata: new FixedDurationReader(TimeSpan.FromMinutes(10)));
        using PodcastsViewModel resumingModel = new(resuming, player);
        window.Close();
        window = new(resumingModel);
        window.Show();
        await UntilAsync(() => resumingModel.Episodes.Count == 3);
        PodcastEpisodeItem kept = resumingModel.Episodes.First(item => item.EpisodeId == "kept");

        await resumingModel.StartOverAsync(kept);

        Assert.That(player.Requests.Single().StartPosition, Is.EqualTo(TimeSpan.Zero));
    }

    [AvaloniaTest]
    public async Task PositionSavedNotification_FromAWorkerThread_UpdatesAFilteredRowAndStopsAfterDispose()
    {
        NotifyingPlayer notifier = new();
        using PodcastsViewModel listening = new(service, notifier);
        window.Close();
        window = new(listening);
        window.Show();
        await UntilAsync(() => listening.Episodes.Count == 3);
        PodcastEpisodeItem plain = listening.Episodes.First(item => item.EpisodeId == "plain");
        StoredPodcastEpisode stored = plain.Episode;
        listening.SearchText = "kept"; // the pane is filtered, yet the row must still follow automatic saves
        PodcastPlaybackProgress progress = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(4), DateTimeOffset.UtcNow);

        await Task.Run(() => notifier.Raise(stored with { SavedProgress = progress }));
        await UntilAsync(() => plain.ProgressPercent > 0);
        Assert.That(plain.ProgressPercent, Is.EqualTo(25).Within(0.001));

        listening.Dispose();
        Assert.That(notifier.HasSubscribers, Is.False);
    }

    [AvaloniaTest]
    public async Task Pause_ShowsOnlyOnThePlayingRowAndPausesThePlayer()
    {
        ControllablePlayer controllable = new();
        using PodcastsViewModel controlled = new(service, controllable);
        window.Close();
        window = new(controlled);
        window.Show();
        await UntilAsync(() => controlled.Episodes.Count == 3);
        PodcastEpisodeItem kept = controlled.Episodes.First(item => item.EpisodeId == "kept");
        PodcastEpisodeItem plain = controlled.Episodes.First(item => item.EpisodeId == "plain");
        Assert.Multiple(() =>
        {
            Assert.That(kept.CanPause, Is.False);
            Assert.That(kept.ShowPlay && kept.ShowStartOver, Is.True);
        });

        controllable.Playing = new Uri(kept.Episode.LocalPath!);
        controllable.Raise();
        Assert.Multiple(() =>
        {
            Assert.That(kept.CanPause, Is.True);
            Assert.That(kept.ShowPlay || kept.ShowStartOver, Is.False, "Pause takes the place of Play while the episode plays.");
            Assert.That(plain.CanPause, Is.False);
        });

        await controlled.PauseAsync(plain);
        Assert.That(controllable.Paused, Is.Empty, "A row that is not playing cannot pause the player.");
        await controlled.PauseAsync(kept);
        Assert.That(controllable.Paused, Is.EqualTo(new[] { controllable.Playing }));

        controllable.Playing = null;
        controllable.Raise();
        Assert.That(kept.CanPause, Is.False);
        Assert.That(kept.ShowPlay, Is.True);
    }

    [AvaloniaTest]
    public async Task Cover_LoadsOncePerRowOnDemandAndFallsBackToThePlaceholder()
    {
        Uri withCover = new("https://example.invalid/kept.jpg");
        Uri broken = new("https://example.invalid/broken.jpg");
        await repository.ApplyRefreshAsync(new(show, new Uri("https://example.invalid/feed"), "Show", "", DateTimeOffset.UtcNow),
            [(await repository.ListEpisodesAsync(show)).Single(item => item.EpisodeId == "kept") with { ImageUrl = withCover },
             (await repository.ListEpisodesAsync(show)).Single(item => item.EpisodeId == "plain") with { ImageUrl = broken },
             (await repository.ListEpisodesAsync(show)).Single(item => item.EpisodeId == "foreign")]);
        FakeCovers covers = new(withCover);
        using PodcastsViewModel covered = new(service, player, covers);
        window.Close();
        window = new(covered);
        window.Show();
        await UntilAsync(() => covered.Episodes.Count == 3);
        PodcastEpisodeItem kept = covered.Episodes.First(item => item.EpisodeId == "kept");
        PodcastEpisodeItem plain = covered.Episodes.First(item => item.EpisodeId == "plain");
        PodcastEpisodeItem none = covered.Episodes.First(item => item.EpisodeId == "foreign");
        Assert.That(covers.Requests, Is.Empty, "Nothing is fetched until a row is shown.");
        Assert.That(kept.ShowCoverPlaceholder && kept.CoverAutomationName == "No cover image available", Is.True);

        await covered.EnsureCoverAsync(kept);
        await covered.EnsureCoverAsync(kept);
        await covered.EnsureCoverAsync(plain);
        await covered.EnsureCoverAsync(none);

        Assert.Multiple(() =>
        {
            Assert.That(kept.HasCover, Is.True);
            Assert.That(kept.CoverAutomationName, Is.EqualTo("Episode cover"));
            Assert.That(plain.ShowCoverPlaceholder, Is.True, "A cover that fails to load keeps the placeholder.");
            Assert.That(none.ShowCoverPlaceholder, Is.True, "An episode without its own cover never borrows another image.");
            Assert.That(covers.Requests, Is.EquivalentTo(new[] { withCover, broken }), "One request per cover URL, none for covers-less rows.");
        });
    }

    [AvaloniaTest]
    public async Task Download_ShowsProgressWhileRunningAndFilterTracksDownloadedRows()
    {
        GatedDownloader gated = new();
        PodcastService downloading = new(repository, new RefusingTransport(), gated, store, positions: repository);
        using PodcastsViewModel local = new(downloading, player);
        window.Close();
        window = new(local);
        window.Show();
        await UntilAsync(() => local.Episodes.Count == 3);
        PodcastEpisodeItem foreign = local.Episodes.First(item => item.EpisodeId == "foreign");

        local.DownloadedOnly = true;
        Assert.That(local.Episodes.Select(item => item.EpisodeId), Is.EquivalentTo(new List<string> { "kept", "plain" }));
        Assert.That(local.EpisodeCountText, Is.EqualTo("2 of 3 episodes"));
        local.DownloadedOnly = false;

        Task work = local.DownloadAsync(foreign);
        await UntilAsync(() => gated.Started);
        gated.Report(new(25, 100)); // Progress<T> posts to the UI thread
        await UntilAsync(() => foreign.DownloadReceived == 25);
        Assert.Multiple(() =>
        {
            Assert.That(foreign.IsDownloading, Is.True);
            Assert.That(foreign.DownloadIndeterminate, Is.False);
            Assert.That(foreign.DownloadPercent, Is.EqualTo(25).Within(0.001));
            Assert.That(foreign.StateText, Does.Contain("25%"));
        });

        local.DownloadedOnly = true;
        Assert.That(local.Episodes, Does.Not.Contain(foreign), "A download in flight is not a downloaded episode yet.");
        gated.Finish();
        await work;
        Assert.That(local.Episodes, Does.Contain(foreign), "It joins the downloaded-only view as soon as it finishes.");

        await local.DeleteDownloadAsync(foreign);
        Assert.That(local.Episodes, Does.Not.Contain(foreign));
        local.ClearFilters();
        Assert.That(local.Episodes, Has.Count.EqualTo(3));
    }

    [AvaloniaTest]
    public async Task Speed_OffersFourChoicesAndAppliesTheSelectionToTheController()
    {
        ControllablePlayer controllable = new();
        using PodcastsViewModel controlled = new(service, controllable);
        Assert.Multiple(() =>
        {
            Assert.That(PodcastsViewModel.SpeedLabels, Is.EqualTo(new List<string> { "1x", "1.25x", "1.5x", "2x" }));
            Assert.That(controlled.CanChooseSpeed, Is.True);
            Assert.That(controlled.SelectedSpeedIndex, Is.Zero);
        });

        controlled.SelectedSpeedIndex = 2;
        await UntilAsync(() => controllable.PodcastPlaybackRate == 1.5);
        Assert.That(controlled.SelectedSpeedIndex, Is.EqualTo(2));
        controlled.SelectedSpeedIndex = 3;
        await UntilAsync(() => controllable.PodcastPlaybackRate == 2.0);
        controlled.SelectedSpeedIndex = 99;
        Assert.That(controllable.PodcastPlaybackRate, Is.EqualTo(2.0), "Out-of-range choices are ignored.");
        Assert.That(new PodcastsViewModel(service, player).CanChooseSpeed, Is.False, "No speed control without a controllable player.");
    }

    [AvaloniaTest]
    public void Notes_ArePlainTextAndToggle()
    {
        PodcastEpisodeItem withNotes = new(Row("plain").Episode with { Description = "<p>Hello &amp; welcome</p><script>x()</script>" }, PodcastEpisodeState.NotDownloaded);
        PodcastEpisodeItem without = new(Row("plain").Episode with { Description = "" }, PodcastEpisodeState.NotDownloaded);
        Assert.Multiple(() =>
        {
            Assert.That(withNotes.HasNotes, Is.True);
            Assert.That(withNotes.NotesText, Is.EqualTo("Hello & welcome"));
            Assert.That(withNotes.NotesExpanded, Is.False);
            Assert.That(withNotes.NotesToggleText, Is.EqualTo("Show notes"));
            Assert.That(without.HasNotes, Is.False);
        });
        withNotes.NotesExpanded = true;
        Assert.That(withNotes.NotesToggleText, Is.EqualTo("Hide notes"));
    }

    [AvaloniaTest]
    public async Task Download_QueueRunsTwoAtATimeInOrderAndQueuedRowsCanLeaveTheLine()
    {
        Guid queueShow = Guid.NewGuid();
        StoredPodcastEpisode[] queueEpisodes = new List<string> { "a", "b", "c", "d" }
            .Select(id => new StoredPodcastEpisode(queueShow, id, "Episode " + id, new Uri($"https://example.invalid/{id}.mp3"))).ToArray();
        await repository.AddSubscriptionAsync(new PodcastSubscription(queueShow, new Uri("https://example.invalid/queue"), "Queue show", "", DateTimeOffset.UtcNow), queueEpisodes);
        QueueDownloader downloader = new();
        PodcastService queueing = new(repository, new RefusingTransport(), downloader, store, positions: repository);
        using PodcastsViewModel local = new(queueing, player);
        window.Close();
        window = new(local);
        window.Show();
        await UntilAsync(() => local.Subscriptions.Count == 2);
        local.SelectedSubscription = local.Subscriptions.First(item => item.Id == queueShow);
        await UntilAsync(() => local.Episodes.Count == 4 && local.Episodes.All(item => item.EpisodeId.Length == 1));
        PodcastEpisodeItem a = local.Episodes.First(item => item.EpisodeId == "a"), b = local.Episodes.First(item => item.EpisodeId == "b"),
            c = local.Episodes.First(item => item.EpisodeId == "c"), d = local.Episodes.First(item => item.EpisodeId == "d");

        Task[] work = [local.DownloadAsync(a), local.DownloadAsync(b), local.DownloadAsync(c), local.DownloadAsync(d)];
        await UntilAsync(() => downloader.Started.Count == 2);
        Assert.Multiple(() =>
        {
            Assert.That(downloader.Started, Is.EquivalentTo(new List<string> { "/a.mp3", "/b.mp3" }), "The first two requests run; their start order is not guaranteed.");
            Assert.That(c.State, Is.EqualTo(PodcastEpisodeState.Queued));
            Assert.That(d.State, Is.EqualTo(PodcastEpisodeState.Queued));
            Assert.That(c.CanCancel && !c.CanDownload, Is.True);
            Assert.That(local.QueuedDownloadCount, Is.EqualTo(2));
            Assert.That(c.StateText, Does.Contain("queue"));
        });

        local.CancelDownload(c);
        Assert.That(c.State, Is.EqualTo(PodcastEpisodeState.Cancelled));
        Assert.That(local.QueuedDownloadCount, Is.EqualTo(1));

        downloader.Finish("/a.mp3");
        await UntilAsync(() => downloader.Started.Count == 3);
        Assert.That(downloader.Started[2], Is.EqualTo("/d.mp3"), "The next waiting episode starts when a slot frees up; the cancelled one is skipped.");
        Assert.That(a.State, Is.EqualTo(PodcastEpisodeState.Downloaded));
        downloader.Finish("/b.mp3"); downloader.Finish("/d.mp3");
        await Task.WhenAll(work);
        // A queued episode's own Task completes when it is queued; its download is started later by the queue.
        await UntilAsync(() => b.State == PodcastEpisodeState.Downloaded && d.State == PodcastEpisodeState.Downloaded);
        Assert.That(new[] { b.State, d.State }, Is.All.EqualTo(PodcastEpisodeState.Downloaded));
        Assert.That(local.QueuedDownloadCount, Is.Zero);
    }

    [AvaloniaTest]
    public async Task RefreshAll_FlagsNewEpisodesDownloadNewQueuesThemAndSeenIsPersistent()
    {
        ScriptedFeed feed = new(new Uri("https://example.invalid/feed"), "kept", "plain", "foreign");
        PodcastService refreshing = new(repository, feed, new WritingDownloader(), store, positions: repository);
        using PodcastsViewModel local = new(refreshing, player);
        window.Close();
        window = new(local);
        window.Show();
        await UntilAsync(() => local.Episodes.Count == 3);
        Assert.That(local.HasNewEpisodes, Is.False);

        feed.Ids = ["kept", "plain", "foreign", "n1", "n2"];
        await local.RefreshAllAsync();
        await UntilAsync(() => local.Episodes.Count == 5);
        PodcastEpisodeItem n1 = local.Episodes.First(item => item.EpisodeId == "n1"), n2 = local.Episodes.First(item => item.EpisodeId == "n2");
        Assert.Multiple(() =>
        {
            Assert.That(n1.IsNew && n2.IsNew, Is.True);
            Assert.That(local.Episodes.Count(item => item.IsNew), Is.EqualTo(2));
            Assert.That(local.HasNewEpisodes && local.CanDownloadNew, Is.True);
            Assert.That(local.SelectedSubscription!.Detail, Does.EndWith("· 2 new"));
            Assert.That(local.StatusMessage, Does.Contain("2 new episodes"));
        });

        await local.DownloadNewAsync();
        await UntilAsync(() => n1.State == PodcastEpisodeState.Downloaded && n2.State == PodcastEpisodeState.Downloaded);
        Assert.Multiple(() =>
        {
            Assert.That(n1.IsNew || n2.IsNew, Is.False, "Queuing a download dismisses the New mark.");
            Assert.That(local.HasNewEpisodes, Is.False);
            Assert.That(local.SelectedSubscription!.Detail, Does.Not.Contain("new"));
        });
        Assert.That(await service.CountNewEpisodesAsync(), Is.Empty, "Seen state is stored, not just shown.");

        feed.Ids = ["kept", "plain", "foreign", "n1", "n2", "n3"];
        await local.RefreshSelectedAsync();
        await UntilAsync(() => local.Episodes.Any(item => item.EpisodeId == "n3"));
        Assert.That(local.Episodes.Single(item => item.IsNew).EpisodeId, Is.EqualTo("n3"));
        await local.MarkAllSeenAsync();
        Assert.That(local.HasNewEpisodes, Is.False);
        Assert.That(await service.CountNewEpisodesAsync(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task Catalog_BackgroundRefreshUpdatesTheVisibleSubscriptions()
    {
        ScriptedFeed feed = new(new Uri("https://example.invalid/feed"), "kept", "plain", "foreign");
        PodcastService refreshing = new(repository, feed, new WritingDownloader(), store, positions: repository);
        CatalogPlayer catalog = new();
        using PodcastsViewModel local = new(refreshing, catalog);
        window.Close();
        window = new(local);
        window.Show();
        await UntilAsync(() => local.Episodes.Count == 3);

        local.SelectedEpisode = local.Episodes.First(item => item.EpisodeId == "plain");
        PodcastEpisodeItem sameRow = local.Episodes.First(item => item.EpisodeId == "kept");

        // A check that finds nothing never reloads the list or moves the selection.
        catalog.Raise(new PodcastRefreshAllResult(1, 1, 0, []));
        await Task.Delay(100); Dispatcher.UIThread.RunJobs();
        Assert.That(local.Episodes.First(item => item.EpisodeId == "kept"), Is.SameAs(sameRow));
        Assert.That(local.SelectedEpisode!.EpisodeId, Is.EqualTo("plain"));

        feed.Ids = ["kept", "plain", "foreign", "fresh"];
        PodcastRefreshAllResult result = await refreshing.RefreshAllAsync(); // what the hourly check does, with no view involved
        catalog.Raise(result);
        await UntilAsync(() => local.Episodes.Count == 4);
        Assert.That(local.SelectedEpisode?.EpisodeId, Is.EqualTo("plain"), "The user's selection survives a background refresh.");
        Assert.That(local.Episodes.Single(item => item.IsNew).EpisodeId, Is.EqualTo("fresh"));
        Assert.That(local.SelectedSubscription!.NewCount, Is.EqualTo(1));
        local.Dispose();
        Assert.That(catalog.HasSubscribers, Is.False);
    }

    [AvaloniaTest]
    public async Task Play_DismissesTheNewMark()
    {
        ScriptedFeed feed = new(new Uri("https://example.invalid/feed"), "kept", "plain", "foreign");
        PodcastService refreshing = new(repository, feed, new WritingDownloader(), store, positions: repository,
            metadata: new FixedDurationReader(TimeSpan.FromMinutes(10)));
        using PodcastsViewModel local = new(refreshing, player);
        window.Close();
        window = new(local);
        window.Show();
        await UntilAsync(() => local.Episodes.Count == 3);
        feed.Ids = ["kept", "plain", "foreign", "fresh"];
        await local.RefreshSelectedAsync();
        await UntilAsync(() => local.Episodes.Count == 4);
        // The file arrives by another route (for example a previous download), so only Play can dismiss the mark.
        await DownloadAsync("fresh", null);
        await local.RefreshSelectedAsync();
        await UntilAsync(() => local.Episodes.Any(item => item.EpisodeId == "fresh" && item.State == PodcastEpisodeState.Downloaded));
        PodcastEpisodeItem fresh = local.Episodes.First(item => item.EpisodeId == "fresh");
        Assert.That(fresh.IsNew, Is.True);

        await local.PlayAsync(fresh);

        Assert.That(fresh.IsNew, Is.False);
        Assert.That(await service.CountNewEpisodesAsync(), Is.Empty);
    }

    private PodcastEpisodeItem Row(string id) => model.Episodes.First(item => item.EpisodeId == id);

    private async Task DownloadAsync(string episodeId, PodcastPlaybackProgress? progress)
    {
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(episodeId)))[..16].ToLowerInvariant() + ".mp3";
        Uri file = store.PrepareDestination(show, name);
        await File.WriteAllBytesAsync(file.LocalPath, [1, 2, 3]);
        await repository.RecordDownloadAsync(show, episodeId, file.LocalPath, 3, DateTimeOffset.UtcNow);
        if (progress is not null) await repository.SaveEpisodePositionAsync(show, episodeId, file.LocalPath, progress);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); }
    }

    private sealed class Player : IPodcastPlaybackHandoff, IPodcastPositionSource
    {
        public PodcastPlaybackSnapshot? Snapshot { get; set; }
        public List<PodcastPlaybackRequest> Requests { get; } = [];
        public Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default) { Requests.Add(request); return Task.CompletedTask; }
        public ValueTask<PodcastPlaybackSnapshot?> GetPodcastPlaybackAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
    }

    private sealed class NotifyingPlayer : IPodcastPlaybackHandoff, Aibell.Desktop.Services.IPodcastProgressNotifications
    {
        public event EventHandler<StoredPodcastEpisode>? PositionSaved;
        public bool HasSubscribers => PositionSaved is not null;
        public void Raise(StoredPodcastEpisode episode) => PositionSaved?.Invoke(this, episode);
        public Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ControllablePlayer : IPodcastPlaybackHandoff, Aibell.Desktop.Services.IPodcastPlaybackControl
    {
        public Uri? Playing { get; set; }
        public List<Uri> Paused { get; } = [];
        public Uri? PodcastPlayingFile => Playing;
        public double PodcastPlaybackRate { get; private set; } = 1.0;
        public Task SetPodcastPlaybackRateAsync(double rate, CancellationToken cancellationToken = default) { PodcastPlaybackRate = rate; return Task.CompletedTask; }
        public event EventHandler? PodcastPlayingChanged;
        public void Raise() => PodcastPlayingChanged?.Invoke(this, EventArgs.Empty);
        public Task PausePodcastAsync(Uri file, CancellationToken cancellationToken = default) { Paused.Add(file); return Task.CompletedTask; }
        public Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCovers(Uri good) : Aibell.Desktop.Services.ICoverImageLoader
    {
        public List<Uri> Requests { get; } = [];
        public Task<Avalonia.Media.Imaging.Bitmap?> LoadAsync(Uri url, CancellationToken cancellationToken = default)
        {
            Requests.Add(url);
            return Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(url == good
                ? new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(2, 2), new Avalonia.Vector(96, 96)) : null);
        }
    }

    private sealed class GatedDownloader : IPodcastEpisodeDownloader
    {
        private readonly TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<PodcastDownloadProgress>? progress;
        public bool Started => progress is not null;
        public void Report(PodcastDownloadProgress value) => progress!.Report(value);
        public void Finish() => finish.SetResult();
        public async Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default)
        {
            progress = request.Progress;
            await finish.Task.WaitAsync(cancellationToken);
            await File.WriteAllBytesAsync(request.Destination.LocalPath, [7, 8, 9], cancellationToken);
            return new(request.Destination, 3);
        }
    }

    private sealed class QueueDownloader : IPodcastEpisodeDownloader
    {
        private readonly Dictionary<string, TaskCompletionSource> gates = [];
        private readonly object sync = new();
        public List<string> Started { get { lock (sync) return [.. startedPaths]; } }
        private readonly List<string> startedPaths = [];
        public void Finish(string path) { lock (sync) gates[path].SetResult(); }
        public async Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sync) { gates[request.Source.AbsolutePath] = gate; startedPaths.Add(request.Source.AbsolutePath); }
            await gate.Task.WaitAsync(cancellationToken);
            await File.WriteAllBytesAsync(request.Destination.LocalPath, [1, 2, 3], cancellationToken);
            return new(request.Destination, 3);
        }
    }

    private sealed class ScriptedFeed(Uri url, params string[] ids) : IPodcastFeedReader
    {
        public string[] Ids { get; set; } = ids;
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default)
        {
            PodcastEpisode[] episodes = Ids.Select((id, index) => new PodcastEpisode(id, "Episode " + id, new Uri($"https://example.invalid/{id}.mp3"),
                Published: DateTimeOffset.UnixEpoch.AddDays(index))).ToArray();
            return Task.FromResult(new PodcastFeedResult(new PodcastFeed(url, "Show", "", episodes), [], EntityTag: null, LastModified: null));
        }
    }

    private sealed class CatalogPlayer : IPodcastPlaybackHandoff, Aibell.Desktop.Services.IPodcastCatalogNotifications
    {
        public event EventHandler<PodcastRefreshAllResult>? PodcastCatalogRefreshed;
        public bool HasSubscribers => PodcastCatalogRefreshed is not null;
        public void Raise(PodcastRefreshAllResult result) => PodcastCatalogRefreshed?.Invoke(this, result);
        public Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedDurationReader(TimeSpan duration) : Aibell.Application.Library.IMediaMetadataReader
    {
        public Task<Aibell.Domain.Media.MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Aibell.Domain.Media.MediaMetadata(null, null, null, duration));
    }

    private sealed class RefusingTransport : IPodcastFeedReader
    {
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("These actions never refresh a feed.");
    }

    private sealed class WritingDownloader : IPodcastEpisodeDownloader
    {
        public async Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default)
        {
            await File.WriteAllBytesAsync(request.Destination.LocalPath, [4, 5, 6], cancellationToken);
            return new(request.Destination, 3);
        }
    }
}
