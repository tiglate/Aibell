using Aibell.Application.Lyrics;
using Aibell.Domain.Lyrics;

namespace Aibell.Infrastructure.Metadata;

/// <summary>
/// Reads lyrics from a file's tags through ATL: synchronized lyrics (ID3 SYLT) when present, otherwise the plain lyrics text. Plain text
/// that holds LRC timestamps (the usual convention for FLAC, Ogg and MP4 files, which have no synchronized frame) is read as synced.
/// </summary>
public sealed class AtlLyricsFileReader : ILyricsFileReader
{
    static AtlLyricsFileReader()
    {
        ATL.Settings.OutputStacktracesToConsole = false;
    }

    public Task<TrackLyrics?> ReadAsync(Uri location, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsFile) return Task.FromResult<TrackLyrics?>(null);
        string path = location.LocalPath;
        return Task.Run(() => Read(path, cancellationToken), cancellationToken);
    }

    private static TrackLyrics? Read(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(path);
        if (!File.Exists(path) || extension.Equals(".spc", StringComparison.OrdinalIgnoreCase) || extension.Equals(".rsn", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            ATL.Track track = new(path);
            foreach (ATL.LyricsInfo info in track.Lyrics)
            {
                List<LyricLine> synced = [.. info.SynchronizedLyrics.Where(phrase => !string.IsNullOrWhiteSpace(phrase.Text))
                    .Select(phrase => new LyricLine(TimeSpan.FromMilliseconds(phrase.TimestampStart), phrase.Text.Trim())).OrderBy(line => line.Start)];
                if (synced.Count > 0) return new TrackLyrics(synced, true, false, LyricsOrigin.Embedded, "file");
                if (TrackLyrics.FromText(null, info.UnsynchronizedLyrics, false, LyricsOrigin.Embedded, "file") is { } plain) return plain;
            }
            foreach (KeyValuePair<string, string> field in track.AdditionalFields)
                if (field.Key.Equals("USLT", StringComparison.OrdinalIgnoreCase) &&
                    TrackLyrics.FromText(null, field.Value, false, LyricsOrigin.Embedded, "file") is { } fromField) return fromField;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
