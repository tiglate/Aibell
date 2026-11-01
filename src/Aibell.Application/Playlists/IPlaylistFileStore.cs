using Aibell.Domain.Media;
using Aibell.Domain.Playlists;

namespace Aibell.Application.Playlists;

public sealed record PlaylistFileIssue(int Entry, string Location, string Message);
public sealed record PlaylistFileEntry(int Entry, Uri Location);
public sealed record PlaylistFileContent(string Name, IReadOnlyList<PlaylistFileEntry> Entries, IReadOnlyList<PlaylistFileIssue> Issues);
public sealed record PlaylistImportPreview(Uri Source, string Name, IReadOnlyList<Track> Tracks,
    IReadOnlyList<PlaylistFileIssue> Issues, int Duplicates);

public interface IPlaylistFileStore
{
    Task<PlaylistFileContent> ReadAsync(Uri source, CancellationToken cancellationToken = default);
    Task WriteAsync(Uri destination, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default);
}

public interface IPlaylistImportDestination
{
    // Atomically create the playlist and any missing library tracks. Existing catalog
    // metadata/history are authoritative; importing never registers watched folders.
    Task<Playlist> ImportPlaylistAsync(string name, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default);
}
