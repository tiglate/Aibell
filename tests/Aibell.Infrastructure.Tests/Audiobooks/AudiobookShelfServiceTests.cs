using System.Security.Cryptography;
using Aibell.Application.Audiobooks;
using Aibell.Application.Library;
using Aibell.Domain.Audiobooks;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Audiobooks;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Audiobooks;

[TestFixture]
public sealed class AudiobookShelfServiceTests
{
    private string directory = null!;
    private string database = null!;
    private string book = null!;
    private FakeReader reader = null!;
    private FakeHandoff handoff = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-audiobooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        database = Path.Combine(directory, "library.db");
        book = Path.Combine(directory, "Moby Dick.mp3");
        File.WriteAllBytes(book, [1, 2, 3, 4, 5]);
        reader = new(new MediaMetadata("Moby Dick", "Herman Melville", "Book", TimeSpan.FromHours(2)));
        handoff = new();
    }

    [TearDown]
    public void TearDown() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }

    private AudiobookShelfService Service(SqliteTrackRepository repository) => new(repository, reader, new AudiobookFiles());

    [Test]
    public async Task Add_PersistsLabelsAndDuration_ThenListsAfterReopen_WithoutAdmittingToMusicLibrary()
    {
        await using (SqliteTrackRepository repository = new(database))
        {
            AudiobookAddResult result = await Service(repository).AddAsync(new Uri(book));
            Assert.That(result.Outcome, Is.EqualTo(AudiobookAddOutcome.Added));
        }
        await using SqliteTrackRepository reopened = new(database);
        AudiobookShelfEntry entry = (await Service(reopened).ListAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Title, Is.EqualTo("Moby Dick"));
            Assert.That(entry.Author, Is.EqualTo("Herman Melville"));
            Assert.That(entry.Duration, Is.EqualTo(TimeSpan.FromHours(2)));
            Assert.That(entry.SavedPosition, Is.EqualTo(TimeSpan.Zero));
            Assert.That(entry.PositionSaved, Is.Null);
        });
        Assert.That(await reopened.ListAsync(), Is.Empty, "the music library is untouched");
    }

    [Test]
    public async Task Add_SameFileTwiceAndViaSymlinkOrDotSegments_IsOneEntryKeepingSavedPosition()
    {
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfService service = Service(repository);
        await service.AddAsync(new Uri(book));
        handoff.Current = new(new Uri(book), TimeSpan.FromMinutes(5), true);
        await service.SaveCurrentAsync(handoff);
        string link = Path.Combine(directory, "alias.mp3");
        string linkedDirectory = Path.Combine(directory, "linkdir");
        File.CreateSymbolicLink(link, book);
        Directory.CreateSymbolicLink(linkedDirectory, directory);
        string dotted = Path.Combine(directory, "sub", "..", "Moby Dick.mp3");

        foreach (string alias in new[] { book, link, Path.Combine(linkedDirectory, "Moby Dick.mp3"), dotted })
        {
            AudiobookAddResult again = await service.AddAsync(new Uri(alias));
            Assert.That(again.Outcome, Is.EqualTo(AudiobookAddOutcome.AlreadyOnShelf), alias);
            Assert.That(again.Entry.SavedPosition, Is.EqualTo(TimeSpan.FromMinutes(5)));
        }
        Assert.That(await service.ListAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Add_UnsupportedMissingOrUnreadable_ThrowsActionableErrorsAndStoresNothing()
    {
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfService service = Service(repository);
        Assert.That(async () => await service.AddAsync(new Uri("https://example.invalid/a.mp3")), Throws.ArgumentException);
        Assert.That(async () => await service.AddAsync(new Uri(Path.Combine(directory, "notes.txt"))), Throws.ArgumentException);
        Assert.That(async () => await service.AddAsync(new Uri(Path.Combine(directory, "gone.mp3"))), Throws.TypeOf<FileNotFoundException>());
        reader.Failure = new InvalidDataException("bad tags");
        Assert.That(async () => await service.AddAsync(new Uri(book)), Throws.TypeOf<InvalidDataException>());
        Assert.That(await service.ListAsync(), Is.Empty);
    }

    [Test]
    public async Task Add_NegativeOrUnboundedDuration_IsBoundedAndTitleFallsBackToFileName()
    {
        reader.Metadata = new(null, null, null, TimeSpan.MaxValue);
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfEntry entry = (await Service(repository).AddAsync(new Uri(book))).Entry;
        Assert.That(entry.Title, Is.EqualTo("Moby Dick"));
        Assert.That(entry.Duration, Is.EqualTo(ResumeRules.MaximumDuration));
    }

    [Test]
    public async Task SaveCurrent_OnlyForMatchingPlayingOrPausedShelfFile_PersistsAcrossReopen()
    {
        string other = Path.Combine(directory, "other.mp3"); File.WriteAllBytes(other, [9]);
        await using (SqliteTrackRepository repository = new(database))
        {
            AudiobookShelfService service = Service(repository);
            await service.AddAsync(new Uri(book));
            Assert.That((await service.SaveCurrentAsync(handoff)).Outcome, Is.EqualTo(AudiobookSaveOutcome.NothingPlaying));
            handoff.Current = new(new Uri(other), TimeSpan.FromMinutes(1), true);
            Assert.That((await service.SaveCurrentAsync(handoff)).Outcome, Is.EqualTo(AudiobookSaveOutcome.NotOnShelf));
            handoff.Current = new(new Uri(book), TimeSpan.FromMinutes(1), false);
            Assert.That((await service.SaveCurrentAsync(handoff)).Outcome, Is.EqualTo(AudiobookSaveOutcome.NothingPlaying), "stopped/ended players are not saved");
            handoff.Current = new(new Uri(book), TimeSpan.FromMinutes(42), true);
            AudiobookSaveResult saved = await service.SaveCurrentAsync(handoff);
            Assert.That(saved.Outcome, Is.EqualTo(AudiobookSaveOutcome.Saved));
            Assert.That(saved.Position, Is.EqualTo(TimeSpan.FromMinutes(42)));
        }
        await using SqliteTrackRepository reopened = new(database);
        AudiobookShelfEntry entry = (await Service(reopened).ListAsync()).Single();
        Assert.That(entry.SavedPosition, Is.EqualTo(TimeSpan.FromMinutes(42)));
        Assert.That(entry.PositionSaved, Is.Not.Null);
    }

    [Test]
    public async Task SaveCurrent_NegativeOrBeyondDuration_ClampsToRealCurrentDuration()
    {
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfService service = Service(repository);
        await service.AddAsync(new Uri(book));
        reader.Metadata = reader.Metadata with { Duration = TimeSpan.FromMinutes(30) };
        handoff.Current = new(new Uri(book), TimeSpan.FromHours(5), true);
        Assert.That((await service.SaveCurrentAsync(handoff)).Position, Is.EqualTo(TimeSpan.FromMinutes(30)));
        handoff.Current = new(new Uri(book), TimeSpan.FromSeconds(-9), true);
        Assert.That((await service.SaveCurrentAsync(handoff)).Position, Is.EqualTo(TimeSpan.Zero));
        Assert.That((await service.ListAsync()).Single().Duration, Is.EqualTo(TimeSpan.FromMinutes(30)));
    }

    [Test]
    public async Task Play_ResumeAndStartOver_SendSeekPositionAndClampToChangedDuration()
    {
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfService service = Service(repository);
        await service.AddAsync(new Uri(book));
        handoff.Current = new(new Uri(book), TimeSpan.FromMinutes(90), true);
        await service.SaveCurrentAsync(handoff);
        AudiobookShelfEntry entry = (await service.ListAsync()).Single();

        Assert.That(await service.PlayAsync(entry, resume: true, handoff), Is.EqualTo(TimeSpan.FromMinutes(90)));
        Assert.That(handoff.Requests.Last().StartPosition, Is.EqualTo(TimeSpan.FromMinutes(90)));
        Assert.That(await service.PlayAsync(entry, resume: false, handoff), Is.EqualTo(TimeSpan.Zero));

        reader.Metadata = reader.Metadata with { Duration = TimeSpan.FromMinutes(20) };
        Assert.That(await service.PlayAsync(entry, resume: true, handoff), Is.EqualTo(TimeSpan.Zero), "saved beyond the shortened file counts as finished");
        reader.Metadata = reader.Metadata with { Duration = TimeSpan.FromMinutes(91) };
        Assert.That(await service.PlayAsync(entry, resume: true, handoff), Is.EqualTo(TimeSpan.FromMinutes(90)), "the last minute is not silently discarded");
        reader.Metadata = reader.Metadata with { Duration = TimeSpan.Zero };
        Assert.That(await service.PlayAsync(entry, resume: true, handoff), Is.EqualTo(TimeSpan.Zero), "unseekable zero-length cannot resume nonzero");
        Assert.That(handoff.Requests.Last().Duration, Is.Null);
    }

    [Test]
    public async Task Play_MissingOrUnreadableFile_FailsWithActionableMessageWithoutStartingPlayback()
    {
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfService service = Service(repository);
        AudiobookShelfEntry entry = (await service.AddAsync(new Uri(book))).Entry;
        reader.Failure = new InvalidDataException("truncated");
        InvalidOperationException unreadable = (await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.PlayAsync(entry, true, handoff)))!;
        Assert.That(unreadable.Message, Does.Contain("could not be read"));
        reader.Failure = null;
        File.Delete(book);
        FileNotFoundException missing = (await Assert.ThrowsAsync<FileNotFoundException>(async () => await service.PlayAsync(entry, true, handoff)))!;
        Assert.That(missing.Message, Does.Contain("missing").And.Contain("Remove"));
        Assert.That(handoff.Requests, Is.Empty);
    }

    [Test]
    public async Task Remove_DeletesOnlyTheShelfRecord_AndLeavesAudioBytesUntouched()
    {
        byte[] before = SHA256.HashData(File.ReadAllBytes(book));
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfService service = Service(repository);
        AudiobookShelfEntry entry = (await service.AddAsync(new Uri(book))).Entry;
        Assert.That(await service.RemoveAsync(entry), Is.True);
        Assert.That(await service.RemoveAsync(entry), Is.False);
        Assert.That(await service.ListAsync(), Is.Empty);
        Assert.That(SHA256.HashData(File.ReadAllBytes(book)), Is.EqualTo(before));
        Assert.That((await service.AddAsync(new Uri(book))).Outcome, Is.EqualTo(AudiobookAddOutcome.Added), "can be re-added");
    }

    [Test]
    public async Task Repository_RejectsPositionBeyondDuration_AndCancellationStopsWork()
    {
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfEntry entry = (await Service(repository).AddAsync(new Uri(book))).Entry;
        Assert.That(async () => await repository.SaveShelfPositionAsync(entry.Location, TimeSpan.FromHours(3), TimeSpan.FromHours(2), DateTimeOffset.UtcNow),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        using CancellationTokenSource cancelled = new(); await cancelled.CancelAsync();
        Assert.That(async () => await Service(repository).AddAsync(new Uri(book), cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task SaveCurrent_PausedSnapshotIsAccepted_ButMissingUnreadableOrCancelledKeepsThePreviousPosition()
    {
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfService service = Service(repository);
        await service.AddAsync(new Uri(book));
        handoff.Current = new(new Uri(book), TimeSpan.FromMinutes(10), CanSavePosition: true); // paused counts: central maps Playing|Paused
        await service.SaveCurrentAsync(handoff);

        handoff.Current = new(new Uri(book), TimeSpan.FromMinutes(50), true);
        reader.Failure = new InvalidDataException("truncated");
        InvalidOperationException unreadable = (await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.SaveCurrentAsync(handoff)))!;
        Assert.That(unreadable.Message, Does.Contain("not saved"));
        reader.Failure = null;
        using CancellationTokenSource cancelled = new(); await cancelled.CancelAsync();
        Assert.That(async () => await service.SaveCurrentAsync(handoff, cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
        File.Move(book, book + ".moved");
        FileNotFoundException missing = (await Assert.ThrowsAsync<FileNotFoundException>(async () => await service.SaveCurrentAsync(handoff)))!;
        Assert.That(missing.Message, Does.Contain("not saved"));
        Assert.That((await service.ListAsync()).Single().SavedPosition, Is.EqualTo(TimeSpan.FromMinutes(10)));
    }

    [Test]
    public async Task SaveCurrent_FileShortenedSinceAdding_ClampsAndStoresTheNewDuration()
    {
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfService service = Service(repository);
        await service.AddAsync(new Uri(book));
        reader.Metadata = reader.Metadata with { Duration = TimeSpan.FromMinutes(45) };
        handoff.Current = new(new Uri(book), TimeSpan.FromMinutes(50), true);
        Assert.That((await service.SaveCurrentAsync(handoff)).Position, Is.EqualTo(TimeSpan.FromMinutes(45)));
        AudiobookShelfEntry entry = (await service.ListAsync()).Single();
        Assert.That(entry.Duration, Is.EqualTo(TimeSpan.FromMinutes(45)));
        Assert.That(entry.SavedPosition, Is.EqualTo(entry.Duration));
    }

    [Test]
    public void Canonicalize_RejectsRemoteQueryAndNonAudioInputWithoutCoercingToAPath()
    {
        AudiobookFiles files = new();
        Assert.That(() => files.Canonicalize(new Uri("https://example.invalid/a/book.mp3")), Throws.ArgumentException);
        Assert.That(() => files.Canonicalize(new Uri(new Uri(book).AbsoluteUri + "?x=1")), Throws.ArgumentException);
        Assert.That(() => files.Canonicalize(new Uri(new Uri(book).AbsoluteUri + "#frag")), Throws.ArgumentException);
        Assert.That(() => files.Canonicalize(new Uri(Path.Combine(directory, "notes.txt"))), Throws.ArgumentException);
        Assert.That(files.Exists(new Uri("https://example.invalid/book.mp3")), Is.False);
    }

    [Test]
    public void Canonicalize_AncestorChainAndCycles_ResolveOrFailInsteadOfLooping()
    {
        AudiobookFiles files = new();
        string real = Path.Combine(directory, "real"); Directory.CreateDirectory(real);
        string target = Path.Combine(real, "Book.flac"); File.WriteAllBytes(target, [1]);
        string hopOne = Path.Combine(directory, "hop1"); Directory.CreateSymbolicLink(hopOne, real);
        string hopTwo = Path.Combine(directory, "hop2"); Directory.CreateSymbolicLink(hopTwo, hopOne);
        Assert.That(files.Canonicalize(new Uri(Path.Combine(hopTwo, "Book.flac"))).LocalPath, Is.EqualTo(target), "chained ancestor links resolve to the real file");

        string loopA = Path.Combine(directory, "loopA"), loopB = Path.Combine(directory, "loopB");
        Directory.CreateSymbolicLink(loopA, loopB); Directory.CreateSymbolicLink(loopB, loopA);
        Assert.That(() => files.Canonicalize(new Uri(Path.Combine(loopA, "Book.flac"))), Throws.InstanceOf<IOException>());
    }

    [Test]
    public async Task AtlReader_RenamedM4aAsM4b_ReadsDurationAndLabelsThroughTheRealShelf()
    {
        string m4b = Path.Combine(directory, "Renamed Book.m4b");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.m4a"), m4b);
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfService service = new(repository, new Aibell.Infrastructure.Metadata.AtlMediaMetadataReader(), new AudiobookFiles());
        AudiobookShelfEntry entry = (await service.AddAsync(new Uri(m4b))).Entry;
        Assert.That(entry.Duration, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(entry.Title, Is.Not.Empty);
        Assert.That(await service.PlayAsync(entry, resume: false, handoff), Is.EqualTo(TimeSpan.Zero));
        Assert.That(handoff.Requests.Single().File, Is.EqualTo(entry.Location));
    }

    [Test]
    [TestCase("add")]
    [TestCase("play")]
    [TestCase("save")]
    public async Task CancelAfterMetadata_IgnoringProviderCannotStartPlaybackOrChangeTheShelf(string action)
    {
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfService normal = Service(repository);
        AudiobookShelfEntry entry = (await normal.AddAsync(new Uri(book))).Entry;
        handoff.Current = new(entry.Location, TimeSpan.FromMinutes(10), true);
        await normal.SaveCurrentAsync(handoff);
        AudiobookShelfEntry before = (await repository.ListShelfAsync()).Single();
        if (action == "add") await repository.RemoveFromShelfAsync(entry.Location);
        handoff.Current = new(entry.Location, TimeSpan.FromMinutes(50), true);
        using CancellationTokenSource cancellation = new();
        reader.AfterRead = cancellation.Cancel;
        UncooperativeRepository ignoring = new(repository);
        AudiobookShelfService service = new(ignoring, reader, new AudiobookFiles());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            switch (action)
            {
                case "add": await service.AddAsync(entry.Location, cancellation.Token); break;
                case "play": await service.PlayAsync(entry, true, handoff, cancellation.Token); break;
                default: await service.SaveCurrentAsync(handoff, cancellation.Token); break;
            }
        });
        Assert.Multiple(() =>
        {
            Assert.That(ignoring.Writes, Is.Zero, "A cancelled use case must not initiate a write even if the repository ignores cancellation.");
            Assert.That(handoff.Requests, Is.Empty);
        });
        IReadOnlyList<AudiobookShelfEntry> after = await repository.ListShelfAsync();
        Assert.That(after, action == "add" ? Is.Empty : Is.EqualTo(new[] { before }));
    }

    [Test]
    public async Task SaveCurrent_CancelAfterSnapshot_DoesNotReadTheShelfOrMetadata()
    {
        await using SqliteTrackRepository repository = new(database);
        await Service(repository).AddAsync(new Uri(book));
        int reads = reader.Reads;
        using CancellationTokenSource cancellation = new();
        handoff.Current = new(new Uri(book), TimeSpan.FromMinutes(20), true);
        handoff.AfterSnapshot = cancellation.Cancel;
        UncooperativeRepository ignoring = new(repository);
        AudiobookShelfService service = new(ignoring, reader, new AudiobookFiles());

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await service.SaveCurrentAsync(handoff, cancellation.Token));

        Assert.Multiple(() =>
        {
            Assert.That(ignoring.Reads, Is.Zero);
            Assert.That(ignoring.Writes, Is.Zero);
            Assert.That(reader.Reads, Is.EqualTo(reads));
        });
    }

    [Test]
    public async Task SaveCurrent_CancelAfterShelfRead_DoesNotReadMetadataOrSavePosition()
    {
        await using SqliteTrackRepository repository = new(database);
        await Service(repository).AddAsync(new Uri(book));
        int reads = reader.Reads;
        using CancellationTokenSource cancellation = new();
        handoff.Current = new(new Uri(book), TimeSpan.FromMinutes(20), true);
        UncooperativeRepository ignoring = new(repository) { AfterRead = cancellation.Cancel };
        AudiobookShelfService service = new(ignoring, reader, new AudiobookFiles());

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await service.SaveCurrentAsync(handoff, cancellation.Token));

        Assert.Multiple(() =>
        {
            Assert.That(ignoring.Reads, Is.EqualTo(1));
            Assert.That(ignoring.Writes, Is.Zero);
            Assert.That(reader.Reads, Is.EqualTo(reads));
        });
        Assert.That((await repository.ListShelfAsync()).Single().PositionSaved, Is.Null);
    }

    [Test]
    [TestCase("add")]
    [TestCase("play")]
    [TestCase("save")]
    public async Task AlreadyCancelled_DoesNotCallMetadataPlayerOrRepository(string action)
    {
        await using SqliteTrackRepository repository = new(database);
        AudiobookShelfEntry entry = (await Service(repository).AddAsync(new Uri(book))).Entry;
        int reads = reader.Reads;
        handoff.Current = new(entry.Location, TimeSpan.FromMinutes(1), true);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        UncooperativeRepository ignoring = new(repository);
        AudiobookShelfService service = new(ignoring, reader, new AudiobookFiles());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            switch (action)
            {
                case "add": await service.AddAsync(entry.Location, cancellation.Token); break;
                case "play": await service.PlayAsync(entry, false, handoff, cancellation.Token); break;
                default: await service.SaveCurrentAsync(handoff, cancellation.Token); break;
            }
        });
        Assert.Multiple(() =>
        {
            Assert.That(reader.Reads, Is.EqualTo(reads));
            Assert.That(handoff.Snapshots, Is.Zero);
            Assert.That(handoff.Requests, Is.Empty);
            Assert.That(ignoring.Reads, Is.Zero);
            Assert.That(ignoring.Writes, Is.Zero);
        });
    }

    private sealed class UncooperativeRepository(IAudiobookShelfRepository inner) : IAudiobookShelfRepository
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Action? AfterRead { get; set; }
        public async Task<IReadOnlyList<AudiobookShelfEntry>> ListShelfAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            IReadOnlyList<AudiobookShelfEntry> entries = await inner.ListShelfAsync(CancellationToken.None);
            AfterRead?.Invoke();
            return entries;
        }
        public Task<bool> AddToShelfAsync(AudiobookShelfEntry entry, CancellationToken cancellationToken = default)
        { Writes++; return inner.AddToShelfAsync(entry, CancellationToken.None); }
        public Task<bool> SaveShelfPositionAsync(Uri location, TimeSpan position, TimeSpan duration, DateTimeOffset at, CancellationToken cancellationToken = default)
        { Writes++; return inner.SaveShelfPositionAsync(location, position, duration, at, CancellationToken.None); }
        public Task<bool> RemoveFromShelfAsync(Uri location, CancellationToken cancellationToken = default) => inner.RemoveFromShelfAsync(location, cancellationToken);
    }

    private sealed class FakeReader(MediaMetadata metadata) : IMediaMetadataReader
    {
        public MediaMetadata Metadata { get; set; } = metadata;
        public Exception? Failure { get; set; }
        public Action? AfterRead { get; set; }
        public int Reads { get; private set; }

        public Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default)
        {
            Reads++;
            cancellationToken.ThrowIfCancellationRequested();
            AfterRead?.Invoke();
            return Failure is null ? Task.FromResult(Metadata) : Task.FromException<MediaMetadata>(Failure);
        }
    }

    private sealed class FakeHandoff : IAudiobookPlaybackHandoff
    {
        public List<AudiobookPlaybackRequest> Requests { get; } = [];
        public AudiobookPlaybackSnapshot? Current { get; set; }
        public Action? AfterSnapshot { get; set; }
        public int Snapshots { get; private set; }

        public Task PlayAsync(AudiobookPlaybackRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }

        public ValueTask<AudiobookPlaybackSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            Snapshots++;
            AfterSnapshot?.Invoke();
            return ValueTask.FromResult(Current);
        }
    }
}
