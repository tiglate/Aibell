using Aibell.Application.Library;
using Aibell.Domain.Audiobooks;
using Aibell.Domain.Media;

namespace Aibell.Application.Audiobooks;

public enum AudiobookAddOutcome
{
    Added,
    AlreadyOnShelf
}

public sealed record AudiobookAddResult(AudiobookAddOutcome Outcome, AudiobookShelfEntry Entry);

public enum AudiobookSaveOutcome
{
    Saved,
    NothingPlaying,
    NotOnShelf
}

public sealed record AudiobookSaveResult(AudiobookSaveOutcome Outcome, AudiobookShelfEntry? Entry = null, TimeSpan Position = default);

/// <summary>
/// Local audiobook shelf. It reads metadata for the chosen file only and never admits it to the music library, never
/// writes to the file, and saves a position only when the caller explicitly asks.
/// </summary>
public sealed class AudiobookShelfService(IAudiobookShelfRepository repository, IMediaMetadataReader reader, IAudiobookFiles files,
    TimeProvider? timeProvider = null, IAudiobookChapterReader? chapterReader = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    /// <summary>True when a chapter reader is configured; without one the shelf still plays and resumes whole books.</summary>
    public bool SupportsChapters => chapterReader is not null;

    /// <summary>Freshly reads and validates the chapters of a shelf book. An empty catalog means the file has no usable chapters.</summary>
    public async Task<AudiobookChapterCatalog> ListChaptersAsync(AudiobookShelfEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);
        if (chapterReader is null) throw new InvalidOperationException("Chapter browsing is not available.");
        Uri location = files.Canonicalize(entry.Location);
        if (!AudiobookLocation.IsSupported(location)) throw new ArgumentException("The file resolves to a location that is not a supported local audio file.");
        if (!files.Exists(location))
            throw new FileNotFoundException("The audio file is missing or was moved. Remove it from the shelf and add it again from its new location: " + location.LocalPath);
        AudiobookChapterListing listing;
        try
        {
            // Keep accepted file reads owned until completion, even when an adapter ignores cancellation.
            listing = await chapterReader.ReadAsync(location, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not FileNotFoundException)
        {
            throw new InvalidOperationException("The chapters could not be read. The file may be damaged or still being copied: " + exception.Message, exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return AudiobookChapterRules.Normalize(listing);
    }

    /// <summary>
    /// Starts one chapter in the shared engine and returns its start. The chapter list is read again first, so a file that
    /// changed since it was listed cannot be sought with a stale offset: the chosen chapter must still exist with the same start.
    /// </summary>
    public async Task<TimeSpan> PlayChapterAsync(AudiobookShelfEntry entry, AudiobookChapter chapter, IAudiobookPlaybackHandoff handoff,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(chapter); ArgumentNullException.ThrowIfNull(handoff);
        AudiobookChapterCatalog fresh = await ListChaptersAsync(entry, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        AudiobookChapter? match = fresh.Chapters.FirstOrDefault(item => item.Start == chapter.Start && item.Title == chapter.Title);
        if (match is null)
            throw new InvalidOperationException("The chapters of this file changed. Show the chapters again and choose a chapter from the new list.");
        await handoff.PlayAsync(new AudiobookPlaybackRequest(entry.Location, entry.Title, entry.Author, match.Start, fresh.Duration), cancellationToken)
            .ConfigureAwait(false);
        return match.Start;
    }

    public Task<IReadOnlyList<AudiobookShelfEntry>> ListAsync(CancellationToken cancellationToken = default) => repository.ListShelfAsync(cancellationToken);

    public bool Exists(AudiobookShelfEntry entry) => files.Exists(entry.Location);

    public async Task<AudiobookAddResult> AddAsync(Uri file, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(file);
        if (!AudiobookLocation.IsSupported(file))
            throw new ArgumentException("Choose a local audio file (MP3, M4A/M4B, AAC, Ogg, Opus, FLAC or WAV).");
        Uri location = files.Canonicalize(file);
        if (!AudiobookLocation.IsSupported(location)) throw new ArgumentException("The file resolves to a location that is not a supported local audio file.");
        if (!files.Exists(location)) throw new FileNotFoundException("The audio file does not exist: " + location.LocalPath);
        MediaMetadata metadata = await reader.ReadAsync(location, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string title = string.IsNullOrWhiteSpace(metadata.Title) ? Path.GetFileNameWithoutExtension(location.LocalPath) : metadata.Title.Trim();
        string author = (string.IsNullOrWhiteSpace(metadata.Artist) ? metadata.AlbumArtist ?? "" : metadata.Artist).Trim();
        AudiobookShelfEntry entry = new(location, Truncate(title, AudiobookShelfEntry.MaximumTitleLength), Truncate(author, AudiobookShelfEntry.MaximumAuthorLength),
            ResumeRules.BoundDuration(metadata.Duration), TimeSpan.Zero, time.GetUtcNow());
        entry.Validate();
        if (await repository.AddToShelfAsync(entry, cancellationToken).ConfigureAwait(false)) return new(AudiobookAddOutcome.Added, entry);
        AudiobookShelfEntry existing = (await repository.ListShelfAsync(cancellationToken).ConfigureAwait(false))
            .First(item => item.Location == location);
        return new(AudiobookAddOutcome.AlreadyOnShelf, existing);
    }

    /// <summary>Starts the entry in the shared engine and returns the start position used. Resume clamps to the file's real current duration.</summary>
    public async Task<TimeSpan> PlayAsync(AudiobookShelfEntry entry, bool resume, IAudiobookPlaybackHandoff handoff,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(handoff);
        if (!files.Exists(entry.Location))
            throw new FileNotFoundException("The audio file is missing or was moved. Remove it from the shelf and add it again from its new location: " + entry.Location.LocalPath);
        MediaMetadata current;
        try { current = await reader.ReadAsync(entry.Location, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("The audio file could not be read. It may be damaged or still being copied: " + exception.Message, exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan duration = ResumeRules.BoundDuration(current.Duration);
        TimeSpan start = resume ? ResumeRules.ResumeStart(entry.SavedPosition, duration) : TimeSpan.Zero;
        await handoff.PlayAsync(new AudiobookPlaybackRequest(entry.Location, entry.Title, entry.Author, start, duration > TimeSpan.Zero ? duration : null),
            cancellationToken).ConfigureAwait(false);
        return start;
    }

    /// <summary>Explicitly saves the shared player's position, only when it is playing a file that is on the shelf.</summary>
    public async Task<AudiobookSaveResult> SaveCurrentAsync(IAudiobookPlaybackHandoff handoff, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(handoff);
        AudiobookPlaybackSnapshot? snapshot = await handoff.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is not { CanSavePosition: true } || !AudiobookLocation.IsSupported(snapshot.File)) return new(AudiobookSaveOutcome.NothingPlaying);
        Uri location = files.Canonicalize(snapshot.File);
        AudiobookShelfEntry? entry = (await repository.ListShelfAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.Location == location);
        cancellationToken.ThrowIfCancellationRequested();
        if (entry is null) return new(AudiobookSaveOutcome.NotOnShelf);
        // The real current duration bounds the saved position, so a missing or unreadable file fails here and the
        // previously saved position stays exactly as it was.
        if (!files.Exists(location))
            throw new FileNotFoundException("The audio file is missing or was moved, so the position was not saved: " + location.LocalPath);
        MediaMetadata current;
        try { current = await reader.ReadAsync(location, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("The audio file could not be read, so the position was not saved: " + exception.Message, exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan duration = ResumeRules.BoundDuration(current.Duration);
        TimeSpan position = ResumeRules.ForSave(snapshot.Position, duration);
        DateTimeOffset now = time.GetUtcNow();
        if (!await repository.SaveShelfPositionAsync(location, position, duration, now, cancellationToken).ConfigureAwait(false))
            return new(AudiobookSaveOutcome.NotOnShelf);
        return new(AudiobookSaveOutcome.Saved, entry with { SavedPosition = position, Duration = duration, PositionSaved = now }, position);
    }

    public Task<bool> RemoveAsync(AudiobookShelfEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return repository.RemoveFromShelfAsync(entry.Location, cancellationToken);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
