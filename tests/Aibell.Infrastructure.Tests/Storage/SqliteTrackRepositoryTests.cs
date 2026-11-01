using Aibell.Application.Library;
using Aibell.Application.Playlists;
using Aibell.Domain.Media;
using Aibell.Domain.Playlists;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Storage;

[TestFixture]
public sealed class SqliteTrackRepositoryTests
{
    private string temporaryDirectory = null!;
    private string databasePath = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aibell-tests-{Guid.NewGuid():N}");
        databasePath = Path.Combine(temporaryDirectory, "library.db");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task DisposeAsync_InitializedRepository_ClosesIdleNativeHandlesAndRejectsReuse()
    {
        await using SqliteTrackRepository repository = new(databasePath);
        await repository.ListAsync();
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
        using SqliteConnection probe = new(connectionString);
        await probe.OpenAsync();
        SQLitePCL.sqlite3 handle = probe.Handle!;
        probe.Close();
        Assert.That(handle.IsClosed, Is.False);

        await repository.DisposeAsync();

        Assert.That(handle.IsClosed, Is.True);
        Assert.That(async () => await repository.ListAsync(), Throws.TypeOf<ObjectDisposedException>());
        File.Move(databasePath, databasePath + ".closed");
    }

    [TestCase(19)]
    [TestCase(-1)]
    public async Task InitializeAsync_UnsupportedSchema_RejectsWithoutChangingDatabase(int version)
    {
        Directory.CreateDirectory(temporaryDirectory);
        await using (SqliteConnection connection = new($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version={version}; CREATE TABLE future_data(value TEXT); INSERT INTO future_data VALUES('preserve');";
            await command.ExecuteNonQueryAsync();
        }
        byte[] originalHash = await DatabaseFileSnapshot.HashAsync(databasePath);
        await using SqliteTrackRepository repository = new(databasePath);

        Assert.That(async () => await repository.ListAsync(), Throws.TypeOf<NotSupportedException>());
        Assert.That(await DatabaseFileSnapshot.HashAsync(databasePath), Is.EqualTo(originalHash));
        await using SqliteConnection reopened = new($"Data Source={databasePath};Pooling=False");
        await reopened.OpenAsync();
        await using SqliteCommand check = reopened.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        Assert.That(await check.ExecuteScalarAsync(), Is.EqualTo((long)version));
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='tracks';";
        Assert.That(await check.ExecuteScalarAsync(), Is.EqualTo(0L));
        check.CommandText = "SELECT value FROM future_data;";
        Assert.That(await check.ExecuteScalarAsync(), Is.EqualTo("preserve"));
    }

    [Test]
    public async Task UpsertAsync_ReopenDatabase_PersistsUpdatedTrackOnce()
    {
        Uri location = new("file:///music/track.flac");
        Track original = CreateTrack(location, "Original");
        Track updated = CreateTrack(location, "Updated");

        await using (SqliteTrackRepository repository = new(databasePath))
        {
            await repository.UpsertAsync(original, "/music");
            await repository.UpsertAsync(updated, "/music");
        }

        await using SqliteTrackRepository reopened = new(databasePath);
        IReadOnlyList<Track> tracks = await reopened.ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(tracks, Has.Count.EqualTo(1));
            Assert.That(tracks[0].Title, Is.EqualTo("Updated"));
            Assert.That(tracks[0].Location, Is.EqualTo(location));
        });

        await using SqliteConnection connection = new($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo(18L));
    }

    [Test]
    public async Task InitializeAsync_SchemaThree_AddsGenreWithoutLosingTracks()
    {
        Directory.CreateDirectory(temporaryDirectory);
        Track existing = CreateTrack(new Uri("file:///music/existing.flac"), "Existing");
        await using (SqliteConnection connection = new($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tracks (
                    id TEXT PRIMARY KEY NOT NULL,
                    location TEXT NOT NULL UNIQUE,
                    title TEXT NOT NULL,
                    artist TEXT NOT NULL,
                    album TEXT NOT NULL,
                    duration_ticks INTEGER NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                INSERT INTO tracks
                    (id, location, title, artist, album, duration_ticks, updated_at_utc)
                VALUES ($id, $location, $title, $artist, $album, $duration, $updated);
                PRAGMA user_version = 3;
                """;
            command.Parameters.AddWithValue("$id", existing.Id.ToString("D"));
            command.Parameters.AddWithValue("$location", existing.Location.AbsoluteUri);
            command.Parameters.AddWithValue("$title", existing.Title);
            command.Parameters.AddWithValue("$artist", existing.Artist);
            command.Parameters.AddWithValue("$album", existing.Album);
            command.Parameters.AddWithValue("$duration", existing.Duration.Ticks);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await using SqliteTrackRepository repository = new(databasePath);
        IReadOnlyList<Track> tracks = await repository.ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(tracks.Single().Id, Is.EqualTo(existing.Id));
            Assert.That(tracks.Single().Genre, Is.Empty);
        });
    }

    [Test]
    public async Task SetRatingAsync_SubsequentMetadataUpsert_PreservesUserRating()
    {
        Uri location = new("file:///music/rated.flac");
        Track track = CreateTrack(location, "Rated");
        await using SqliteTrackRepository repository = new(databasePath);
        await repository.UpsertAsync(track, "/music");

        await repository.SetRatingAsync(track.Id, 5);
        await repository.UpsertAsync(track with { Title = "Updated", Rating = 1 }, "/music");
        Track actual = (await repository.ListAsync()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(actual.Title, Is.EqualTo("Updated"));
            Assert.That(actual.Rating, Is.EqualTo(5));
        });
    }

    [Test]
    public async Task UpsertAsync_ExtendedMetadata_PersistsValuesAndPreservesHistory()
    {
        Uri location = new("file:///music/detailed.flac");
        DateTimeOffset modified = new(2025, 6, 7, 8, 9, 10, TimeSpan.Zero);
        Track track = CreateTrack(location, "Detailed") with
        {
            BitRate = 1411,
            BeatsPerMinute = 123.5,
            Channels = 2,
            Comment = "A comment",
            Composer = "A composer",
            DateModified = modified,
            Description = "A description",
            DiscNumber = 2,
            EpisodeNumber = 4,
            Keywords = "ambient;test",
            SampleRate = 44100,
            SeasonNumber = 3,
            ShowName = "A show",
            FileSize = 123456
        };
        DateTimeOffset played = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        DateTimeOffset skipped = played.AddMinutes(1);

        await using SqliteTrackRepository repository = new(databasePath);
        await repository.UpsertAsync(track, "/music");
        await repository.RecordPlaybackStartedAsync(track.Id, played);
        await repository.RecordSkippedAsync(track.Id, skipped);
        await repository.UpsertAsync(track with { Comment = "Updated" }, "/music");

        Track actual = (await repository.ListAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(actual.BitRate, Is.EqualTo(1411));
            Assert.That(actual.BeatsPerMinute, Is.EqualTo(123.5));
            Assert.That(actual.Channels, Is.EqualTo(2));
            Assert.That(actual.Comment, Is.EqualTo("Updated"));
            Assert.That(actual.Composer, Is.EqualTo("A composer"));
            Assert.That(actual.DateAdded, Is.Not.Null);
            Assert.That(actual.DateModified, Is.EqualTo(modified));
            Assert.That(actual.Description, Is.EqualTo("A description"));
            Assert.That(actual.DiscNumber, Is.EqualTo(2));
            Assert.That(actual.EpisodeNumber, Is.EqualTo(4));
            Assert.That(actual.Keywords, Is.EqualTo("ambient;test"));
            Assert.That(actual.LastPlayed, Is.EqualTo(played));
            Assert.That(actual.LastSkipped, Is.EqualTo(skipped));
            Assert.That(actual.PlayCount, Is.EqualTo(1));
            Assert.That(actual.SampleRate, Is.EqualTo(44100));
            Assert.That(actual.SeasonNumber, Is.EqualTo(3));
            Assert.That(actual.ShowName, Is.EqualTo("A show"));
            Assert.That(actual.FileSize, Is.EqualTo(123456));
            Assert.That(actual.SkipCount, Is.EqualTo(1));
            Assert.That(actual.Source, Is.EqualTo(Path.GetFullPath("/music")));
        });
    }

    [Test]
    public async Task DeleteMissingAsync_RemovesOnlyTracksOwnedBySource()
    {
        string firstSource = Path.Combine(temporaryDirectory, "first");
        string secondSource = Path.Combine(temporaryDirectory, "second");
        Directory.CreateDirectory(firstSource);
        Directory.CreateDirectory(secondSource);
        Track missing = CreateTrack(new Uri(Path.Combine(firstSource, "missing.flac")), "Missing");
        Track retained = CreateTrack(new Uri(Path.Combine(secondSource, "retained.flac")), "Retained");

        await using SqliteTrackRepository repository = new(databasePath);
        await repository.UpsertAsync(missing, firstSource);
        await repository.UpsertAsync(retained, secondSource);

        int removed = await repository.DeleteMissingAsync(firstSource, []);
        IReadOnlyList<Track> tracks = await repository.ListAsync();
        IReadOnlyList<string> sources = await ((Aibell.Application.Library.ILibrarySourceRepository)repository)
            .ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(tracks, Has.Count.EqualTo(1));
            Assert.That(tracks[0].Title, Is.EqualTo("Retained"));
            Assert.That(sources, Is.EquivalentTo(new[] { firstSource, secondSource }));
        });
    }

    [Test]
    public async Task RemoveSourceAsync_RemovesOnlyTracksWithoutAnotherOwner()
    {
        string firstSource = Path.Combine(temporaryDirectory, "first");
        string secondSource = Path.Combine(temporaryDirectory, "second");
        Track exclusive = CreateTrack(new Uri("file:///music/exclusive.flac"), "Exclusive");
        Track shared = CreateTrack(new Uri("file:///music/shared.flac"), "Shared");
        await using SqliteTrackRepository repository = new(databasePath);
        await repository.UpsertAsync(exclusive, firstSource);
        await repository.UpsertAsync(shared, firstSource);
        await repository.UpsertAsync(shared, secondSource);

        int removed = await repository.RemoveAsync(firstSource);
        IReadOnlyList<Track> tracks = await repository.ListAsync();
        IReadOnlyList<string> sources = await ((ILibrarySourceRepository)repository).ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(tracks.Single().Id, Is.EqualTo(shared.Id));
            Assert.That(sources, Is.EqualTo(new[] { Path.GetFullPath(secondSource) }));
        });
    }

    [Test]
    public async Task SearchAsync_MatchesTitleArtistAndAlbumWithoutCaseSensitivity()
    {
        await using SqliteTrackRepository repository = new(databasePath);
        await repository.UpsertAsync(
            CreateTrack(new Uri("file:///music/one.flac"), "Californication"),
            "/music");
        Track otherArtist = CreateTrack(new Uri("file:///music/two.flac"), "Other") with
        {
            Artist = "Massive Attack",
            Album = "Mezzanine"
        };
        await repository.UpsertAsync(otherArtist, "/music");

        IReadOnlyList<Track> titleMatches = await repository.SearchAsync("CALIF");
        IReadOnlyList<Track> artistMatches = await repository.SearchAsync("massive");
        IReadOnlyList<Track> albumMatches = await repository.SearchAsync("zanine");

        Assert.Multiple(() =>
        {
            Assert.That(titleMatches.Single().Title, Is.EqualTo("Californication"));
            Assert.That(artistMatches.Single().Title, Is.EqualTo("Other"));
            Assert.That(albumMatches.Single().Title, Is.EqualTo("Other"));
        });
    }

    [Test]
    public async Task LibraryBrowser_GroupsFacetsAndScopesTrackQueries()
    {
        await using SqliteTrackRepository repository = new(databasePath);
        Track first = CreateTrack(new Uri("file:///music/one.flac"), "First") with
        {
            Artist = "Artist A",
            Album = "Shared",
            Genre = "Rock"
        };
        Track second = CreateTrack(new Uri("file:///music/two.flac"), "Second") with
        {
            Artist = "Artist A",
            Album = "Second Album",
            Genre = "Jazz"
        };
        Track third = CreateTrack(new Uri("file:///music/three.flac"), "Third") with
        {
            Artist = "Artist B",
            Album = "Shared",
            Genre = "Rock"
        };
        await repository.UpsertAsync(first, "/music");
        await repository.UpsertAsync(second, "/music");
        await repository.UpsertAsync(third, "/music");

        IReadOnlyList<GenreSummary> genres = await repository.ListGenresAsync();
        IReadOnlyList<ArtistSummary> artists = await repository.ListArtistsAsync("rock");
        IReadOnlyList<AlbumSummary> albums = await repository.ListAlbumsAsync("rock", null);
        IReadOnlyList<Track> artistTracks = await repository.GetTracksAsync(
            "jazz", "artist a", null, "second");
        IReadOnlyList<Track> albumTracks = await repository.GetTracksAsync(
            "ROCK", "ARTIST B", "SHARED", string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(genres, Has.Count.EqualTo(2));
            Assert.That(artists, Has.Count.EqualTo(2));
            Assert.That(artists.Single(item => item.Name == "Artist A").TrackCount, Is.EqualTo(1));
            Assert.That(albums, Has.Count.EqualTo(2));
            Assert.That(albums.Count(item => item.Title == "Shared"), Is.EqualTo(2));
            Assert.That(artistTracks.Single().Id, Is.EqualTo(second.Id));
            Assert.That(albumTracks.Single().Id, Is.EqualTo(third.Id));
        });
    }

    [Test]
    public async Task CreateWithTrackAsync_ReopenDatabase_PersistsPlaylistAndInitialTrack()
    {
        Track track = CreateTrack(new Uri("file:///music/first.flac"), "First");
        Playlist created;
        await using (SqliteTrackRepository repository = new(databasePath))
        {
            await repository.UpsertAsync(track, "/music");
            created = await repository.CreateWithTrackAsync("  Evening Jazz  ", track.Id);
        }

        await using SqliteTrackRepository reopened = new(databasePath);
        IReadOnlyList<Playlist> playlists = await ((IPlaylistRepository)reopened).ListAsync();
        IReadOnlyList<Track> tracks = await reopened.GetTracksAsync(created.Id, string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(created.Name, Is.EqualTo("Evening Jazz"));
            Assert.That(created.TrackIds, Is.EqualTo(new[] { track.Id }));
            Assert.That(playlists.Single().Id, Is.EqualTo(created.Id));
            Assert.That(playlists.Single().TrackIds, Is.EqualTo(new[] { track.Id }));
            Assert.That(tracks.Single().Id, Is.EqualTo(track.Id));
        });
    }

    [Test]
    public async Task CreateWithTrackAsync_MissingTrack_RollsBackPlaylistCreation()
    {
        await using SqliteTrackRepository repository = new(databasePath);

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await repository.CreateWithTrackAsync("Evening Jazz", Guid.NewGuid()));

        IReadOnlyList<Playlist> playlists = await ((IPlaylistRepository)repository).ListAsync();
        Assert.That(playlists, Is.Empty);
    }

    [Test]
    public async Task CreateWithTrackAsync_DuplicateName_DoesNotModifyExistingPlaylist()
    {
        Track track = CreateTrack(new Uri("file:///music/first.flac"), "First");
        await using SqliteTrackRepository repository = new(databasePath);
        await repository.UpsertAsync(track, "/music");
        Playlist existing = await repository.CreateAsync("Evening Jazz");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.CreateWithTrackAsync("  evening jazz  ", track.Id));

        IReadOnlyList<Playlist> playlists = await ((IPlaylistRepository)repository).ListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(playlists.Single().Id, Is.EqualTo(existing.Id));
            Assert.That(playlists.Single().TrackIds, Is.Empty);
        });
    }

    [Test]
    public async Task PlaylistMutations_PersistNamesMembershipAndOrdering()
    {
        Track first = CreateTrack(new Uri("file:///music/first.flac"), "First");
        Track second = CreateTrack(new Uri("file:///music/second.flac"), "Second");
        await using SqliteTrackRepository repository = new(databasePath);
        await repository.UpsertAsync(first, "/music");
        await repository.UpsertAsync(second, "/music");

        var playlist = await repository.CreateAsync("Road Trip");
        await repository.AddTrackAsync(playlist.Id, second.Id);
        await repository.AddTrackAsync(playlist.Id, first.Id);
        await repository.AddTrackAsync(playlist.Id, second.Id);

        IReadOnlyList<Track> tracks = await repository.GetTracksAsync(playlist.Id, string.Empty);
        IReadOnlyList<Track> filtered = await repository.GetTracksAsync(playlist.Id, "first");
        await repository.MoveTrackAsync(playlist.Id, first.Id, -1);
        IReadOnlyList<Track> reordered = await repository.GetTracksAsync(playlist.Id, string.Empty);
        IReadOnlyList<Guid> draggedOrder = [second.Id, first.Id];
        await repository.ReorderTracksAsync(playlist.Id, draggedOrder);
        IReadOnlyList<Track> dragReordered = await repository.GetTracksAsync(
            playlist.Id,
            string.Empty);
        await repository.RenameAsync(playlist.Id, "Open Road");
        IReadOnlyList<Playlist> renamed = await ((IPlaylistRepository)repository).ListAsync();
        await repository.RemoveTrackAsync(playlist.Id, second.Id);
        IReadOnlyList<Track> remaining = await repository.GetTracksAsync(playlist.Id, string.Empty);
        await repository.DeleteAsync(playlist.Id);
        IReadOnlyList<Playlist> deleted = await ((IPlaylistRepository)repository).ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(tracks, Has.Count.EqualTo(2));
            Assert.That(tracks[0].Id, Is.EqualTo(second.Id));
            Assert.That(tracks[1].Id, Is.EqualTo(first.Id));
            Assert.That(filtered.Single().Id, Is.EqualTo(first.Id));
            Assert.That(reordered[0].Id, Is.EqualTo(first.Id));
            Assert.That(reordered[1].Id, Is.EqualTo(second.Id));
            Assert.That(dragReordered[0].Id, Is.EqualTo(second.Id));
            Assert.That(dragReordered[1].Id, Is.EqualTo(first.Id));
            Assert.That(renamed.Single().Name, Is.EqualTo("Open Road"));
            Assert.That(remaining.Single().Id, Is.EqualTo(first.Id));
            Assert.That(deleted, Is.Empty);
        });
    }

    private static Track CreateTrack(Uri location, string title)
    {
        return new Track(
            TrackIdentity.FromLocation(location),
            location,
            title,
            "Artist",
            "Album",
            TimeSpan.FromMinutes(3));
    }
}
