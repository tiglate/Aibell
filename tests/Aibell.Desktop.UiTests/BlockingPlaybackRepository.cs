using Aibell.Application.Library;
using Aibell.Domain.Media;

namespace Aibell.Desktop.UiTests;

internal sealed class BlockingPlaybackRepository(ITrackRepository inner) : ITrackRepository
{
    public bool BlockStarts { get; set; }
    public bool IsBlocked { get; private set; }
    public bool BlockSkips { get; set; }
    public Exception? SkipFailure { get; set; }
    public int SkipAttempts { get; private set; }
    public bool IsSkipBlocked { get; private set; }
    public TaskCompletionSource ReleaseSkips { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task RecordPlaybackStartedAsync(Guid id, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        if (BlockStarts) { IsBlocked = true; await Release.Task.WaitAsync(cancellationToken); }
        await inner.RecordPlaybackStartedAsync(id, occurredAt, cancellationToken);
    }
    public Task<IReadOnlyList<Track>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
    public Task<IReadOnlyList<Track>> SearchAsync(string query, CancellationToken cancellationToken = default) => inner.SearchAsync(query, cancellationToken);
    public Task UpsertAsync(Track track, string root, CancellationToken cancellationToken = default) => inner.UpsertAsync(track, root, cancellationToken);
    public Task SetRatingAsync(Guid id, int rating, CancellationToken cancellationToken = default) => inner.SetRatingAsync(id, rating, cancellationToken);
    public async Task RecordSkippedAsync(Guid id, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        SkipAttempts++;
        if (BlockSkips) { IsSkipBlocked = true; await ReleaseSkips.Task.WaitAsync(cancellationToken); }
        if (SkipFailure is not null) throw SkipFailure;
        await inner.RecordSkippedAsync(id, occurredAt, cancellationToken);
    }
    public Task<int> DeleteMissingAsync(string root, IReadOnlyCollection<Uri> locations, CancellationToken cancellationToken = default) => inner.DeleteMissingAsync(root, locations, cancellationToken);
}

