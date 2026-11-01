using Aibell.Application.Library;
using Aibell.Application.Playback;
using Aibell.Application.Settings;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Domain.Playlists;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Settings;
using Aibell.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.Playback;

[TestFixture]
public sealed class PlaybackIndicatorIntegrationTests
{
    private string temporaryDirectory = null!;
    private SqliteTrackRepository repository = null!;
    private JsonApplicationSettingsStore settings = null!;
    private LibraryMonitoringService monitoring = null!;
    private SnapshotPlaybackEngine engine = null!;
    private MainWindowViewModel viewModel = null!;
    private Track first = null!;
    private Track second = null!;
    private LibraryImportService importer = null!;

    [SetUp]
    public async Task SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aibell-indicator-{Guid.NewGuid():N}");
        repository = new SqliteTrackRepository(Path.Combine(temporaryDirectory, "library.db"));
        settings = new JsonApplicationSettingsStore(Path.Combine(temporaryDirectory, "settings.json"));
        first = new Track(Guid.NewGuid(), new Uri(Path.Combine(temporaryDirectory, "first.flac")),
            "First", "Artist", "Album", TimeSpan.FromMinutes(3));
        second = first with
        {
            Id = Guid.NewGuid(),
            Location = new Uri(Path.Combine(temporaryDirectory, "second.flac")),
            Title = "Second"
        };
        await repository.UpsertAsync(first, temporaryDirectory);
        await repository.UpsertAsync(second, temporaryDirectory);
        importer = new(new FileSystemMediaDiscovery(),
            new AtlMediaMetadataReader(), repository);
        monitoring = new LibraryMonitoringService(repository, importer,
            NullLogger<LibraryMonitoringService>.Instance);
        engine = new SnapshotPlaybackEngine();
        viewModel = new MainWindowViewModel(importer, repository, repository, repository,
            repository, settings, monitoring, engine);
        await viewModel.LoadAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await viewModel.FlushPreferencesAsync();
        viewModel.Dispose();
        monitoring.Dispose();
        settings.Dispose();
        await repository.DisposeAsync();
        Directory.Delete(temporaryDirectory, recursive: true);
    }

    [Test]
    public async Task TogglePlaybackAsync_SelectionPauseTrackChangeAndStop_FollowsEngineTrack()
    {
        viewModel.SelectedTrack = viewModel.Tracks.Single(row => row.Source.Id == first.Id);
        await viewModel.TogglePlaybackAsync();
        viewModel.StartListening();

        viewModel.SelectedTrack = viewModel.Tracks.Single(row => row.Source.Id == second.Id);
        Assert.That(viewModel.Tracks.Single(row => row.IsPlaying).Source.Id, Is.EqualTo(first.Id));

        viewModel.SelectedTrack = null;
        await viewModel.TogglePlaybackAsync();
        viewModel.StartListening();
        Assert.That(viewModel.Tracks.Single(row => row.IsPaused).Source.Id, Is.EqualTo(first.Id));

        viewModel.SelectedTrack = viewModel.Tracks.Single(row => row.Source.Id == second.Id);
        await viewModel.TogglePlaybackAsync();
        viewModel.StartListening();
        Assert.That(viewModel.Tracks.Single(row => row.HasPlaybackIndicator).Source.Id,
            Is.EqualTo(second.Id));

        await viewModel.StopPlaybackAsync();
        viewModel.StartListening();
        Assert.That(viewModel.Tracks.Any(row => row.HasPlaybackIndicator), Is.False);
    }

    [Test]
    public async Task RefreshLibraryAndPlaylist_ActiveTrack_KeepsIndicatorAcrossRowRebuilds()
    {
        engine.Snapshot = new PlaybackSnapshot(PlaybackStatus.Paused, first, TimeSpan.Zero, 0.8);
        viewModel.StartListening();
        viewModel.SortTracks(TrackSortField.Title);
        Assert.That(viewModel.Tracks.Single(row => row.IsPaused).Source.Id, Is.EqualTo(first.Id));

        await viewModel.SearchAsync("Second");
        Assert.That(viewModel.Tracks.Any(row => row.HasPlaybackIndicator), Is.False);
        await viewModel.SearchAsync(string.Empty);
        Assert.That(viewModel.Tracks.Single(row => row.IsPaused).Source.Id, Is.EqualTo(first.Id));

        viewModel.SelectedTrack = viewModel.Tracks.Single(row => row.Source.Id == first.Id);
        await viewModel.SetSelectedTrackRatingAsync(4);
        Assert.That(viewModel.Tracks.Single(row => row.IsPaused).Source.Id, Is.EqualTo(first.Id));

        await viewModel.CreatePlaylistWithTrackAsync("Playing", viewModel.SelectedTrack!);
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.Tracks.Single().IsPaused, Is.True);
            Assert.That(viewModel.Tracks.Single().CanReorder, Is.True);
        });
    }

    [Test]
    public async Task TogglePlaybackAsync_PlayStatisticsReplacement_PreservesIndicator()
    {
        viewModel.SelectedTrack = viewModel.Tracks.Single(row => row.Source.Id == first.Id);
        TrackRowViewModel original = viewModel.SelectedTrack;

        await viewModel.TogglePlaybackAsync();

        TrackRowViewModel updated = viewModel.Tracks.Single(row => row.Source.Id == first.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updated, Is.Not.SameAs(original));
            Assert.That(updated.Source.PlayCount, Is.EqualTo(1));
            Assert.That(updated.IsPlaying, Is.True);
        });
    }

    [Test]
    public async Task TogglePlaybackAsync_UnloadedEngine_StartsQueueBeforeLibrarySelection()
    {
        viewModel.SelectedTrack = viewModel.Tracks.Single(row => row.Source.Id == first.Id);
        viewModel.QueueTracks([second, first]);

        await viewModel.TogglePlaybackAsync();
        viewModel.StartListening();

        Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(second.Id));
        Assert.That(viewModel.QueueItems.Single(entry => entry.Row.IsPlaying).Row.Source.Id, Is.EqualTo(second.Id));
    }

    [Test]
    public async Task QueueTracks_PrioritizesRequestsAndResumesLibraryContext()
    {
        viewModel.SelectedTrack = viewModel.Tracks.Single(row => row.Source.Id == first.Id);
        await viewModel.TogglePlaybackAsync();
        viewModel.QueueTracks([second, first]);

        await viewModel.MoveNextAsync();
        viewModel.StartListening();
        Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(second.Id));
        Assert.That(viewModel.QueueItems.Single(entry => entry.Row.IsPlaying).Position, Is.EqualTo(1));

        await viewModel.SearchAsync("First");
        await viewModel.SearchAsync(string.Empty);
        await viewModel.MoveNextAsync();
        Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(first.Id));
        await viewModel.MoveNextAsync();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.QueueItems, Is.Empty);
            Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(second.Id));
        });
    }

    [Test]
    public async Task RemoveAndClearQueue_LeavePlayingTrackAndLibraryUntouched()
    {
        viewModel.QueueTracks([first, first, second]);
        Guid activeId = viewModel.QueueItems[0].Id;
        await viewModel.PlayQueueEntryAsync(activeId);
        viewModel.RemoveQueueEntry(viewModel.QueueItems[1].Id);
        Assert.That(viewModel.QueueItems.Count, Is.EqualTo(2));
        viewModel.ClearPlayQueue();

        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
            Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(first.Id));
            Assert.That(viewModel.QueueItems, Is.Empty);
        });
        Assert.That(await repository.ListAsync(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task QueueFilesAsync_StagesExternalAudioAndAddsItToPlaylistOnRequest()
    {
        string path = Path.Combine(temporaryDirectory, "external.wav");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.wav"), path);
        await viewModel.QueueFilesAsync([new Uri(path)]);

        Assert.That(viewModel.QueueItems, Has.Count.EqualTo(1));
        Assert.That(await repository.ListAsync(), Has.Count.EqualTo(2));
        PlayQueueEntryViewModel entry = viewModel.QueueItems.Single();
        await viewModel.CreatePlaylistWithQueueEntryAsync("External", entry);

        Assert.That(await repository.ListAsync(), Has.Count.EqualTo(3));
        Assert.That(viewModel.Tracks.Single().Source.Location, Is.EqualTo(new Uri(path)));
        Assert.That(File.Exists(path), Is.True);
    }

    [Test]
    public async Task QueueFilesAsync_MissingAudio_ReportsFailureWithoutQueueOrLibraryChanges()
    {
        await viewModel.QueueFilesAsync([new Uri(Path.Combine(temporaryDirectory, "missing.wav"))]);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.QueueItems, Is.Empty);
            Assert.That(viewModel.StatusMessage, Does.Contain("could not be read"));
        });
        Assert.That(await repository.ListAsync(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task AddQueueEntryToLibraryAsync_ImportsStagedFileOnceAndKeepsOtherTracks()
    {
        string path = Path.Combine(temporaryDirectory, "external.wav");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.wav"), path);
        await viewModel.QueueFilesAsync([new Uri(path)]);
        PlayQueueEntryViewModel entry = viewModel.QueueItems.Single();

        await viewModel.AddQueueEntryToLibraryAsync(entry);
        await viewModel.AddQueueEntryToLibraryAsync(entry);

        IReadOnlyList<Track> library = await repository.ListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(library, Has.Count.EqualTo(3));
            Assert.That(library.Count(track => track.Location == new Uri(path)), Is.EqualTo(1));
            Assert.That(library.Any(track => track.Id == first.Id), Is.True);
            Assert.That(library.Any(track => track.Id == second.Id), Is.True);
            Assert.That(File.Exists(path), Is.True);
        });
    }

    [Test]
    public async Task PlayQueueEntryAsync_DuplicateRequest_MarksOnlyTheChosenEntry()
    {
        viewModel.QueueTracks([first, second, first]);
        Guid chosen = viewModel.QueueItems[2].Id;
        await viewModel.PlayQueueEntryAsync(chosen);
        viewModel.StartListening();

        Assert.That(viewModel.QueueItems.Single(entry => entry.Row.IsPlaying).Id, Is.EqualTo(chosen));
        Assert.That(viewModel.QueueItems[0].Id, Is.EqualTo(chosen));
        viewModel.QueueTracks([second], next: true);
        await viewModel.MoveNextAsync();

        Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(second.Id));
    }

    [Test]
    public async Task ResizeAndFoldQueue_PersistsLayoutAcrossViewModels()
    {
        await viewModel.ResizePlayQueueAsync(350);
        await viewModel.TogglePlayQueueAsync();
        MainWindowViewModel restored = new(importer, repository, repository, repository,
            repository, settings, monitoring, engine);
        await restored.LoadAsync();

        Assert.Multiple(() =>
        {
            Assert.That(restored.IsPlayQueueVisible, Is.False);
            Assert.That(restored.PlayQueueWidth, Is.EqualTo(350));
        });
    }

    [Test]
    public async Task QueueChanges_Restart_RetainsOrderDuplicatesAndCurrentAsPending()
    {
        viewModel.QueueTracks([first, second, first]);
        Guid current = viewModel.QueueItems[2].Id;
        await viewModel.PlayQueueEntryAsync(current);
        viewModel.QueueTracks([second], next: true);
        viewModel.MoveQueueEntry(viewModel.QueueItems[3].Id, viewModel.QueueItems[2].Id, false);
        Guid[] expectedIds = viewModel.QueueItems.Select(entry => entry.Id).ToArray();
        Guid[] expectedTracks = viewModel.QueueItems.Select(entry => entry.Row.Source.Id).ToArray();
        await viewModel.FlushPreferencesAsync();

        SnapshotPlaybackEngine stoppedEngine = new();
        Assert.That((await settings.LoadAsync()).PlayQueue, Has.Count.EqualTo(4), viewModel.StatusMessage);
        using JsonApplicationSettingsStore reopened = new(Path.Combine(temporaryDirectory, "settings.json"));
        MainWindowViewModel restored = new(importer, repository, repository, repository,
            repository, reopened, monitoring, stoppedEngine);
        await restored.LoadAsync();

        Assert.That(restored.StatusMessage, Does.Not.StartWith("Operation failed"));
        Assert.Multiple(() =>
        {
            Assert.That(restored.QueueItems.Select(entry => entry.Id), Is.EqualTo(expectedIds));
            Assert.That(restored.QueueItems.Select(entry => entry.Row.Source.Id), Is.EqualTo(expectedTracks));
            Assert.That(restored.QueueItems.All(entry => entry.CanReorder), Is.True);
            Assert.That(restored.QueueItems.Any(entry => entry.Row.HasPlaybackIndicator), Is.False);
            Assert.That(stoppedEngine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
            Assert.That(stoppedEngine.Snapshot.Track, Is.Null);
            Assert.That(restored.QueueItems[0].Row.Source.PlayCount, Is.EqualTo(1));
        });
        await restored.TogglePlaybackAsync();
        Assert.That(stoppedEngine.Snapshot.Track!.Id, Is.EqualTo(first.Id));
        await restored.FlushPreferencesAsync();
    }

    [Test]
    public async Task QueueConsumptionRemovalAndClear_Restart_DoesNotResurrectRequests()
    {
        viewModel.QueueTracks([first, second, first, second]);
        await viewModel.PlayQueueEntryAsync(viewModel.QueueItems[0].Id);
        await viewModel.MoveNextAsync();
        viewModel.RemoveQueueEntry(viewModel.QueueItems[2].Id);
        Guid[] expected = viewModel.QueueItems.Select(entry => entry.Id).ToArray();
        await viewModel.FlushPreferencesAsync();
        Assert.That((await settings.LoadAsync()).PlayQueue!.Select(entry => entry.Id), Is.EqualTo(expected));

        viewModel.ClearPlayQueue();
        await viewModel.FlushPreferencesAsync();
        MainWindowViewModel restored = new(importer, repository, repository, repository,
            repository, settings, monitoring, new SnapshotPlaybackEngine());
        await restored.LoadAsync();
        Assert.That(restored.QueueItems, Is.Empty);
    }

    [Test]
    public async Task QueueFilesAsync_Restart_PreservesExternalTrackWithoutImportingIt()
    {
        string path = Path.Combine(temporaryDirectory, "external.wav");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.wav"), path);
        await viewModel.QueueFilesAsync([new Uri(path), new Uri(path)]);
        Guid[] expected = viewModel.QueueItems.Select(entry => entry.Id).ToArray();
        await viewModel.FlushPreferencesAsync();

        MainWindowViewModel restored = new(importer, repository, repository, repository,
            repository, settings, monitoring, new SnapshotPlaybackEngine());
        await restored.LoadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(restored.QueueItems.Select(entry => entry.Id), Is.EqualTo(expected));
            Assert.That(restored.QueueItems.Select(entry => entry.Row.Source.Location),
                Is.EqualTo(new[] { new Uri(path), new Uri(path) }));
            Assert.That(File.Exists(path), Is.True);
        });
        Assert.That(await repository.ListAsync(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task QueueChanges_BlockedSaveAndCancelledControl_DrainsLatestSnapshotInOrder()
    {
        BlockingSettingsStore blockedStore = new();
        MainWindowViewModel queued = new(importer, repository, repository, repository,
            repository, blockedStore, monitoring,
            new SnapshotPlaybackEngine());
        await queued.LoadAsync();
        queued.QueueTracks([first, second, first]);
        queued.RemoveQueueEntry(queued.QueueItems[1].Id);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Task cancelledControl = queued.TogglePlayQueueAsync(cancelled.Token);
        queued.ClearPlayQueue();
        Task drain = queued.FlushPreferencesAsync();
        try
        {
            Assert.That(blockedStore.SaveAttempts, Is.EqualTo(1));
            Assert.That(drain.IsCompleted, Is.False);
        }
        finally
        {
            blockedStore.Release.TrySetResult();
            try
            {
                await cancelledControl;
                Assert.Fail("The cancelled control save should propagate cancellation.");
            }
            catch (OperationCanceledException)
            {
            }
            await drain;
        }

        int[] expectedCounts = [3, 2, 0];
        Assert.Multiple(() =>
        {
            Assert.That(blockedStore.Saved.Select(preferences => preferences.PlayQueue!.Count),
                Is.EqualTo(expectedCounts));
            Assert.That(blockedStore.Saved[^1].IsPlayQueueVisible, Is.False);
        });
    }

    private sealed class BlockingSettingsStore : IApplicationSettingsStore
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ApplicationPreferences> Saved { get; } = [];
        public int SaveAttempts { get; private set; }

        public Task<ApplicationPreferences> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ApplicationPreferences.Default);

        public async Task SaveAsync(ApplicationPreferences preferences, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            Saved.Add(preferences);
        }
    }

    [Test]
    public async Task CreateSmartPlaylistAsync_RatingChanges_RefreshesMembershipAndKeepsQueue()
    {
        await repository.SetRatingAsync(first.Id, 4);
        await viewModel.LoadAsync();
        viewModel.QueueTracks([second]);
        await viewModel.CreateSmartPlaylistAsync("Favorites", new(SmartPlaylistMatch.All,
            [new(SmartPlaylistField.Rating, SmartPlaylistOperator.GreaterOrEqual, "4")]));
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.SelectedPlaylist!.IsSmart, Is.True);
            Assert.That(viewModel.Tracks.Single().Source.Id, Is.EqualTo(first.Id));
            Assert.That(viewModel.Tracks.Single().CanReorder, Is.False);
            Assert.That(viewModel.Tracks.Single().IsPlaylistTrack, Is.False);
            Assert.That(viewModel.CanReorderPlaylist, Is.False);
            Assert.That(viewModel.CanEditPlaylist, Is.True);
            Assert.That(viewModel.PlaylistTarget, Is.Null);
        });
        viewModel.SelectedTrack = viewModel.Tracks.Single();
        Assert.That(viewModel.CanRemoveFromPlaylist, Is.False);
        await viewModel.SetSelectedTrackRatingAsync(3);

        Assert.That(viewModel.Tracks, Is.Empty);
        Assert.That(viewModel.SelectedPlaylist!.TrackCount, Is.Zero);
        Assert.That(viewModel.QueueItems.Single().Row.Source.Id, Is.EqualTo(second.Id));
    }

    [Test]
    public async Task PlaySmartPlaylistTrack_StatisticsChange_RetainsPlayingTrackAndAutomaticContext()
    {
        await viewModel.CreateSmartPlaylistAsync("Unplayed", new(SmartPlaylistMatch.All,
            [new(SmartPlaylistField.PlayCount, SmartPlaylistOperator.Equals, "0")]));
        viewModel.SelectedTrack = viewModel.Tracks.Single(row => row.Source.Id == first.Id);
        await viewModel.TogglePlaybackAsync();

        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(first.Id));
            Assert.That(viewModel.Tracks.Single().Source.Id, Is.EqualTo(second.Id));
            Assert.That(viewModel.CanPlay, Is.True);
            Assert.That(viewModel.CanGoNext, Is.True);
        });
        await viewModel.MoveNextAsync();
        Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(second.Id));
        Assert.That(viewModel.Tracks, Is.Empty);
    }

    [Test]
    public async Task UpdateSmartPlaylistAsync_SearchAndRestart_PreservesDefinitionAndManualTargets()
    {
        viewModel.NewPlaylistName = "Manual";
        await viewModel.CreatePlaylistAsync();
        await viewModel.CreateSmartPlaylistAsync("Filtered", new(SmartPlaylistMatch.Any,
            [new(SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "First")]));
        Assert.That(viewModel.PlaylistTarget!.Name, Is.EqualTo("Manual"));
        await viewModel.UpdateSelectedSmartPlaylistAsync(new(SmartPlaylistMatch.All,
            [new(SmartPlaylistField.Title, SmartPlaylistOperator.Equals, "Second")]));
        await viewModel.SearchAsync("First");
        Assert.That(viewModel.Tracks, Is.Empty);
        await viewModel.SearchAsync("");
        Assert.That(viewModel.Tracks.Single().Source.Id, Is.EqualTo(second.Id));
        using MainWindowViewModel restored = new(importer, repository, repository, repository,
            repository, settings, monitoring, new SnapshotPlaybackEngine());
        await restored.LoadAsync();
        await restored.SelectPlaylistAsync(restored.EditablePlaylists.Single(playlist => playlist.IsSmart));

        Assert.That(restored.Tracks.Single().Source.Id, Is.EqualTo(second.Id));
        Assert.That(restored.SelectedPlaylist!.Definition!.Rules.Single().Value, Is.EqualTo("Second"));
    }

    // Tests sample this engine through StartListening; native UI event dispatch is
    // exercised separately by the rendered desktop smoke check.
    private sealed class SnapshotPlaybackEngine : IPlaybackEngine
    {
        public PlaybackSnapshot Snapshot { get; set; } =
            new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8);

        public event EventHandler<PlaybackSnapshot>? StateChanged { add { } remove { } }

        public Task LoadAsync(Track track, CancellationToken cancellationToken = default)
        {
            Snapshot = Snapshot with { Track = track, Status = PlaybackStatus.Stopped };
            return Task.CompletedTask;
        }

        public Task PlayAsync(CancellationToken cancellationToken = default) => SetStatus(PlaybackStatus.Playing);
        public Task PauseAsync(CancellationToken cancellationToken = default) => SetStatus(PlaybackStatus.Paused);
        public Task StopAsync(CancellationToken cancellationToken = default) => SetStatus(PlaybackStatus.Stopped);
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private Task SetStatus(PlaybackStatus status)
        {
            Snapshot = Snapshot with { Status = status };
            return Task.CompletedTask;
        }
    }
}
