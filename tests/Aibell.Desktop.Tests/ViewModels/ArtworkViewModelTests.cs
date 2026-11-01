using Aibell.Application.Library;
using Aibell.Application.Playback;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Settings;
using Aibell.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.ViewModels;

[TestFixture]
public sealed class ArtworkViewModelTests
{
    private string directory = null!;
    private SqliteTrackRepository repository = null!;
    private JsonApplicationSettingsStore settings = null!;
    private LibraryMonitoringService monitoring = null!;
    private MainWindowViewModel viewModel = null!;
    private TestEngine engine = null!;
    private DeferredArtworkProvider provider = null!;
    private Track first = null!;
    private Track second = null!;

    [SetUp]
    public async Task SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-artwork-vm-{Guid.NewGuid():N}");
        repository = new SqliteTrackRepository(Path.Combine(directory, "library.db"));
        settings = new JsonApplicationSettingsStore(Path.Combine(directory, "settings.json"));
        LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), repository);
        monitoring = new LibraryMonitoringService(repository, importer, NullLogger<LibraryMonitoringService>.Instance);
        engine = new TestEngine();
        provider = new DeferredArtworkProvider();
        first = new Track(Guid.NewGuid(), new Uri("file:///music/first.flac"), "First", "Artist", "Album", TimeSpan.FromMinutes(3));
        second = first with { Id = Guid.NewGuid(), Location = new Uri("file:///music/second.flac") };
        viewModel = new MainWindowViewModel(importer, repository, repository, repository,
            repository, settings, monitoring, engine, provider);
        await viewModel.LoadAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        viewModel.Dispose();
        provider.First.TrySetResult(null);
        provider.Second.TrySetResult(null);
        await viewModel.WaitForArtworkAsync();
        await viewModel.FlushPreferencesAsync();
        monitoring.Dispose();
        settings.Dispose();
        await repository.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task PlaybackSnapshots_PositionAndPauseChanges_ReadArtworkOnce()
    {
        engine.Snapshot = engine.Snapshot with { Track = first, Status = PlaybackStatus.Playing };
        viewModel.StartListening();
        engine.Snapshot = engine.Snapshot with { Position = TimeSpan.FromSeconds(5), Status = PlaybackStatus.Paused };
        viewModel.StartListening();
        Uri cover = new("file:///cache/first.image");
        provider.First.SetResult(cover);
        await viewModel.WaitForArtworkAsync();

        Assert.Multiple(() =>
        {
            Assert.That(provider.Requests, Is.EqualTo(new[] { first.Location }));
            Assert.That(viewModel.ArtworkLocation, Is.EqualTo(cover));
        });
    }

    [Test]
    public async Task PlaybackSnapshots_RapidTrackChange_DiscardsLateArtworkFromPreviousTrack()
    {
        engine.Snapshot = engine.Snapshot with { Track = first };
        viewModel.StartListening();
        engine.Snapshot = engine.Snapshot with { Track = second };
        viewModel.StartListening();
        provider.First.SetResult(new Uri("file:///cache/stale.image"));
        await provider.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(viewModel.ArtworkLocation, Is.Null);
        Uri secondCover = new("file:///cache/second.image");
        provider.Second.SetResult(secondCover);
        await viewModel.WaitForArtworkAsync();

        Assert.That(viewModel.ArtworkLocation, Is.EqualTo(secondCover));
        Assert.That(provider.Requests, Is.EqualTo(new[] { first.Location, second.Location }));
    }

    [Test]
    public async Task StopListening_PendingRead_DrainsWithoutPublishingArtwork()
    {
        engine.Snapshot = engine.Snapshot with { Track = first };
        viewModel.StartListening();
        viewModel.StopListening();
        Task drained = viewModel.WaitForArtworkAsync();
        Assert.That(drained.IsCompleted, Is.False);
        provider.First.SetResult(new Uri("file:///cache/stale.image"));
        await drained;

        Assert.That(viewModel.ArtworkLocation, Is.Null);
    }

    [Test]
    public async Task PlaybackSnapshots_ArtworkFailure_DoesNotReplacePlaybackStatusMessage()
    {
        viewModel.StatusMessage = "Playback is ready.";
        engine.Snapshot = engine.Snapshot with { Track = first };
        viewModel.StartListening();
        provider.First.SetException(new IOException("Unreadable cover."));
        await viewModel.WaitForArtworkAsync();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.ArtworkLocation, Is.Null);
            Assert.That(viewModel.StatusMessage, Is.EqualTo("Playback is ready."));
        });
    }

    [Test]
    public async Task PlaybackSnapshots_NoLoadedTrack_ClearsPreviousArtwork()
    {
        engine.Snapshot = engine.Snapshot with { Track = first };
        viewModel.StartListening();
        provider.First.SetResult(new Uri("file:///cache/cover.image"));
        await viewModel.WaitForArtworkAsync();
        engine.Snapshot = engine.Snapshot with { Track = null };
        viewModel.StartListening();

        Assert.That(viewModel.ArtworkLocation, Is.Null);
    }

    [Test]
    public async Task PlaybackSnapshots_ChangedMetadata_RefreshesArtworkForSameLocation()
    {
        engine.Snapshot = engine.Snapshot with { Track = first };
        viewModel.StartListening();
        provider.First.SetResult(new Uri("file:///cache/old.image"));
        await viewModel.WaitForArtworkAsync();
        engine.Snapshot = engine.Snapshot with { Track = first with { DateModified = DateTimeOffset.UtcNow } };
        viewModel.StartListening();
        await provider.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(viewModel.ArtworkLocation, Is.Null);
        provider.Second.SetResult(new Uri("file:///cache/new.image"));
        await viewModel.WaitForArtworkAsync();

        Assert.That(viewModel.ArtworkLocation, Is.EqualTo(new Uri("file:///cache/new.image")));
        Assert.That(provider.Requests, Has.Count.EqualTo(2));
    }

    private sealed class DeferredArtworkProvider : IArtworkProvider
    {
        public List<Uri> Requests { get; } = [];
        public TaskCompletionSource<Uri?> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Uri?> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Uri?> GetAsync(Uri mediaLocation, CancellationToken cancellationToken = default)
        {
            Requests.Add(mediaLocation);
            if (Requests.Count == 1) return First.Task;
            SecondStarted.TrySetResult();
            return Second.Task;
        }
    }

    private sealed class TestEngine : IPlaybackEngine
    {
        public PlaybackSnapshot Snapshot { get; set; } = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8);
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
