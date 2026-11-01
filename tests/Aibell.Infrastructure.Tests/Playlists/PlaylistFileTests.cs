using System.Security.Cryptography;
using System.Text;
using Aibell.Application.Library;
using Aibell.Application.Playlists;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Playlists;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Playlists;

[TestFixture]
public sealed class PlaylistFileTests
{
    private static readonly int[] PlsIndices = [2, 7], SkippedIndices = [1, 2, 3], ReviewIndices = [2, 3];
    private string directory = null!;
    [SetUp] public void SetUp() => directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-playlist-" + Guid.NewGuid().ToString("N"))).FullName;
    [TearDown] public void TearDown() => Directory.Delete(directory, recursive: true);

    [TestCase("m3u")]
    [TestCase("m3u8")]
    public async Task ReadAsync_ExtendedPlaylist_ResolvesRelativeUnicodeAndFileUrisInOrder(string extension)
    {
        string subdirectory = Directory.CreateDirectory(Path.Combine(directory, "Music 日本語")).FullName;
        string first = Copy(subdirectory, "first ação.flac"), second = Copy(directory, "second.flac");
        string path = Path.Combine(directory, "Imported." + extension);
        await File.WriteAllTextAsync(path, "#EXTM3U\r\n#EXTINF:1,Ignored display override\r\nMusic 日本語/first ação.flac\r\n\r\n" + new Uri(second).AbsoluteUri + "\n", new UTF8Encoding(true));
        PlaylistFileContent result = await new PlaylistFileStore().ReadAsync(new Uri(path));
        Assert.That(result.Name, Is.EqualTo("Imported"));
        Assert.That(result.Entries.Select(entry => entry.Location), Is.EqualTo(new[] { new Uri(first), new Uri(second) }));
        Assert.That(result.Issues, Is.Empty);
    }

    [Test]
    public async Task ReadAsync_PlsOutOfOrderAndGaps_UsesFileIndicesAndIgnoresDisplayTags()
    {
        string first = Copy(directory, "first.flac"), second = Copy(directory, "second.flac");
        string path = Path.Combine(directory, "legacy.pls");
        await File.WriteAllTextAsync(path, "[Playlist]\nFile7=second.flac\nTitle7=Incorrect title\nFile2=first.flac\nLength2=-1\nVersion=2\nNumberOfEntries=2\n");
        PlaylistFileContent result = await new PlaylistFileStore().ReadAsync(new Uri(path));
        Assert.That(result.Entries.Select(entry => entry.Entry), Is.EqualTo(PlsIndices));
        Assert.That(result.Entries.Select(entry => entry.Location), Is.EqualTo(new[] { new Uri(first), new Uri(second) }));
    }

    [Test]
    public async Task ReadAsync_StreamsNestedAndNetworkPaths_ReportsOriginalIndicesWithoutFetching()
    {
        string local = Copy(directory, "local.flac");
        string path = Path.Combine(directory, "mixed.m3u8");
        await File.WriteAllTextAsync(path, "https://example.invalid/stream\nfile://remote-host/share/song.flac\nnested.m3u\nlocal.flac\n");
        PlaylistFileContent result = await new PlaylistFileStore().ReadAsync(new Uri(path));
        Assert.That(result.Entries.Single().Location, Is.EqualTo(new Uri(local)));
        Assert.That(result.Entries.Single().Entry, Is.EqualTo(4));
        Assert.That(result.Issues.Select(issue => issue.Entry), Is.EqualTo(SkippedIndices));
    }

    [TestCase("[playlist]\nFile1=a.flac\nFile1=b.flac", "pls")]
    [TestCase("[playlist]\nFile0=a.flac", "pls")]
    [TestCase("[not-playlist]\nFile1=a.flac", "pls")]
    [TestCase("[playlist]\nFile10001=a.flac", "pls")]
    public void ReadAsync_AmbiguousPls_Rejects(string content, string extension)
    {
        string path = Path.Combine(directory, "invalid." + extension); File.WriteAllText(path, content);
        Assert.That(async () => await new PlaylistFileStore().ReadAsync(new Uri(path)), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void ReadAsync_HlsOrExcessiveInput_RejectsBeforeAnyImport()
    {
        string path = Path.Combine(directory, "stream.m3u8"); File.WriteAllText(path, "#EXTM3U\n#EXT-X-TARGETDURATION:10\nsegment.ts");
        Assert.That(async () => await new PlaylistFileStore().ReadAsync(new Uri(path)), Throws.TypeOf<NotSupportedException>());
        File.WriteAllText(path, new string('x', 4 * 1024 * 1024 + 1));
        Assert.That(async () => await new PlaylistFileStore().ReadAsync(new Uri(path)), Throws.TypeOf<InvalidDataException>());
        File.WriteAllText(path, string.Join('\n', Enumerable.Repeat("track.flac", 10001)));
        Assert.That(async () => await new PlaylistFileStore().ReadAsync(new Uri(path)), Throws.TypeOf<InvalidDataException>());
        // Invalid UTF-8 without a Unicode BOM must not silently corrupt file names.
        File.WriteAllBytes(path, [0xc0, 0xaf]);
        Assert.That(async () => await new PlaylistFileStore().ReadAsync(new Uri(path)), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task WriteAsync_UnicodeAndLineBreaks_RoundTripsUrisWithoutChangingAudio()
    {
        string source = Copy(directory, "音楽 #1.flac"); string original = Hash(source);
        Track track = new(TrackIdentity.FromLocation(new Uri(source)), new Uri(source), "Title\n#injected", "Artist\rName", "Album", TimeSpan.FromSeconds(1));
        string target = Path.Combine(directory, "export.m3u8");
        await new PlaylistFileStore().WriteAsync(new Uri(target), new[] { track });
        string[] lines = await File.ReadAllLinesAsync(target);
        Assert.That(lines, Has.Length.EqualTo(3));
        Assert.That(lines[0], Is.EqualTo("#EXTM3U"));
        Assert.That(lines[1], Does.Contain("Artist Name - Title #injected"));
        Assert.That((await new PlaylistFileStore().ReadAsync(new Uri(target))).Entries.Single().Location, Is.EqualTo(new Uri(source)));
        Assert.That(Hash(source), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(directory, ".aibell-playlist-*.tmp"), Is.Empty);
    }

    [Test]
    public async Task WriteAsync_PreCancelledOrInvalidExtension_PreservesExistingDestinationAndCleansUp()
    {
        string path = Path.Combine(directory, "existing.m3u8"); await File.WriteAllTextAsync(path, "original");
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.That(async () => await new PlaylistFileStore().WriteAsync(new Uri(path), [], cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("original"));
        string audio = Copy(directory, "audio.flac"); string hash = Hash(audio);
        Assert.That(async () => await new PlaylistFileStore().WriteAsync(new Uri(audio), []), Throws.TypeOf<NotSupportedException>());
        Assert.That(Hash(audio), Is.EqualTo(hash));
        Assert.That(Directory.GetFiles(directory, ".aibell-playlist-*.tmp"), Is.Empty);
    }

    [Test]
    public async Task PrepareAndImport_ReviewIsReadOnlyThenCommitsOrderedTracksWithoutWatchingFolders()
    {
        string first = Copy(directory, "first.flac"), second = Copy(directory, "second.flac");
        string original = Hash(first);
        string playlist = Path.Combine(directory, "review.m3u8");
        await File.WriteAllTextAsync(playlist, "second.flac\nmissing.flac\nhttps://example.invalid/live\nfirst.flac\nsecond.flac\n");
        await using SqliteTrackRepository repository = new(Path.Combine(directory, "library.db"));
        LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), repository);
        PlaylistFileService service = new(new PlaylistFileStore(), importer, repository, repository);
        PlaylistImportPreview preview = await service.PrepareAsync(new Uri(playlist));
        Assert.That(preview.Tracks.Select(track => track.Location), Is.EqualTo(new[] { new Uri(second), new Uri(first) }));
        Assert.That(preview.Issues.Select(issue => issue.Entry), Is.EqualTo(ReviewIndices));
        Assert.That(preview.Duplicates, Is.EqualTo(1));
        Assert.That(await repository.ListAsync(), Is.Empty);
        Assert.That(await ((Aibell.Application.Playlists.IPlaylistRepository)repository).ListAsync(), Is.Empty);
        var result = await service.ImportAsync("Reviewed", preview);
        Assert.That((await repository.GetTracksAsync(result.Id, "")).Select(track => track.Location), Is.EqualTo(preview.Tracks.Select(track => track.Location)));
        Assert.That(await ((ILibrarySourceRepository)repository).ListAsync(), Is.Empty);
        Assert.That(Hash(first), Is.EqualTo(original));
        await repository.DisposeAsync();
        await using SqliteTrackRepository reopened = new(Path.Combine(directory, "library.db"));
        Assert.That((await reopened.GetTracksAsync(result.Id, "")).Select(track => track.Id), Is.EqualTo(result.TrackIds));
    }

    [Test]
    public async Task Import_ExistingTrack_PreservesCatalogMetadataRatingAndHistory()
    {
        string file = Copy(directory, "existing.flac");
        await using SqliteTrackRepository repository = new(Path.Combine(directory, "library.db"));
        LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), repository);
        Track original = await importer.ImportTrackAsync(new Uri(file));
        await repository.SaveMetadataAsync(original.Id, TrackMetadata.FromTrack(original) with { Title = "Catalog title", Composer = "Catalog composer", Rating = 5 });
        await repository.RecordPlaybackStartedAsync(original.Id, DateTimeOffset.UtcNow);
        Track before = (await repository.ListAsync()).Single();
        Track stale = original with { Title = "Stale prepared tags", Rating = 1 };
        var result = await repository.ImportPlaylistAsync("Preserve", new[] { stale, stale });
        Assert.That(result.TrackIds, Has.Count.EqualTo(1));
        Assert.That((await repository.ListAsync()).Single(), Is.EqualTo(before));
    }

    [Test]
    public async Task Import_ConflictAfterFirstTrack_RollsBackPlaylistAndAllNewTracks()
    {
        await using SqliteTrackRepository repository = new(Path.Combine(directory, "library.db"));
        Track first = new(Guid.NewGuid(), new Uri(Path.Combine(directory, "first.flac")), "First", "Artist", "Album", TimeSpan.FromSeconds(1));
        Track collision = first with { Location = new Uri(Path.Combine(directory, "second.flac")) };
        Assert.That(async () => await repository.ImportPlaylistAsync("Rollback", new[] { first, collision }), Throws.TypeOf<InvalidDataException>());
        Assert.That(await repository.ListAsync(), Is.Empty);
        Assert.That(await ((Aibell.Application.Playlists.IPlaylistRepository)repository).ListAsync(), Is.Empty);
        await repository.ImportPlaylistAsync("Name", new[] { first });
        Assert.That(async () => await repository.ImportPlaylistAsync("NAME", new[] { collision }), Throws.TypeOf<InvalidOperationException>());
        Assert.That(await repository.ListAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task PrepareAsync_CancelDuringMetadataRead_LeavesCatalogAndPlaylistsUnchanged()
    {
        string source = Path.Combine(directory, "cancel.m3u8"); await File.WriteAllTextAsync(source, "first.flac\n");
        await using SqliteTrackRepository repository = new(Path.Combine(directory, "library.db"));
        BlockingReader reader = new();
        LibraryImportService importer = new(new FileSystemMediaDiscovery(), reader, repository);
        PlaylistFileService service = new(new PlaylistFileStore(), importer, repository, repository);
        using CancellationTokenSource cancellation = new();
        Task prepare = service.PrepareAsync(new Uri(source), cancellation.Token);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        Assert.That(async () => await prepare, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(await repository.ListAsync(), Is.Empty);
        Assert.That(await ((IPlaylistRepository)repository).ListAsync(), Is.Empty);
    }

    [Test]
    public async Task ReadAsync_DosRelativeSeparatorsAndLongLocations_AreBoundedAndPortable()
    {
        string folder = Directory.CreateDirectory(Path.Combine(directory, "Music")).FullName;
        string audio = Copy(folder, "fixture.flac");
        string source = Path.Combine(directory, "portable.m3u");
        await File.WriteAllTextAsync(source, "Music\\fixture.flac\n" + new string('x', 16385));
        PlaylistFileContent content = await new PlaylistFileStore().ReadAsync(new Uri(source));
        Assert.That(content.Entries.Single().Location, Is.EqualTo(new Uri(audio)));
        Assert.That(content.Issues.Single().Location.Length, Is.LessThan(300));
    }

    [Test]
    public async Task ReadAsync_FileUriQueriesAndFragments_AreRejectedWithoutLosingEscapedFilenameCharacters()
    {
        string audio = Copy(directory, "hash # title.flac");
        string source = Path.Combine(directory, "identities.m3u8");
        string uri = new Uri(audio).AbsoluteUri;
        await File.WriteAllTextAsync(source, uri + "?query=1\n" + uri + "#fragment\n" + uri);
        PlaylistFileContent result = await new PlaylistFileStore().ReadAsync(new Uri(source));
        Assert.That(result.Entries.Single().Location, Is.EqualTo(new Uri(audio)));
        Assert.That(result.Issues, Has.Count.EqualTo(2));
    }

    private sealed class BlockingReader : IMediaMetadataReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Cancellation should finish the read.");
        }
    }

    private static string Copy(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.flac"), path);
        return path;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
