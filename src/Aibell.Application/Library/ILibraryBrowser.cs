using Aibell.Domain.Media;

namespace Aibell.Application.Library;

public sealed record ArtistSummary(string Name, int TrackCount);

public sealed record AlbumSummary(string Artist, string Title, int TrackCount);

public sealed record GenreSummary(string Name, int TrackCount);

public interface ILibraryBrowser
{
    Task<IReadOnlyList<GenreSummary>> ListGenresAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtistSummary>> ListArtistsAsync(
        string? genre,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlbumSummary>> ListAlbumsAsync(
        string? genre,
        string? artist,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Track>> GetTracksAsync(
        string? genre,
        string? artist,
        string? album,
        string query,
        CancellationToken cancellationToken = default);
}
