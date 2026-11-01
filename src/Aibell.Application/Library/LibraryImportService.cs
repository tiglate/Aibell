using Aibell.Domain.Media;

namespace Aibell.Application.Library;

public sealed class LibraryImportService(
    IMediaFileDiscovery discovery,
    IMediaMetadataReader metadataReader,
    ITrackRepository repository)
{
    public async Task<Track> ReadTrackAsync(Uri location, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsFile)
        {
            throw new ArgumentException("Only local music files can be queued.", nameof(location));
        }
        MediaMetadata metadata = await metadataReader.ReadAsync(location, cancellationToken).ConfigureAwait(false);
        return CreateTrack(location, metadata, Path.GetDirectoryName(location.LocalPath)!);
    }

    public async Task<Track> ImportTrackAsync(Uri location, CancellationToken cancellationToken = default)
    {
        Track track = await ReadTrackAsync(location, cancellationToken).ConfigureAwait(false);
        await repository.UpsertAsync(track, track.Source, cancellationToken).ConfigureAwait(false);
        return track;
    }

    public async Task<LibraryImportResult> ImportAsync(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        string[] normalizedRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedRoots.Length == 0)
        {
            throw new ArgumentException("At least one library root is required.", nameof(roots));
        }

        int discovered = 0;
        int imported = 0;
        int removed = 0;
        List<LibraryImportIssue> issues = [];

        foreach (string root in normalizedRoots)
        {
            HashSet<Uri> existingLocations = [];

            await foreach (Uri location in discovery
                .DiscoverAsync([root], cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                discovered++;
                existingLocations.Add(location);

                try
                {
                    MediaMetadata metadata = await metadataReader
                        .ReadAsync(location, cancellationToken)
                        .ConfigureAwait(false);
                    Track track = CreateTrack(location, metadata, root);

                    await repository.UpsertAsync(track, root, cancellationToken).ConfigureAwait(false);
                    imported++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    issues.Add(new LibraryImportIssue(location, exception.Message));
                }
            }

            removed += await repository
                .DeleteMissingAsync(root, existingLocations, cancellationToken)
                .ConfigureAwait(false);
        }

        return new LibraryImportResult(discovered, imported, removed, issues);
    }

    private static Track CreateTrack(Uri location, MediaMetadata metadata, string sourceRoot)
    {
        string fallbackTitle = location.IsFile
            ? Path.GetFileNameWithoutExtension(location.LocalPath)
            : Uri.UnescapeDataString(location.Segments.LastOrDefault() ?? location.Host);

        return new Track(
            TrackIdentity.FromLocation(location),
            location,
            string.IsNullOrWhiteSpace(metadata.Title) ? fallbackTitle : metadata.Title,
            metadata.Artist ?? string.Empty,
            metadata.Album ?? string.Empty,
            metadata.Duration,
            metadata.Genre ?? string.Empty,
            metadata.AlbumArtist ?? string.Empty,
            metadata.TrackNumber,
            metadata.Year,
            metadata.Rating,
            metadata.BitRate,
            metadata.BeatsPerMinute,
            metadata.Channels,
            metadata.Comment ?? string.Empty,
            metadata.Composer ?? string.Empty,
            DateAdded: null,
            metadata.DateModified,
            metadata.Description ?? string.Empty,
            metadata.DiscNumber,
            metadata.EpisodeNumber,
            metadata.Keywords ?? string.Empty,
            LastPlayed: null,
            LastSkipped: null,
            PlayCount: 0,
            metadata.SampleRate,
            metadata.SeasonNumber,
            metadata.ShowName ?? string.Empty,
            metadata.FileSize,
            SkipCount: 0,
            sourceRoot);
    }
}
