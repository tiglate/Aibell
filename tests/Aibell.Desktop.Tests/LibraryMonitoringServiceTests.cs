using Aibell.Application.Library;
using Aibell.Desktop.Services;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.Tests;

/// <summary>
/// The folder watcher behind "added, changed and removed files are reconciled automatically" (MIG-009, MIG-012, MIG-015), on a real
/// folder and the real importer. Changes are debounced for a second, so each test waits for the library to change (polling, with a
/// generous limit) instead of sleeping for a fixed time; only "nothing happens" checks have to wait out a fixed period.
/// </summary>
[TestFixture]
public sealed class LibraryMonitoringServiceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private string directory = null!;
    private string music = null!;
    private SqliteTrackRepository repository = null!;
    private FlakyDiscovery discovery = null!;
    private LibraryMonitoringService service = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Directory.CreateTempSubdirectory("aibell-monitor").FullName;
        music = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        repository = new(Path.Combine(directory, "library.db"));
        discovery = new();
        service = new(repository, new LibraryImportService(discovery, new AtlMediaMetadataReader(), repository), NullLogger<LibraryMonitoringService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await service.StopAsync(CancellationToken.None);
        service.Dispose();
        await repository.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    private static string Fixture(string name) => Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", name);

    private async Task<int> TrackCount() => (await repository.ListAsync()).Count;

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        DateTime deadline = DateTime.UtcNow + Limit;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("The library did not reach the expected state in time.");
    }

    private async Task StartWatchingAsync()
    {
        await repository.AddAsync(music);
        await service.StartAsync(CancellationToken.None);
    }

    [Test]
    public async Task Start_ImportsTheSavedFoldersAndRaisesTheSynchronizedEvent()
    {
        File.Copy(Fixture("fixture.mp3"), Path.Combine(music, "one.mp3"));
        File.Copy(Fixture("fixture.flac"), Path.Combine(music, "two.flac"));
        TaskCompletionSource<LibrarySynchronizedEventArgs> synchronized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.LibrarySynchronized += (_, args) => synchronized.TrySetResult(args);

        await StartWatchingAsync();
        LibrarySynchronizedEventArgs args = await synchronized.Task.WaitAsync(Limit);

        Assert.That(args.SourceRoot, Is.EqualTo(music));
        Assert.That(args.Result.Imported, Is.EqualTo(2));
        Assert.That(await TrackCount(), Is.EqualTo(2));
    }

    [Test]
    public async Task FileAddedWhileWatching_IsImportedAndFileRemovedIsDropped()
    {
        string first = Path.Combine(music, "one.mp3");
        File.Copy(Fixture("fixture.mp3"), first);
        await StartWatchingAsync();
        await UntilAsync(async () => await TrackCount() == 1);

        File.Copy(Fixture("fixture.flac"), Path.Combine(music, "added.flac"));
        await UntilAsync(async () => await TrackCount() == 2);

        File.Delete(first);
        await UntilAsync(async () => await TrackCount() == 1);
        Assert.That((await repository.ListAsync()).Single().Location.LocalPath, Does.EndWith("added.flac"));
    }

    [Test]
    public async Task FileMovedIntoASubfolder_FollowsTheMove()
    {
        File.Copy(Fixture("fixture.mp3"), Path.Combine(music, "one.mp3"));
        await StartWatchingAsync();
        await UntilAsync(async () => await TrackCount() == 1);

        string album = Directory.CreateDirectory(Path.Combine(music, "Album")).FullName;
        File.Move(Path.Combine(music, "one.mp3"), Path.Combine(album, "one.mp3"));

        await UntilAsync(async () => (await repository.ListAsync()).Select(track => track.Location.LocalPath).SequenceEqual([Path.Combine(album, "one.mp3")]));
        Assert.That(await TrackCount(), Is.EqualTo(1), "the move is not a second copy of the track");
    }

    [Test]
    public async Task StopWatching_StopsImportingNewFilesFromThatFolder()
    {
        File.Copy(Fixture("fixture.mp3"), Path.Combine(music, "one.mp3"));
        TaskCompletionSource initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int events = 0;
        service.LibrarySynchronized += (_, _) => { Interlocked.Increment(ref events); initial.TrySetResult(); };
        await StartWatchingAsync();
        await initial.Task.WaitAsync(Limit);
        Interlocked.Exchange(ref events, 0);

        await service.StopWatchingAsync(music);
        File.Copy(Fixture("fixture.flac"), Path.Combine(music, "late.flac"));
        // A change would be imported about a second after it is noticed; there is nothing to wait for when nothing should happen.
        await Task.Delay(TimeSpan.FromSeconds(3.5));

        Assert.That(await TrackCount(), Is.EqualTo(1));
        Assert.That(events, Is.Zero);
    }

    [Test]
    public async Task Watch_AfterStopWatching_ResumesImporting()
    {
        File.Copy(Fixture("fixture.mp3"), Path.Combine(music, "one.mp3"));
        await StartWatchingAsync();
        await UntilAsync(async () => await TrackCount() == 1);
        await service.StopWatchingAsync(music);

        service.Watch(music);
        File.Copy(Fixture("fixture.flac"), Path.Combine(music, "back.flac"));

        await UntilAsync(async () => await TrackCount() == 2);
    }

    [Test]
    public async Task FailedSynchronization_IsSurvivedAndTheNextChangeIsStillImported()
    {
        File.Copy(Fixture("fixture.mp3"), Path.Combine(music, "one.mp3"));
        await StartWatchingAsync();
        await UntilAsync(async () => await TrackCount() == 1);
        discovery.FailNext = true;

        File.Copy(Fixture("fixture.flac"), Path.Combine(music, "during-failure.flac"));
        await UntilAsync(() => Task.FromResult(!discovery.FailNext));
        File.Copy(Fixture("fixture.wav"), Path.Combine(music, "after-failure.wav"));

        await UntilAsync(async () => await TrackCount() == 3);
    }

    [Test]
    public async Task WatchingAFolderThatDoesNotExist_DoesNothingAndDoesNotThrow()
    {
        string missing = Path.Combine(directory, "unplugged");

        Assert.That(() => service.Watch(missing), Throws.Nothing);
        await service.StopWatchingAsync(missing);
    }

    private sealed class FlakyDiscovery : IMediaFileDiscovery
    {
        private readonly FileSystemMediaDiscovery inner = new();

        public volatile bool FailNext;

        public async IAsyncEnumerable<Uri> DiscoverAsync(IReadOnlyCollection<string> roots, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("The drive went away during the scan.");
            }
            await foreach (Uri uri in inner.DiscoverAsync(roots, cancellationToken)) yield return uri;
        }
    }
}
