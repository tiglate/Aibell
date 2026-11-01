using Aibell.Domain.Audiobooks;

namespace Aibell.Application.Audiobooks;

public interface IAudiobookShelfRepository
{
    Task<IReadOnlyList<AudiobookShelfEntry>> ListShelfAsync(CancellationToken cancellationToken = default);

    // Returns false, changing nothing, when the canonical location is already on the shelf.
    Task<bool> AddToShelfAsync(AudiobookShelfEntry entry, CancellationToken cancellationToken = default);

    // Stores an explicitly saved position and the duration observed when saving. False when the entry is gone.
    Task<bool> SaveShelfPositionAsync(Uri location, TimeSpan position, TimeSpan duration, DateTimeOffset at, CancellationToken cancellationToken = default);

    // Removes the shelf record only; it never touches the audio file.
    Task<bool> RemoveFromShelfAsync(Uri location, CancellationToken cancellationToken = default);
}

/// <summary>File-system facts the service needs; the adapter lives in Infrastructure so Application does no I/O.</summary>
public interface IAudiobookFiles
{
    // Resolves the location to its canonical form: full path with the file and every ancestor link followed. Path casing is preserved.
    Uri Canonicalize(Uri location);
    bool Exists(Uri location);
}

public interface IAudiobookPlaybackHandoff
{
    // Serializes on the UI thread, loads the file into the one shared engine and seeks to StartPosition BEFORE starting playback.
    Task PlayAsync(AudiobookPlaybackRequest request, CancellationToken cancellationToken = default);

    // The shared engine's current state, or null when nothing is loaded. CanSavePosition is true while Playing or Paused.
    // Read only for an explicit Save position.
    ValueTask<AudiobookPlaybackSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default);
}

public sealed record AudiobookPlaybackRequest(Uri File, string Title, string Author, TimeSpan StartPosition, TimeSpan? Duration);

public sealed record AudiobookPlaybackSnapshot(Uri File, TimeSpan Position, bool CanSavePosition);
