using Aibell.Application.Library;
using Aibell.Application.Playback;
using Aibell.Application.Settings;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.Podcasts;

[TestFixture]
public sealed class PodcastFolderPreferencesTests
{
    private string directory = null!;
    private string oldRoot = null!;
    private string newRoot = null!;
    private SqliteTrackRepository repository = null!;
    private LibraryMonitoringService monitoring = null!;
    private GatedSettingsStore store = null!;
    private PodcastDownloadStore downloads = null!;
    private MainWindowViewModel viewModel = null!;

    [SetUp]
    public async Task SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-podcast-folder-{Guid.NewGuid():N}");
        oldRoot = Path.Combine(directory, "old");
        newRoot = Path.Combine(directory, "new");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot);
        repository = new SqliteTrackRepository(Path.Combine(directory, "library.db"));
        LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), repository);
        monitoring = new LibraryMonitoringService(repository, importer, NullLogger<LibraryMonitoringService>.Instance);
        store = new GatedSettingsStore();
        downloads = new PodcastDownloadStore(oldRoot);
        viewModel = new MainWindowViewModel(importer, repository, repository, repository, repository, store,
            monitoring, new StoppedEngine());
        viewModel.ConfigurePodcastDownloads(downloads, oldRoot);
        await viewModel.LoadAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        store.ReleaseAll();
        await viewModel.FlushPreferencesAsync();
        viewModel.Dispose();
        monitoring.Dispose();
        await repository.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task SavePreferencesAsync_FolderWriteFails_KeepsOldFolderEverywhereAndSurfacesTheFailure()
    {
        Task<Exception?> save = Capture(viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { PodcastFolder = newRoot }));
        await store.WaitForSaveAsync(1);
        // An unrelated write accepted while the folder write is still pending.
        viewModel.QueueTracks([TestTrack()]);

        store.Complete(1, new IOException("disk full"));
        Exception? failure = await save;
        await store.WaitForSaveAsync(2);
        store.Complete(2);
        await viewModel.FlushPreferencesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.TypeOf<IOException>());
            Assert.That(downloads.Root, Is.EqualTo(Path.GetFullPath(oldRoot)));
            Assert.That(store.Persisted, Has.Count.EqualTo(1));
            Assert.That(store.Persisted[0].PodcastDownloadFolder, Is.Null, "the rejected folder must never reach the settings file");
            Assert.That(store.Persisted[0].PodcastDownloadFolderHistory, Is.Empty);
            Assert.That(store.Persisted[0].PlayQueue, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task SavePreferencesAsync_FolderWriteSucceeds_QueuedUnrelatedWriteKeepsTheNewFolder()
    {
        Task<Exception?> save = Capture(viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { PodcastFolder = newRoot }));
        await store.WaitForSaveAsync(1);
        // Its snapshot is taken before the folder commits, but it only writes afterwards.
        viewModel.QueueTracks([TestTrack()]);

        store.Complete(1);
        Exception? failure = await save;
        await store.WaitForSaveAsync(2);
        store.Complete(2);
        await viewModel.FlushPreferencesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null);
            Assert.That(downloads.Root, Is.EqualTo(Path.GetFullPath(newRoot)));
            Assert.That(store.Persisted, Has.Count.EqualTo(2));
            Assert.That(store.Persisted.Select(item => item.PodcastDownloadFolder),
                Is.All.EqualTo(Path.GetFullPath(newRoot)));
            Assert.That(store.Persisted[1].PodcastDownloadFolderHistory, Does.Contain(Path.GetFullPath(oldRoot)));
            Assert.That(store.Persisted[1].PlayQueue, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task SavePreferencesAsync_AfterAFailedFolderWrite_LaterSavesStillRun()
    {
        Task<Exception?> failed = Capture(viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { PodcastFolder = newRoot }));
        await store.WaitForSaveAsync(1);
        store.Complete(1, new IOException("denied"));
        await failed;

        Task retry = viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { PodcastFolder = newRoot });
        await store.WaitForSaveAsync(2);
        store.Complete(2);
        await retry;

        Assert.That(downloads.Root, Is.EqualTo(Path.GetFullPath(newRoot)));
        Assert.That(store.Persisted.Single().PodcastDownloadFolder, Is.EqualTo(Path.GetFullPath(newRoot)));
    }

    [Test]
    public async Task SavePreferencesAsync_AutoRefreshChoice_IsPersistedAndKeptByLaterWrites()
    {
        Assert.That(viewModel.GetPreferences().PodcastAutoRefresh, Is.True, "On by default.");
        Task save = viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { PodcastAutoRefresh = false });
        await store.WaitForSaveAsync(1);
        store.Complete(1);
        await save;
        viewModel.QueueTracks([TestTrack()]);
        await store.WaitForSaveAsync(2);
        store.Complete(2);
        await viewModel.FlushPreferencesAsync();

        Assert.That(store.Persisted.Select(item => item.PodcastAutoRefresh), Is.All.False);
        Assert.That(viewModel.GetPreferences().PodcastAutoRefresh, Is.False);
    }

    [Test]
    public async Task SavePreferencesAsync_OnlineLookupChoice_IsOffByDefaultAndPersisted()
    {
        Assert.That(viewModel.GetPreferences().OnlineLookup, Is.False, "Nothing goes online unless the user allows it.");
        Assert.That(viewModel.OnlineLookupAllowed, Is.False);
        Task save = viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { OnlineLookup = true });
        await store.WaitForSaveAsync(1);
        store.Complete(1);
        await save;

        Assert.That(store.Persisted.Single().OnlineLookup, Is.True);
        Assert.That(viewModel.OnlineLookupAllowed, Is.True);
    }

    [Test]
    public async Task SavePreferencesAsync_PlaybackEngineChoice_DefaultsToThePreferredPlayerAndIsPersisted()
    {
        // With nothing chosen, the platform's own choice is used: PipeWire on Linux.
        Assert.That(viewModel.GetPreferences().PlaybackEngine, Is.EqualTo(PlaybackEngineDefaults.Preferred));
        Task save = viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { PlaybackEngine = PlaybackEngineKind.Vlc });
        await store.WaitForSaveAsync(1);
        store.Complete(1);
        await save;

        Assert.That(store.Persisted.Single().PlaybackEngine, Is.EqualTo(PlaybackEngineKind.Vlc));
        Assert.That(viewModel.GetPreferences().PlaybackEngine, Is.EqualTo(PlaybackEngineKind.Vlc));
        Assert.That(viewModel.StatusMessage, Does.Contain("next time Aibell starts"), "the new engine applies on the next start");
    }

    [Test]
    public async Task SavePreferencesAsync_Language_IsPersistedAndAppliesOnNextStart()
    {
        Assert.That(viewModel.GetPreferences().Language, Is.Null, "follows the operating system by default");
        Task save = viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { Language = "it" });
        await store.WaitForSaveAsync(1);
        store.Complete(1);
        await save;

        Assert.That(store.Persisted.Single().Language, Is.EqualTo("it"));
        Assert.That(viewModel.StatusMessage, Does.Contain("language changes the next time Aibell starts"));
    }

    [Test]
    public async Task SavePreferencesAsync_UnshippedLanguage_IsRejected()
    {
        Exception? error = await Capture(viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { Language = "xx" }));

        Assert.That(error, Is.InstanceOf<ArgumentException>());
    }

    [Test]
    public async Task SavePreferencesAsync_UndefinedPlaybackEngine_IsRejected()
    {
        Exception? error = await Capture(viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { PlaybackEngine = (PlaybackEngineKind)99 }));

        Assert.That(error, Is.InstanceOf<ArgumentException>());
    }

    private static Track TestTrack() => new(Guid.NewGuid(), new Uri("file:///music/queued.flac"), "Queued", "Artist", "Album", TimeSpan.FromMinutes(3));

    private static async Task<Exception?> Capture(Task task)
    {
        try { await task; return null; }
        catch (Exception exception) { return exception; }
    }

    private sealed class GatedSettingsStore : IApplicationSettingsStore
    {
        private readonly object gate = new();
        private readonly List<(ApplicationPreferences Preferences, TaskCompletionSource<Exception?> Result)> saves = [];
        public List<ApplicationPreferences> Persisted { get; } = [];

        public Task<ApplicationPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ApplicationPreferences.Default);

        public async Task SaveAsync(ApplicationPreferences preferences, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<Exception?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) saves.Add((preferences, result));
            Exception? error = await result.Task;
            if (error is not null) throw error;
            lock (gate) Persisted.Add(preferences);
        }

        public async Task WaitForSaveAsync(int count)
        {
            for (int attempt = 0; attempt < 500; attempt++)
            {
                lock (gate) if (saves.Count >= count) return;
                await Task.Delay(10);
            }
            Assert.Fail($"Save {count} never reached the settings store.");
        }

        public void Complete(int number, Exception? error = null)
        {
            lock (gate) saves[number - 1].Result.TrySetResult(error);
        }

        public void ReleaseAll()
        {
            lock (gate) foreach (var save in saves) save.Result.TrySetResult(null);
        }
    }

    private sealed class StoppedEngine : IPlaybackEngine
    {
        public PlaybackSnapshot Snapshot { get; } = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8);
        public event EventHandler<PlaybackSnapshot>? StateChanged { add { } remove { } }
        public Task LoadAsync(Track track, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
