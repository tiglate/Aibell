using Aibell.Domain.Media;
using Aibell.Domain.Playlists;

namespace Aibell.Application.Playlists;

public interface IPlaylistRepository
{
    Task<Playlist> CreateSmartAsync(string name, SmartPlaylistDefinition definition,
        CancellationToken cancellationToken = default);

    Task UpdateSmartDefinitionAsync(Guid playlistId, SmartPlaylistDefinition definition,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Playlist>> ListAsync(CancellationToken cancellationToken = default);

    Task<Playlist> CreateAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task<Playlist> CreateWithTrackAsync(
        string name,
        Guid trackId,
        CancellationToken cancellationToken = default);

    Task RenameAsync(
        Guid playlistId,
        string name,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid playlistId,
        CancellationToken cancellationToken = default);

    Task AddTrackAsync(
        Guid playlistId,
        Guid trackId,
        CancellationToken cancellationToken = default);

    Task RemoveTrackAsync(
        Guid playlistId,
        Guid trackId,
        CancellationToken cancellationToken = default);

    Task MoveTrackAsync(
        Guid playlistId,
        Guid trackId,
        int offset,
        CancellationToken cancellationToken = default);

    Task ReorderTracksAsync(
        Guid playlistId,
        IReadOnlyList<Guid> trackIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Track>> GetTracksAsync(
        Guid playlistId,
        string query,
        CancellationToken cancellationToken = default);
}
