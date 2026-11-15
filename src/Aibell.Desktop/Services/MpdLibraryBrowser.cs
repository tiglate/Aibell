using Aibell.Application.Library;
using Aibell.Domain.Media;
using Aibell.Mpd.Client;

namespace Aibell.Desktop.Services;

/// <summary>
/// The server's library in the shape of Aibell's own: genres, artists and albums with their counts, and the tracks of a selection, so
/// the Music view can show it unchanged. The whole library is fetched once (MPD answers for thousands of songs in well under a second)
/// and the lists are worked out here, until <see cref="Invalidate"/> says the server's database changed.
/// </summary>
public sealed class MpdLibraryBrowser(IMpdLibrarySource source) : ILibraryBrowser
{
    private IReadOnlyList<MpdSong>? songs;

    /// <summary>Forgets what was fetched; the next question asks the server again.</summary>
    public void Invalidate() => songs = null;

    private async Task<IReadOnlyList<MpdSong>> SongsAsync(CancellationToken cancellationToken)
    {
        if (!source.IsConnected) return [];
        return songs ??= await source.GetAllSongsAsync(cancellationToken);
    }

    private static string GenreOf(MpdSong song) => song.Tag("Genre") ?? string.Empty;

    /// <summary>The artist a song is listed under: its album artist when it has one, as album browsers do, else its artist.</summary>
    private static string ArtistOf(MpdSong song) => song.AlbumArtist ?? song.Artist ?? string.Empty;

    private static string AlbumOf(MpdSong song) => song.Album ?? string.Empty;

    public async Task<IReadOnlyList<GenreSummary>> ListGenresAsync(CancellationToken cancellationToken = default) =>
        [.. (await SongsAsync(cancellationToken)).GroupBy(GenreOf, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GenreSummary(group.First() is { } first ? GenreOf(first) : string.Empty, group.Count()))
            .OrderBy(item => item.Name.Length == 0 ? 0 : 1).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)];

    public async Task<IReadOnlyList<ArtistSummary>> ListArtistsAsync(string? genre, CancellationToken cancellationToken = default) =>
        [.. (await SongsAsync(cancellationToken)).Where(song => MatchesGenre(song, genre)).GroupBy(ArtistOf, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ArtistSummary(ArtistOf(group.First()), group.Count()))
            .OrderBy(item => item.Name.Length == 0 ? 0 : 1).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)];

    public async Task<IReadOnlyList<AlbumSummary>> ListAlbumsAsync(string? genre, string? artist, CancellationToken cancellationToken = default) =>
        [.. (await SongsAsync(cancellationToken)).Where(song => MatchesGenre(song, genre) && MatchesArtist(song, artist))
            .GroupBy(song => (Artist: ArtistOf(song).ToUpperInvariant(), Album: AlbumOf(song).ToUpperInvariant()))
            .Select(group => new AlbumSummary(ArtistOf(group.First()), AlbumOf(group.First()), group.Count()))
            .OrderBy(item => item.Title.Length == 0 ? 0 : 1).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Artist, StringComparer.CurrentCultureIgnoreCase)];

    public async Task<IReadOnlyList<Track>> GetTracksAsync(string? genre, string? artist, string? album, string query, CancellationToken cancellationToken = default)
    {
        IEnumerable<MpdSong> selection = (await SongsAsync(cancellationToken)).Where(song => MatchesGenre(song, genre) && MatchesArtist(song, artist) &&
            (album is null || string.Equals(AlbumOf(song), album, StringComparison.OrdinalIgnoreCase)));
        return Order(Filter(selection, query));
    }

    /// <summary>The songs of a stored playlist in their stored order, narrowed by the search text.</summary>
    public async Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(string name, string query, CancellationToken cancellationToken = default)
    {
        if (!source.IsConnected) return [];
        return [.. Filter(await source.GetPlaylistSongsAsync(name, cancellationToken), query).Select(MpdTracks.FromSong)];
    }

    private static bool MatchesGenre(MpdSong song, string? genre) => genre is null || string.Equals(GenreOf(song), genre, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesArtist(MpdSong song, string? artist) => artist is null || string.Equals(ArtistOf(song), artist, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<MpdSong> Filter(IEnumerable<MpdSong> selection, string query)
    {
        string text = query.Trim();
        if (text.Length == 0) return selection;
        return selection.Where(song => Contains(song.DisplayTitle, text) || Contains(song.Artist, text) || Contains(song.AlbumArtist, text) ||
            Contains(song.Album, text) || Contains(song.Tag("Genre"), text) || Contains(song.Uri, text));
    }

    private static bool Contains(string? value, string text) => value?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true;

    private static List<Track> Order(IEnumerable<MpdSong> selection) =>
        [.. selection.Select(MpdTracks.FromSong)
            .OrderBy(track => track.AlbumArtist.Length > 0 ? track.AlbumArtist : track.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(track => track.Album, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(track => track.DiscNumber)
            .ThenBy(track => track.TrackNumber)
            .ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)];
}
