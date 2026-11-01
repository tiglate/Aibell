namespace Aibell.Domain.Audiobooks;

/// <summary>
/// URI rules only. Canonicalization (full path, link resolution) is an Infrastructure concern. Identity is the exact
/// canonical URI text: distinct-case paths stay distinct, so alias spellings on case-insensitive volumes are not merged.
/// </summary>
public static class AudiobookLocation
{
    public const int MaximumLength = 8192;
    private static readonly string[] AudioExtensions = [".mp3", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wav"];

    /// <summary>True for an absolute local (non-UNC) file URI with a supported audio extension, no query or fragment.</summary>
    public static bool IsSupported(Uri? location)
    {
        if (location is not { IsAbsoluteUri: true, IsFile: true, IsUnc: false }) return false;
        if (location.AbsoluteUri.Length > MaximumLength || location.Query.Length != 0 || location.Fragment.Length != 0) return false;
        string path = location.LocalPath;
        int dot = path.LastIndexOf('.');
        return dot >= 0 && dot > path.LastIndexOfAny(['/', '\\']) + 1 &&
            AudioExtensions.Contains(path[dot..], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The stable shelf key for an already-canonical location.</summary>
    public static string Key(Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!IsSupported(location)) throw new ArgumentException("Choose a local audio file (MP3, M4A/M4B, AAC, Ogg, Opus, FLAC or WAV).", nameof(location));
        return location.AbsoluteUri;
    }
}
