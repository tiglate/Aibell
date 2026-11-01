using System.Runtime.CompilerServices;
using Aibell.Application.Library;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Application.Tests.Library;

[TestFixture]
public sealed class LibraryImportServiceTests
{
    [Test]
    public async Task ImportAsync_ImportsReadableFilesAndReportsFailures()
    {
        Uri readable = new("file:///music/readable.flac");
        Uri broken = new("file:///music/broken.mp3");
        StubDiscovery discovery = new(readable, broken);
        StubMetadataReader reader = new(broken);
        RecordingRepository repository = new();
        LibraryImportService service = new(discovery, reader, repository);

        LibraryImportResult result = await service.ImportAsync(["/music"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Discovered, Is.EqualTo(2));
            Assert.That(result.Imported, Is.EqualTo(1));
            Assert.That(result.Removed, Is.Zero);
            Assert.That(result.Issues, Has.Count.EqualTo(1));
            Assert.That(repository.Tracks, Has.Count.EqualTo(1));
            Assert.That(repository.Tracks[0].Title, Is.EqualTo("readable"));
        });
    }

    [Test]
    public async Task ReadTrackAsync_StagesMetadataWithoutChangingLibrary()
    {
        RecordingRepository repository = new();
        LibraryImportService service = new(new StubDiscovery(), new StubMetadataReader(new Uri("file:///broken.mp3")), repository);

        Track track = await service.ReadTrackAsync(new Uri("file:///music/staged.flac"));

        Assert.Multiple(() =>
        {
            Assert.That(track.Title, Is.EqualTo("staged"));
            Assert.That(repository.Tracks, Is.Empty);
            Assert.That(repository.MissingScans, Is.Zero);
        });
    }

    [Test]
    public async Task ImportTrackAsync_ImportsOnlyTheRequestedFileWithoutReconciliation()
    {
        RecordingRepository repository = new();
        LibraryImportService service = new(new StubDiscovery(), new StubMetadataReader(new Uri("file:///broken.mp3")), repository);

        Track track = await service.ImportTrackAsync(new Uri("file:///music/staged.flac"));

        Assert.Multiple(() =>
        {
            Assert.That(repository.Tracks.Single().Id, Is.EqualTo(track.Id));
            Assert.That(repository.MissingScans, Is.Zero);
        });
    }

    [Test]
    public async Task ImportTrackAsync_UnreadableFile_DoesNotChangeLibrary()
    {
        Uri broken = new("file:///music/broken.mp3");
        RecordingRepository repository = new();
        LibraryImportService service = new(new StubDiscovery(), new StubMetadataReader(broken), repository);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.ImportTrackAsync(broken));
        Assert.That(repository.Tracks, Is.Empty);
    }

    private sealed class StubDiscovery(params Uri[] locations) : IMediaFileDiscovery
    {
        public async IAsyncEnumerable<Uri> DiscoverAsync(
            IReadOnlyCollection<string> roots,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (Uri location in locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return location;
                await Task.Yield();
            }
        }
    }

    private sealed class StubMetadataReader(Uri brokenLocation) : IMediaMetadataReader
    {
        public Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default)
        {
            if (location == brokenLocation)
            {
                throw new InvalidDataException("Unreadable metadata.");
            }

            return Task.FromResult(new MediaMetadata(null, "Artist", "Album", TimeSpan.FromMinutes(3)));
        }
    }

    private sealed class RecordingRepository : ITrackRepository
    {
        public List<Track> Tracks { get; } = [];
        public int MissingScans { get; private set; }

        public Task<IReadOnlyList<Track>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Track>>(Tracks);
        }

        public Task<IReadOnlyList<Track>> SearchAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Track>>(Tracks);
        }

        public Task UpsertAsync(
            Track track,
            string sourceRoot,
            CancellationToken cancellationToken = default)
        {
            Tracks.Add(track);
            return Task.CompletedTask;
        }

        public Task SetRatingAsync(
            Guid trackId,
            int rating,
            CancellationToken cancellationToken = default)
        {
            int index = Tracks.FindIndex(track => track.Id == trackId);
            if (index >= 0)
            {
                Tracks[index] = Tracks[index] with { Rating = rating };
            }

            return Task.CompletedTask;
        }

        public Task RecordPlaybackStartedAsync(
            Guid trackId,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RecordSkippedAsync(
            Guid trackId,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> DeleteMissingAsync(
            string sourceRoot,
            IReadOnlyCollection<Uri> existingLocations,
            CancellationToken cancellationToken = default)
        {
            MissingScans++;
            return Task.FromResult(0);
        }
    }
}
