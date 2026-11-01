using Aibell.Domain.Audiobooks;

namespace Aibell.Application.Audiobooks;

/// <summary>Reads chapter markers and the real duration of one canonical local audiobook file. It must never write the file.</summary>
public interface IAudiobookChapterReader
{
    // Throws FileNotFoundException, InvalidDataException (unreadable or no audio) or OperationCanceledException.
    Task<AudiobookChapterListing> ReadAsync(Uri file, CancellationToken cancellationToken = default);
}
