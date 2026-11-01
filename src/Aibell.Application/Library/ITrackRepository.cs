using Aibell.Domain.Media;

namespace Aibell.Application.Library;

public interface ITrackRepository
{
    Task<IReadOnlyList<Track>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Track>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        Track track,
        string sourceRoot,
        CancellationToken cancellationToken = default);

    Task SetRatingAsync(
        Guid trackId,
        int rating,
        CancellationToken cancellationToken = default);

    Task RecordPlaybackStartedAsync(
        Guid trackId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task RecordSkippedAsync(
        Guid trackId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<int> DeleteMissingAsync(
        string sourceRoot,
        IReadOnlyCollection<Uri> existingLocations,
        CancellationToken cancellationToken = default);
}
