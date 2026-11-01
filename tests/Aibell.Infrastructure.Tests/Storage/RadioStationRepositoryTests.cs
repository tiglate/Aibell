using Aibell.Domain.Media;
using Aibell.Application.Radio;
using Aibell.Domain.Radio;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Storage;

[TestFixture]
public sealed class RadioStationRepositoryTests
{
    private static readonly string[] PlaylistTitles = ["Song", "Second"];
    private static readonly string[] StationNames = ["First", "Second"];
    private string directory = null!;
    private string database = null!;
    [SetUp]
    public void SetUp() { directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-{Guid.NewGuid():N}"); database = Path.Combine(directory, "library.db"); }
    [TearDown]
    public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Test]
    public async Task AddEditPlayAndReopen_PreservesStationHistoryAndKeepsMusicTablesSeparate()
    {
        RadioStation station = Station("Quote ' ü", "https://example.invalid/live?channel=1");
        DateTimeOffset played = new(2026, 10, 16, 3, 0, 0, TimeSpan.Zero);
        await using (SqliteTrackRepository repository = new(database))
        {
            await repository.AddStationAsync(station);
            await repository.RecordStationPlayedAsync(station.Id, played);
            await repository.UpdateStationAsync(station with { Name = "Edited", Genre = "Rock", Description = "User's station", Rating = 5, PlayCount = 999 });
            Assert.That(await repository.ListAsync(), Is.Empty);
            Assert.That(await ((Aibell.Application.Library.ILibrarySourceRepository)repository).ListAsync(), Is.Empty);
        }
        await using SqliteTrackRepository reopened = new(database);
        RadioStation actual = (await reopened.ListStationsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(actual.Name, Is.EqualTo("Edited")); Assert.That(actual.Genre, Is.EqualTo("Rock"));
            Assert.That(actual.Stream, Is.EqualTo(station.Stream)); Assert.That(actual.Rating, Is.EqualTo(5));
            Assert.That(actual.PlayCount, Is.EqualTo(1)); Assert.That(actual.LastPlayed, Is.EqualTo(played));
        });
        await reopened.RemoveStationAsync(station.Id);
        Assert.That(await reopened.ListStationsAsync(), Is.Empty);
    }

    [Test]
    public async Task Save_DuplicateUrlMissingIdAndInvalidInput_DoNotOverwriteExistingStation()
    {
        await using SqliteTrackRepository repository = new(database);
        RadioStation first = Station("First", "https://example.invalid/first"), second = Station("Second", "https://example.invalid/second");
        await repository.AddStationAsync(first); await repository.AddStationAsync(second);
        Assert.That(async () => await repository.AddStationAsync(first with { Id = Guid.NewGuid() }), Throws.TypeOf<InvalidOperationException>());
        Assert.That(async () => await repository.UpdateStationAsync(second with { Stream = first.Stream }), Throws.TypeOf<InvalidOperationException>());
        Assert.That(async () => await repository.UpdateStationAsync(second with { Id = Guid.NewGuid() }), Throws.TypeOf<InvalidOperationException>());
        Assert.That(async () => await repository.AddStationAsync(first with { Stream = new Uri("file:///tmp/private.mp3") }), Throws.TypeOf<ArgumentException>());
        Assert.That((await repository.ListStationsAsync()).Select(station => station.Name), Is.EqualTo(StationNames));
    }

    [Test]
    public async Task CatalogFields_RoundTripThroughAddUpdateAndReopen()
    {
        RadioStation station = Station("Catalog", "https://example.invalid/catalog") with
        {
            Country = "Germany",
            CountryCode = "DE",
            LogoUri = new Uri("https://example.invalid/logo.png"),
            CatalogId = "de-catalog"
        };
        await using (SqliteTrackRepository repository = new(database))
        {
            await repository.AddStationAsync(station);
            await repository.UpdateStationAsync(station with { Name = "Renamed" });
        }
        await using SqliteTrackRepository reopened = new(database);
        RadioStation actual = (await reopened.ListStationsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(actual.Name, Is.EqualTo("Renamed"));
            Assert.That(actual.Country, Is.EqualTo("Germany"));
            Assert.That(actual.CountryCode, Is.EqualTo("DE"));
            Assert.That(actual.LogoUri, Is.EqualTo(station.LogoUri));
            Assert.That(actual.CatalogId, Is.EqualTo("de-catalog"));
        });
    }

    [Test]
    public async Task ImportStationsAsync_SavingTheSameCatalogStationTwice_IsIdempotentAndKeepsHistory()
    {
        await using SqliteTrackRepository repository = new(database);
        RadioStation station = new Aibell.Application.Radio.CuratedStation("de-catalog", "Catalog", "DE", "Germany", "Rock", "Text",
            new Uri("https://example.invalid/catalog"), new Uri("https://example.invalid/logo.png")).ToRadioStation();
        Assert.That(await repository.ImportStationsAsync([station]), Is.EqualTo(new RadioStationImportResult(1, 0)));
        await repository.RecordStationPlayedAsync(station.Id, DateTimeOffset.UtcNow);
        Assert.That(await repository.ImportStationsAsync([station]), Is.EqualTo(new RadioStationImportResult(0, 1)));
        // The same catalog entry arriving with another address or identifier is still the same entry.
        Assert.That(await repository.ImportStationsAsync([station with { Id = Guid.NewGuid(), Stream = new Uri("https://example.invalid/other") }]),
            Is.EqualTo(new RadioStationImportResult(0, 1)));
        RadioStation saved = (await repository.ListStationsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(saved.CatalogId, Is.EqualTo("de-catalog"));
            Assert.That(saved.PlayCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Initialize_SchemaNine_AddsRadioWithoutChangingTrackMetadataHistoryOrPlaylistOrder()
    {
        Track track = new(Guid.NewGuid(), new Uri("file:///music/synthetic.flac"), "Song", "Artist", "Album", TimeSpan.FromSeconds(1));
        Guid playlist;
        await using (SqliteTrackRepository original = new(database))
        {
            await original.UpsertAsync(track, "/music"); await original.SetRatingAsync(track.Id, 4);
            Track second = track with { Id = Guid.NewGuid(), Location = new Uri("file:///music/second.flac"), Title = "Second" };
            await original.UpsertAsync(second, "/music");
            playlist = (await original.CreateAsync("Keep")).Id;
            await original.AddTrackAsync(playlist, track.Id); await original.AddTrackAsync(playlist, second.Id);
        }
        await ExecuteAsync("DROP TABLE podcast_playback_positions; DROP TRIGGER clear_podcast_position_after_download_change; DROP TABLE audiobook_shelf; DROP TABLE podcast_episodes; DROP TABLE podcast_subscriptions; DROP TABLE radio_stations; PRAGMA user_version=9;");
        await using SqliteTrackRepository upgraded = new(database);
        Assert.That(await upgraded.ListStationsAsync(), Is.Empty);
        Assert.That((await upgraded.ListAsync()).Single(item => item.Id == track.Id).Rating, Is.EqualTo(4));
        Assert.That((await upgraded.GetTracksAsync(playlist, "")).Select(item => item.Title), Is.EqualTo(PlaylistTitles));
        Assert.That(await ExecuteAsync("PRAGMA user_version;"), Is.EqualTo(18L));
    }

    [Test]
    public async Task Initialize_IncompatibleRadioTable_RollsBackAndCanRetry()
    {
        await using (SqliteTrackRepository original = new(database)) await original.ListAsync();
        await ExecuteAsync("DROP TABLE podcast_playback_positions; DROP TRIGGER clear_podcast_position_after_download_change; DROP TABLE audiobook_shelf; DROP TABLE podcast_episodes; DROP TABLE podcast_subscriptions; DROP TABLE radio_stations; CREATE TABLE radio_stations (unexpected TEXT); PRAGMA user_version=9;");
        byte[] before = await DatabaseFileSnapshot.HashAsync(database);
        await using SqliteTrackRepository repository = new(database);
        Assert.That(async () => await repository.ListStationsAsync(), Throws.TypeOf<SqliteException>());
        Assert.That(await DatabaseFileSnapshot.HashAsync(database), Is.EqualTo(before));
        Assert.That(await ExecuteAsync("PRAGMA user_version;"), Is.EqualTo(9L));
        await ExecuteAsync("DROP TABLE radio_stations;");
        Assert.That(await repository.ListStationsAsync(), Is.Empty);
    }

    [Test]
    public async Task Add_PreCancelled_DoesNotCreateAStation()
    {
        await using SqliteTrackRepository repository = new(database);
        await repository.ListStationsAsync();
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.That(async () => await repository.AddStationAsync(Station("Cancelled", "https://example.invalid/live"), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(await repository.ListStationsAsync(), Is.Empty);
    }

    [Test]
    public async Task ImportStationsAsync_DuplicatesPreserveHistoryAndNewStationsStartWithoutHistory()
    {
        await using SqliteTrackRepository repository = new(database);
        RadioStation existing = Station("Keep", "https://example.invalid/existing");
        await repository.AddStationAsync(existing);
        await repository.RecordStationPlayedAsync(existing.Id, DateTimeOffset.UtcNow);
        RadioStation added = Station("New", "https://example.invalid/new") with { PlayCount = 999, LastPlayed = DateTimeOffset.UtcNow };
        RadioStationImportResult result = await repository.ImportStationsAsync(
            [existing with { Id = Guid.NewGuid(), Name = "Overwrite" }, added, added with { Id = Guid.NewGuid() }]);
        IReadOnlyList<RadioStation> stations = await repository.ListStationsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(new RadioStationImportResult(1, 2)));
            Assert.That(stations.Single(station => station.Id == existing.Id).Name, Is.EqualTo("Keep"));
            Assert.That(stations.Single(station => station.Id == existing.Id).PlayCount, Is.EqualTo(1));
            Assert.That(stations.Single(station => station.Id == added.Id).PlayCount, Is.Zero);
            Assert.That(stations.Single(station => station.Id == added.Id).LastPlayed, Is.Null);
        });
    }

    [Test]
    public async Task ImportStationsAsync_LateConstraintFailure_RollsBackEarlierInsertionsAndAllowsRetry()
    {
        await using SqliteTrackRepository repository = new(database);
        RadioStation first = Station("First", "https://example.invalid/first");
        RadioStation second = first with { Name = "Second", Stream = new Uri("https://example.invalid/second") };
        await Assert.ThatAsync(async () => await repository.ImportStationsAsync([first, second]), Throws.TypeOf<SqliteException>());
        Assert.That(await repository.ListStationsAsync(), Is.Empty);
        Assert.That(await repository.ImportStationsAsync([first, second with { Id = Guid.NewGuid() }]),
            Is.EqualTo(new RadioStationImportResult(2, 0)));
    }

    [Test]
    public async Task ImportStationsAsync_InvalidLaterEntryOrCancelledRequest_WritesNothing()
    {
        await using SqliteTrackRepository repository = new(database);
        RadioStation first = Station("First", "https://example.invalid/first");
        await Assert.ThatAsync(async () => await repository.ImportStationsAsync([first, first with { Name = "" }]),
            Throws.TypeOf<ArgumentException>());
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThatAsync(async () => await repository.ImportStationsAsync([first], cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(await repository.ListStationsAsync(), Is.Empty);
    }

    private static RadioStation Station(string name, string address) => new(Guid.NewGuid(), name, new Uri(address));
    private async Task<object?> ExecuteAsync(string sql)
    {
        await using SqliteConnection connection = new($"Data Source={database};Pooling=False"); await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync();
    }
}
