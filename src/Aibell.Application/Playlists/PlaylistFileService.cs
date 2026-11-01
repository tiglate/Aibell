using Aibell.Application.Library;
using Aibell.Domain.Media;
using Aibell.Domain.Playlists;

namespace Aibell.Application.Playlists;

public sealed class PlaylistFileService(IPlaylistFileStore files, LibraryImportService importer,
    ITrackRepository library, IPlaylistImportDestination destination)
{
    public async Task<PlaylistImportPreview> PrepareAsync(Uri source, CancellationToken cancellationToken = default)
    {
        PlaylistFileContent content = await files.ReadAsync(source, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Track> existing = await library.ListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<Uri, Track> indexed = existing.GroupBy(track => track.Location).ToDictionary(group => group.Key, group => group.First());
        HashSet<Uri> seen = [];
        List<Track> tracks = [];
        List<PlaylistFileIssue> issues = [.. content.Issues];
        int duplicates = 0;
        foreach (PlaylistFileEntry entry in content.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri location = entry.Location;
            if (!seen.Add(location)) { duplicates++; continue; }
            try
            {
                Track scanned = await importer.ReadTrackAsync(location, cancellationToken).ConfigureAwait(false);
                Track track = indexed.GetValueOrDefault(location) ?? scanned;
                tracks.Add(track);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            { issues.Add(new(entry.Entry, location.LocalPath, exception.Message)); }
        }
        return new(source, content.Name, tracks, issues.OrderBy(issue => issue.Entry).ToArray(), duplicates);
    }

    public Task<Playlist> ImportAsync(string name, PlaylistImportPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Tracks.Count == 0) throw new ArgumentException("The playlist contains no readable local tracks.", nameof(preview));
        return destination.ImportPlaylistAsync(name, preview.Tracks, cancellationToken);
    }

    public Task ExportAsync(Uri destination, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default) =>
        files.WriteAsync(destination, tracks, cancellationToken);
}
