using Aibell.Application.Audiobooks;
using Aibell.Domain.Audiobooks;
using Aibell.Infrastructure.Metadata;

namespace Aibell.Infrastructure.Audiobooks;

/// <summary>
/// Read-only chapter adapter over ATL (ID3 CHAP frames, MP4 chapter atoms and other formats ATL exposes). The file is opened
/// only for reading by ATL's Track constructor and is never saved. Markers are returned raw; the domain rules validate them.
/// Cancellation is honoured before the read and again before the result is returned.
/// </summary>
public sealed class AtlAudiobookChapterReader : IAudiobookChapterReader
{
    static AtlAudiobookChapterReader()
    {
        ATL.Settings.OutputStacktracesToConsole = false;
    }

    public Task<AudiobookChapterListing> ReadAsync(Uri file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!AudiobookLocation.IsSupported(file))
            throw new ArgumentException("Choose a local audio file (MP3, M4A/M4B, AAC, Ogg, Opus, FLAC or WAV).", nameof(file));
        string path = file.LocalPath;
        if (!File.Exists(path)) throw new FileNotFoundException("The audio file does not exist.", path);
        return Task.Run(() => Read(path, cancellationToken), cancellationToken);
    }

    private static AudiobookChapterListing Read(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ATL.Track track = new(path);
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan duration = TimeSpan.FromMilliseconds(track.DurationMs);
        if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            // Same duration source as the shelf reader, so VBR files agree on where "the end" is.
            using FileStream audio = File.OpenRead(path);
            if (MpegAudioData.TryRead(audio, cancellationToken) is { } mpeg) duration = mpeg.Duration;
        }
        if (duration <= TimeSpan.Zero) throw new InvalidDataException($"ATL could not read audio data from '{path}'.");
        List<AudiobookChapter> chapters = [];
        foreach (ATL.ChapterInfo chapter in track.Chapters)
            chapters.Add(new(TimeSpan.FromMilliseconds(chapter.StartTime), chapter.Title ?? ""));
        cancellationToken.ThrowIfCancellationRequested();
        return new(duration, chapters);
    }
}
