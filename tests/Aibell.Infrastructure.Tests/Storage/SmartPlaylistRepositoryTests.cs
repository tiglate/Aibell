using Aibell.Application.Playlists;
using Aibell.Domain.Media;
using Aibell.Domain.Playlists;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Storage;

[TestFixture]
public sealed class SmartPlaylistRepositoryTests
{
    private string directory = null!;
    private string path = null!;
    private SqliteTrackRepository repository = null!;
    private IPlaylistRepository playlists = null!;
    private Track first = null!;
    private Track second = null!;
    private Track third = null!;

    [SetUp]
    public async Task SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-smart-{Guid.NewGuid():N}");
        path = Path.Combine(directory, "library.db");
        repository = new SqliteTrackRepository(path);
        playlists = repository;
        first = new Track(Guid.NewGuid(), new Uri("file:///music/first.flac"), "First", "Artist", "Album", TimeSpan.FromMinutes(3), Genre: "Rock", Year: 2026, Rating: 4, PlayCount: 3);
        second = first with { Id = Guid.NewGuid(), Location = new Uri("file:///music/second.flac"), Title = "Second", Rating = 2, Year = 2020, Genre = "Jazz", PlayCount = 0 };
        third = first with { Id = Guid.NewGuid(), Location = new Uri("file:///music/third.flac"), Title = "Third", Genre = "", Rating = 5, Year = 1999, PlayCount = 0, SkipCount = 2 };
        await repository.UpsertAsync(first, "/music");
        await repository.UpsertAsync(second, "/music");
        await repository.UpsertAsync(third, "/music");
    }

    [TearDown]
    public async Task TearDown()
    {
        await repository.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    [TestCase(SmartPlaylistMatch.All, 1)]
    [TestCase(SmartPlaylistMatch.Any, 2)]
    public async Task GetTracksAsync_AllAndAny_ComposesRulesAndScopesSearch(SmartPlaylistMatch match, int count)
    {
        Playlist playlist = await playlists.CreateSmartAsync("Favorites", new(match,
            [new(SmartPlaylistField.Rating, SmartPlaylistOperator.GreaterOrEqual, "4"),
                new(SmartPlaylistField.Genre, SmartPlaylistOperator.Equals, "rOcK")]));

        IReadOnlyList<Track> tracks = await playlists.GetTracksAsync(playlist.Id, "");

        Assert.That(tracks, Has.Count.EqualTo(count));
        Assert.That(tracks.Any(track => track.Id == first.Id), Is.True);
        Assert.That(await playlists.GetTracksAsync(playlist.Id, "SECOND"), Is.Empty);
        Assert.That((await playlists.GetTracksAsync(playlist.Id, "first")).Single().Id, Is.EqualTo(first.Id));
    }

    [Test]
    public async Task ListAsync_ReopenedDatabase_PreservesRulesAndReflectsChangedRatingsAndImports()
    {
        Playlist playlist = await playlists.CreateSmartAsync("Favorites", Favorites());
        await repository.SetRatingAsync(second.Id, 5);
        Track added = first with { Id = Guid.NewGuid(), Location = new Uri("file:///music/added.flac"), Title = "Added" };
        await repository.UpsertAsync(added, "/music");
        await using SqliteTrackRepository reopened = new(path);
        IPlaylistRepository reopenedPlaylists = reopened;

        Playlist persisted = (await reopenedPlaylists.ListAsync()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(persisted.Id, Is.EqualTo(playlist.Id));
            Assert.That(persisted.IsSmart, Is.True);
            Assert.That(persisted.Definition!.Match, Is.EqualTo(SmartPlaylistMatch.All));
            Assert.That(persisted.Definition.Rules, Is.EqualTo(Favorites().Rules));
            Assert.That(persisted.TrackIds, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public async Task GetTracksAsync_TextContains_TreatsSqlAndWildcardCharactersLiterally()
    {
        string injection = "' OR 1=1 -- %_";
        Track literal = first with { Id = Guid.NewGuid(), Location = new Uri("file:///music/literal.flac"), Title = "literal " + injection };
        await repository.UpsertAsync(literal, "/music");
        Playlist playlist = await playlists.CreateSmartAsync("Literal", new(SmartPlaylistMatch.All,
            [new(SmartPlaylistField.Title, SmartPlaylistOperator.Contains, injection)]));

        Assert.That((await playlists.GetTracksAsync(playlist.Id, "")).Single().Id, Is.EqualTo(literal.Id));
        Assert.That(await repository.ListAsync(), Has.Count.EqualTo(4));
    }

    [Test]
    public async Task GetTracksAsync_EmptyGenreEquality_MatchesUnknownTags()
    {
        Playlist playlist = await playlists.CreateSmartAsync("Unknown genre", new(SmartPlaylistMatch.All,
            [new(SmartPlaylistField.Genre, SmartPlaylistOperator.Equals, "")]));

        Assert.That((await playlists.GetTracksAsync(playlist.Id, "")).Single().Id, Is.EqualTo(third.Id));
    }

    [Test]
    public async Task GetTracksAsync_PlayAndSkipStatistics_UpdatesWithoutMembershipWrites()
    {
        await repository.RecordPlaybackStartedAsync(first.Id, DateTimeOffset.UtcNow);
        await repository.RecordSkippedAsync(third.Id, DateTimeOffset.UtcNow);
        Playlist playlist = await playlists.CreateSmartAsync("Unplayed", new(SmartPlaylistMatch.All,
            [new(SmartPlaylistField.PlayCount, SmartPlaylistOperator.Equals, "0"),
                new(SmartPlaylistField.SkipCount, SmartPlaylistOperator.LessOrEqual, "0")]));
        Assert.That((await playlists.GetTracksAsync(playlist.Id, "")).Single().Id, Is.EqualTo(second.Id));
        await repository.RecordPlaybackStartedAsync(second.Id, DateTimeOffset.UtcNow);

        Assert.That(await playlists.GetTracksAsync(playlist.Id, ""), Is.Empty);
    }

    [Test]
    public async Task UpdateSmartDefinitionAsync_ValidAndInvalidRules_PreservesLastValidDefinition()
    {
        Playlist playlist = await playlists.CreateSmartAsync("Favorites", Favorites());
        SmartPlaylistDefinition rock = new(SmartPlaylistMatch.All, [new(SmartPlaylistField.Genre, SmartPlaylistOperator.Equals, "Rock")]);
        await playlists.UpdateSmartDefinitionAsync(playlist.Id, rock);
        Assert.That((await playlists.GetTracksAsync(playlist.Id, "")).Single().Id, Is.EqualTo(first.Id));
        Assert.That(async () => await playlists.UpdateSmartDefinitionAsync(playlist.Id,
            new(SmartPlaylistMatch.All, [new(SmartPlaylistField.Rating, SmartPlaylistOperator.Equals, "99")])), Throws.ArgumentException);
        Assert.That((await playlists.GetTracksAsync(playlist.Id, "")).Single().Id, Is.EqualTo(first.Id));
    }

    [Test]
    public async Task MembershipCommands_SmartPlaylist_RejectManualChanges()
    {
        Playlist playlist = await playlists.CreateSmartAsync("Favorites", Favorites());

        Assert.That(async () => await playlists.AddTrackAsync(playlist.Id, second.Id), Throws.TypeOf<InvalidOperationException>());
        Assert.That(async () => await playlists.RemoveTrackAsync(playlist.Id, first.Id), Throws.TypeOf<InvalidOperationException>());
        Assert.That(async () => await playlists.MoveTrackAsync(playlist.Id, first.Id, 1), Throws.TypeOf<InvalidOperationException>());
        Assert.That(async () => await playlists.ReorderTracksAsync(playlist.Id, [third.Id, first.Id]), Throws.TypeOf<InvalidOperationException>());
        Assert.That(await playlists.GetTracksAsync(playlist.Id, ""), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task CreateRenameDeleteAsync_SmartPlaylist_SharesNamesAndCascadesOnlyRules()
    {
        Playlist playlist = await playlists.CreateSmartAsync("Favorites", Favorites());
        Assert.That(async () => await playlists.CreateAsync(" favorites "), Throws.TypeOf<InvalidOperationException>());
        await playlists.RenameAsync(playlist.Id, "Renamed");
        Assert.That((await playlists.ListAsync()).Single().Name, Is.EqualTo("Renamed"));
        await playlists.DeleteAsync(playlist.Id);

        Assert.That(await playlists.ListAsync(), Is.Empty);
        Assert.That(await repository.ListAsync(), Has.Count.EqualTo(3));
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM smart_playlist_rules;";
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo(0L));
    }

    [Test]
    public async Task GetTracksAsync_CorruptDefinition_KeepsLibraryUsableAndAllowsRepair()
    {
        Playlist playlist = await playlists.CreateSmartAsync("Favorites", Favorites());
        await using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE smart_playlist_rules SET definition_json='{invalid';";
            await command.ExecuteNonQueryAsync();
        }
        Playlist unreadable = (await playlists.ListAsync()).Single();
        Assert.That(unreadable.IsSmart, Is.True);
        Assert.That(unreadable.Definition!.Rules, Is.Empty);
        Assert.That(await playlists.GetTracksAsync(playlist.Id, ""), Is.Empty);
        Assert.That(await repository.ListAsync(), Has.Count.EqualTo(3));
        await playlists.UpdateSmartDefinitionAsync(playlist.Id, Favorites());
        Assert.That(await playlists.GetTracksAsync(playlist.Id, ""), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task UpdateSmartDefinitionAsync_ManualPlaylist_RejectsConversionWithoutMutation()
    {
        Playlist manual = await playlists.CreateWithTrackAsync("Manual", first.Id);
        Assert.That(async () => await playlists.UpdateSmartDefinitionAsync(manual.Id, Favorites()), Throws.TypeOf<InvalidOperationException>());
        Assert.That((await playlists.GetTracksAsync(manual.Id, "")).Single().Id, Is.EqualTo(first.Id));
        Assert.That((await playlists.ListAsync()).Single().IsSmart, Is.False);
    }

    [Test]
    public async Task CreateSmartAsync_FailedRuleInsert_RollsBackPlaylistDefinition()
    {
        await using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_smart_insert BEFORE INSERT ON smart_playlist_rules
                BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        Assert.That(async () => await playlists.CreateSmartAsync("Rollback", Favorites()), Throws.TypeOf<SqliteException>());
        Assert.That(await playlists.ListAsync(), Is.Empty);
        Assert.That(await repository.ListAsync(), Has.Count.EqualTo(3));
    }

    [Test]
    public async Task CreateSmartAsync_CancelledRequest_DoesNotCreatePlaylist()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.That(async () => await playlists.CreateSmartAsync("Cancelled", Favorites(), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(await playlists.ListAsync(), Is.Empty);
    }

    [Test]
    public async Task InitializeAsync_SchemaSix_PreservesManualPlaylistAndAddsSmartStorage()
    {
        Playlist manual = await playlists.CreateWithTrackAsync("Manual", first.Id);
        await using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE podcast_playback_positions; DROP TRIGGER clear_podcast_position_after_download_change; DROP TABLE audiobook_shelf; DROP TABLE podcast_episodes; DROP TABLE podcast_subscriptions; DROP TABLE radio_stations; DROP TABLE smart_playlist_rules; ALTER TABLE tracks DROP COLUMN metadata_json; PRAGMA user_version=6;";
            await command.ExecuteNonQueryAsync();
        }
        await using SqliteTrackRepository migrated = new(path);
        Assert.That((await migrated.GetTracksAsync(manual.Id, "")).Single().Id, Is.EqualTo(first.Id));
        Playlist smart = await migrated.CreateSmartAsync("Favorites", Favorites());
        Assert.That(await migrated.GetTracksAsync(smart.Id, ""), Has.Count.EqualTo(2));
        Assert.That(await migrated.ListAsync(), Has.Count.EqualTo(3));
    }

    private static SmartPlaylistDefinition Favorites() => new(SmartPlaylistMatch.All,
        [new(SmartPlaylistField.Rating, SmartPlaylistOperator.GreaterOrEqual, "4")]);
}
