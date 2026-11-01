using System.Security.Cryptography;
using Aibell.Application.Playlists;
using Aibell.Domain.Media;
using Aibell.Domain.Playlists;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class TrackPropertiesTests
{
    private string directory = null!;
    private string database = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-properties-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory); database = Path.Combine(directory, "library.db");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task SaveMetadataAsync_ReopenAndRescan_PreservesEditsHistoryAndFileBytes()
    {
        string path = Path.Combine(directory, "fixture.flac");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.flac"), path);
        byte[] original = SHA256.HashData(await File.ReadAllBytesAsync(path));
        Track track = new(Guid.NewGuid(), new Uri(path), "Original", "Artist", "Album", TimeSpan.FromSeconds(1));
        TrackMetadata edit = TrackMetadata.FromTrack(track) with
        {
            Title = "Edited 日本語",
            Artist = "New artist",
            AlbumArtist = "Various artists",
            Genre = "Metal",
            Composer = "A composer",
            Conductor = "A conductor",
            Grouping = "A group",
            TrackNumber = 2,
            TrackTotal = 12,
            DiscNumber = 1,
            DiscTotal = 2,
            Rating = 5,
            BeatsPerMinute = 140.5,
            SortTitle = "Alternate title",
            SortArtist = "Alternate artist",
            Copyright = "© 2026",
            LicenseUri = "https://example.com/license"
        };
        await using (SqliteTrackRepository repository = new(database))
        {
            await repository.UpsertAsync(track, directory);
            await repository.RecordPlaybackStartedAsync(track.Id, DateTimeOffset.UtcNow);
            await repository.RecordSkippedAsync(track.Id, DateTimeOffset.UtcNow);
            await repository.SaveMetadataAsync(track.Id, edit);
            await repository.UpsertAsync(track with { Title = "Rescanned", FileSize = 42 }, directory);
            Assert.That(await repository.SearchAsync("日本語"), Has.Count.EqualTo(1));
            Assert.That((await repository.ListGenresAsync()).Single().Name, Is.EqualTo("Metal"));
            Playlist smart = await repository.CreateSmartAsync("Five stars", new(SmartPlaylistMatch.All,
                [new(SmartPlaylistField.Rating, SmartPlaylistOperator.Equals, "5")]));
            Assert.That(await repository.GetTracksAsync(smart.Id, ""), Has.Count.EqualTo(1));
        }
        await using SqliteTrackRepository reopened = new(database);
        Track saved = (await reopened.ListAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(saved.Title, Is.EqualTo(edit.Title));
            Assert.That(saved.Artist, Is.EqualTo(edit.Artist));
            Assert.That(saved.Metadata, Is.EqualTo(edit));
            Assert.That(saved.PlayCount, Is.EqualTo(1));
            Assert.That(saved.SkipCount, Is.EqualTo(1));
            Assert.That(saved.FileSize, Is.EqualTo(42));
        });
        await reopened.SetRatingAsync(track.Id, 3);
        Assert.That(TrackMetadata.FromTrack((await reopened.ListAsync()).Single()).Rating, Is.EqualTo(3));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(path)), Is.EqualTo(original));
    }

    [Test]
    public async Task SaveMetadataAsync_InvalidOrCancelled_DoesNotChangeLibrary()
    {
        Track track = new(Guid.NewGuid(), new Uri("file:///music/fixture.flac"), "Original", "Artist", "Album", TimeSpan.FromSeconds(1));
        await using SqliteTrackRepository repository = new(database);
        await repository.UpsertAsync(track, directory);
        Assert.That(async () => await repository.SaveMetadataAsync(track.Id, new(Rating: 6)), Throws.InstanceOf<ArgumentException>());
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.That(async () => await repository.SaveMetadataAsync(track.Id, new(Title: "Changed"), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Track saved = (await repository.ListAsync()).Single();
        Assert.That(saved.Title, Is.EqualTo("Original"));
        Assert.That(saved.Metadata, Is.Null);
        Assert.That(async () => await repository.SaveMetadataAsync(Guid.NewGuid(), new(Title: "Missing")), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task InitializeAsync_SchemaEight_AddsMetadataWithoutLosingLibrary()
    {
        Track track = new(Guid.NewGuid(), new Uri("file:///music/fixture.flac"), "Original", "Artist", "Album", TimeSpan.FromSeconds(1));
        await using (SqliteTrackRepository initial = new(database)) await initial.UpsertAsync(track, directory);
        await using (SqliteConnection connection = new($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE podcast_playback_positions; DROP TRIGGER clear_podcast_position_after_download_change; DROP TABLE audiobook_shelf; DROP TABLE podcast_episodes; DROP TABLE podcast_subscriptions; DROP TABLE radio_stations; ALTER TABLE tracks DROP COLUMN metadata_json; PRAGMA user_version=8;";
            await command.ExecuteNonQueryAsync();
        }
        await using SqliteTrackRepository upgraded = new(database);
        Assert.That((await upgraded.ListAsync()).Single().Id, Is.EqualTo(track.Id));
        await upgraded.SaveMetadataAsync(track.Id, TrackMetadata.FromTrack(track) with { Title = "Upgraded" });
        Assert.That((await upgraded.ListAsync()).Single().Title, Is.EqualTo("Upgraded"));
    }

    [TestCase("flac")]
    [TestCase("mp3")]
    [TestCase("m4a")]
    [TestCase("ogg")]
    [TestCase("opus")]
    public async Task ReadAsync_PodcastFields_AgreeWithImportedMetadata(string extension)
    {
        string path = Path.Combine(directory, "podcast." + extension);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), path);
        ATL.Track seed = new(path);
        seed.AdditionalFields["SHOW"] = "A show 日本語";
        seed.AdditionalFields["EPISODENUMBER"] = "7";
        seed.AdditionalFields["SEASONNUMBER"] = "2";
        seed.AdditionalFields["KEYWORDS"] = "music, ação";
        seed.AdditionalFields["LICENSE"] = "https://example.com/license";
        Assert.That(seed.Save(), Is.True);
        byte[] original = SHA256.HashData(await File.ReadAllBytesAsync(path));

        var imported = await new AtlMediaMetadataReader().ReadAsync(new Uri(path));
        var details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(path));
        Assert.Multiple(() =>
        {
            Assert.That(imported.ShowName, Is.EqualTo("A show 日本語"));
            Assert.That(imported.EpisodeNumber, Is.EqualTo(7));
            Assert.That(imported.SeasonNumber, Is.EqualTo(2));
            Assert.That(imported.Keywords, Is.EqualTo("music, ação"));
            Assert.That(details.Metadata.ShowName, Is.EqualTo(imported.ShowName));
            Assert.That(details.Metadata.EpisodeNumber, Is.EqualTo(imported.EpisodeNumber));
            Assert.That(details.Metadata.SeasonNumber, Is.EqualTo(imported.SeasonNumber));
            Assert.That(details.Metadata.Keywords, Is.EqualTo(imported.Keywords));
            Assert.That(details.Metadata.LicenseUri, Is.EqualTo("https://example.com/license"));
            Assert.That(details.SourceHash, Is.EqualTo(Convert.ToHexString(original)));
        });
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(path)), Is.EqualTo(original));
    }

    [TestCase("flac")]
    [TestCase("mp3")]
    [TestCase("m4a")]
    [TestCase("ogg")]
    [TestCase("opus")]
    [TestCase("wav")]
    public async Task ReadAsync_FileDetails_ReadsTechnicalTagsWithoutModifyingMedia(string extension)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension);
        byte[] original = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(path));
        Assert.That(details.Codec, Is.Not.Empty);
        Assert.That(details.Metadata.Title, Is.EqualTo("Fixture Tone"));
        Assert.That(details.TagFormats, Is.Not.Empty);
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(path)), Is.EqualTo(original));
    }

    [TestCase("mp3")]
    [TestCase("m4a")]
    public async Task ReadAsync_MovementTags_AreNotMisidentifiedAsPodcastFields(string extension)
    {
        string path = Path.Combine(directory, "series." + extension);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), path);
        ATL.Track seed = new(path) { SeriesTitle = "Native show ação", SeriesPart = "12" };
        Assert.That(seed.Save(), Is.True);
        var imported = await new AtlMediaMetadataReader().ReadAsync(new Uri(path));
        var details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(path));
        Assert.Multiple(() =>
        {
            Assert.That(imported.ShowName, Is.Null);
            Assert.That(imported.EpisodeNumber, Is.Zero);
            Assert.That(details.Metadata.ShowName, Is.Empty);
            Assert.That(details.Metadata.EpisodeNumber, Is.Zero);
            Assert.That(details.AdditionalFields["Movement name"], Is.EqualTo("Native show ação"));
            Assert.That(details.AdditionalFields["Movement number"], Is.EqualTo("12"));
        });
    }

    [Test]
    public void ReadAsync_Cancelled_DoesNotInspectUnavailableFile()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.That(async () => await new AtlTrackFileDetailsReader().ReadAsync(
            new Uri(Path.Combine(directory, "missing.flac")), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }
}
