using System.Security.Cryptography;
using Aibell.Application.Audiobooks;
using Aibell.Application.Library;
using Aibell.Domain.Audiobooks;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Audiobooks;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Audiobooks;

[TestFixture]
public sealed class AudiobookChapterServiceTests
{
    private string directory = null!;
    private string book = null!;
    private AudiobookShelfEntry entry = null!;
    private RecordingHandoff handoff = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-chapter-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        book = Path.Combine(directory, "book.mp3");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.mp3"), book);
        ATL.Track track = new(book);
        track.Chapters.Add(new ATL.ChapterInfo { Title = "One", StartTime = 0 });
        track.Chapters.Add(new ATL.ChapterInfo { Title = "Two", StartTime = 500 });
        Assert.That(track.Save(), Is.True);
        entry = new(new AudiobookFiles().Canonicalize(new Uri(book)), "Book", "Author", TimeSpan.FromSeconds(1), TimeSpan.Zero, DateTimeOffset.UnixEpoch);
        handoff = new();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    private static AudiobookShelfService Service(IAudiobookChapterReader? chapters) =>
        new(new NoRepository(), new NoMetadata(), new AudiobookFiles(), null, chapters);

    [Test]
    public async Task PlayChapterAsync_RealFile_HandsTheChapterStartToTheSharedHandoffAndNeverChangesTheFile()
    {
        byte[] before = SHA256.HashData(File.ReadAllBytes(book));
        AudiobookShelfService service = Service(new AtlAudiobookChapterReader());

        AudiobookChapterCatalog catalog = await service.ListChaptersAsync(entry);
        TimeSpan start = await service.PlayChapterAsync(entry, catalog.Chapters[1], handoff);

        Assert.That(start, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        Assert.That(handoff.Requests, Has.Count.EqualTo(1));
        Assert.That(handoff.Requests[0].StartPosition, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        Assert.That(handoff.Requests[0].File, Is.EqualTo(entry.Location));
        Assert.That(handoff.Requests[0].Duration, Is.EqualTo(catalog.Duration));
        Assert.That(SHA256.HashData(File.ReadAllBytes(book)), Is.EqualTo(before));
    }

    [Test]
    public async Task PlayChapterAsync_ChaptersChangedSinceListing_RefusesInsteadOfSeekingStale()
    {
        AudiobookShelfService service = Service(new AtlAudiobookChapterReader());
        AudiobookChapterCatalog catalog = await service.ListChaptersAsync(entry);
        ATL.Track track = new(book);
        track.Chapters.Clear();
        track.Chapters.Add(new ATL.ChapterInfo { Title = "Only", StartTime = 100 });
        Assert.That(track.Save(), Is.True);

        Assert.That(async () => await service.PlayChapterAsync(entry, catalog.Chapters[1], handoff), Throws.InvalidOperationException.With.Message.Contains("changed"));
        Assert.That(handoff.Requests, Is.Empty);
    }

    [Test]
    public void PlayChapterAsync_ListedStartNowBeyondTheRealDuration_NeverPlaysTheOpening()
    {
        FixedChapters shrunk = new(new AudiobookChapterListing(TimeSpan.FromSeconds(10), [new(TimeSpan.Zero, "A"), new(TimeSpan.FromSeconds(90), "Far")]));
        AudiobookShelfService service = Service(shrunk);

        Assert.That(async () => await service.PlayChapterAsync(entry, new(TimeSpan.FromSeconds(90), "Far"), handoff), Throws.InvalidOperationException);
        Assert.That(handoff.Requests, Is.Empty);
    }

    [Test]
    public async Task ListChaptersAsync_FileWithoutChapters_ReturnsAnEmptyCatalog()
    {
        ATL.Track track = new(book);
        track.Chapters.Clear();
        track.Save();

        AudiobookChapterCatalog catalog = await Service(new AtlAudiobookChapterReader()).ListChaptersAsync(entry);

        Assert.That(catalog.Chapters, Is.Empty);
    }

    [Test]
    public void ListChaptersAsync_WithoutAChapterReader_IsUnavailableButOrdinaryServiceStillConstructs()
    {
        AudiobookShelfService service = Service(null);

        Assert.That(service.SupportsChapters, Is.False);
        Assert.That(async () => await service.ListChaptersAsync(entry), Throws.InvalidOperationException);
    }

    [Test]
    public async Task ListChaptersAsync_ReaderIgnoringCancellation_DrainsAndRejectsTheLateResult()
    {
        TaskCompletionSource<AudiobookChapterListing> never = new();
        using CancellationTokenSource cancel = new();
        Task<AudiobookChapterCatalog> listing = Service(new FixedChapters(never.Task)).ListChaptersAsync(entry, cancel.Token);
        cancel.Cancel();

        bool wasDraining = !listing.IsCompleted;
        never.SetResult(new(TimeSpan.FromSeconds(1), []));
        await Assert.ThatAsync(async () => await listing, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(wasDraining, Is.True, "the service owns accepted file reads until completion");
    }

    [Test]
    public void ListChaptersAsync_CancelledWhileTheReaderFinishesAnyway_DiscardsTheResult()
    {
        using CancellationTokenSource cancel = new();
        AudiobookShelfService service = Service(new FixedChapters(new AudiobookChapterListing(TimeSpan.FromSeconds(10), [new(TimeSpan.Zero, "A")]), cancel.Cancel));

        Assert.That(async () => await service.ListChaptersAsync(entry, cancel.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void ListChaptersAsync_UnreadableFile_ReportsAnActionableError()
    {
        Assert.That(async () => await Service(new FixedChapters(Task.FromException<AudiobookChapterListing>(new InvalidDataException("bad")))).ListChaptersAsync(entry),
            Throws.InvalidOperationException.With.Message.Contains("could not be read"));
    }

    [Test]
    public void ListChaptersAsync_MissingFile_ReportsFileNotFound()
    {
        File.Delete(book);
        Assert.That(async () => await Service(new AtlAudiobookChapterReader()).ListChaptersAsync(entry), Throws.TypeOf<FileNotFoundException>());
    }

    private sealed class FixedChapters(Task<AudiobookChapterListing> result, Action? during = null) : IAudiobookChapterReader
    {
        public FixedChapters(AudiobookChapterListing listing, Action? during = null) : this(Task.FromResult(listing), during) { }
        public Task<AudiobookChapterListing> ReadAsync(Uri file, CancellationToken cancellationToken = default) { during?.Invoke(); return result; }
    }

    private sealed class RecordingHandoff : IAudiobookPlaybackHandoff
    {
        public List<AudiobookPlaybackRequest> Requests { get; } = [];
        public Task PlayAsync(AudiobookPlaybackRequest request, CancellationToken cancellationToken = default) { Requests.Add(request); return Task.CompletedTask; }
        public ValueTask<AudiobookPlaybackSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<AudiobookPlaybackSnapshot?>(null);
    }

    private sealed class NoMetadata : IMediaMetadataReader
    {
        public Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoRepository : IAudiobookShelfRepository
    {
        public Task<IReadOnlyList<AudiobookShelfEntry>> ListShelfAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AddToShelfAsync(AudiobookShelfEntry entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SaveShelfPositionAsync(Uri location, TimeSpan position, TimeSpan duration, DateTimeOffset at, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveFromShelfAsync(Uri location, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
