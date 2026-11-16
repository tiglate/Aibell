using Aibell.Domain.Lyrics;
using Aibell.Application.Lyrics;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Storage;

[TestFixture]
public sealed class LyricsStoreTests
{
    private static readonly string[] HelloWorld = ["Hällo", "World"];
    private static readonly DateTimeOffset At = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private string directory = null!;
    private string database = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-lyrics-db-{Guid.NewGuid():N}");
        database = Path.Combine(directory, "library.db");
    }

    [TearDown]
    public void TearDown()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Test]
    public async Task FetchedLyricsRoundTripAndSurviveReopening()
    {
        Guid id = Guid.NewGuid();
        await using (SqliteTrackRepository repository = new(database))
        {
            SqliteTrackRepository store = repository;
            Assert.That(await store.GetAsync(id), Is.Null);
            Assert.That(await store.SaveFetchedAsync(id, TrackLyrics.FromText("[00:01.50]Hällo\n[00:03.00]World", null, false, LyricsOrigin.Provider, "lrclib")!, At), Is.True);
        }
        await using SqliteTrackRepository reopened = new(database);
        StoredLyrics? stored = await ((ILyricsStore)reopened).GetAsync(id);
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.StoredAt, Is.EqualTo(At));
        Assert.That(stored.Lyrics!.IsSynced, Is.True);
        Assert.That(stored.Lyrics.Source, Is.EqualTo("lrclib"));
        Assert.That(stored.Lyrics.Lines.Select(line => line.Text), Is.EqualTo(HelloWorld));
        Assert.That(stored.Lyrics.Lines[0].Start, Is.EqualTo(TimeSpan.FromSeconds(1.5)));
    }

    [Test]
    public async Task InstrumentalAndMissRowsRoundTrip()
    {
        await using SqliteTrackRepository repository = new(database);
        SqliteTrackRepository store = repository;
        Guid instrumental = Guid.NewGuid(), miss = Guid.NewGuid();
        await store.SaveFetchedAsync(instrumental, TrackLyrics.Instrumental(LyricsOrigin.Provider, "lrclib"), At);
        await store.SaveMissAsync(miss, At);
        Assert.That((await store.GetAsync(instrumental))!.Lyrics!.IsInstrumental, Is.True);
        StoredLyrics? missed = await store.GetAsync(miss);
        Assert.That(missed, Is.Not.Null);
        Assert.That(missed!.Lyrics, Is.Null);
    }

    [Test]
    public async Task AutomaticWritesNeverReplaceTheUsersEntryButFoundLyricsReplaceAMiss()
    {
        await using SqliteTrackRepository repository = new(database);
        SqliteTrackRepository store = repository;
        Guid id = Guid.NewGuid();
        await store.SaveMissAsync(id, At);
        Assert.That(await store.SaveFetchedAsync(id, TrackLyrics.FromText(null, "auto", false, LyricsOrigin.Provider)!, At), Is.True);
        Assert.That(await store.SaveMissAsync(id, At), Is.False, "a miss never replaces lyrics");
        await store.SaveUserAsync(id, TrackLyrics.FromText(null, "mine", false, LyricsOrigin.UserWritten)!, At);
        Assert.That(await store.SaveFetchedAsync(id, TrackLyrics.FromText(null, "auto again", false, LyricsOrigin.Provider)!, At), Is.False);
        Assert.That(await store.SaveMissAsync(id, At), Is.False);
        StoredLyrics stored = (await store.GetAsync(id))!;
        Assert.That(stored.Lyrics!.Lines[0].Text, Is.EqualTo("mine"));
        Assert.That(stored.Lyrics.Origin, Is.EqualTo(LyricsOrigin.UserWritten));
        await store.SaveUserAsync(id, TrackLyrics.FromText(null, "mine two", false, LyricsOrigin.UserSelected)!, At);
        Assert.That((await store.GetAsync(id))!.Lyrics!.Origin, Is.EqualTo(LyricsOrigin.UserSelected));
        await store.RemoveAsync(id);
        Assert.That(await store.GetAsync(id), Is.Null);
    }
}
