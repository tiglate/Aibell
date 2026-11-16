namespace Aibell.Domain.Lyrics;

/// <summary>Where a track's lyrics came from. Higher-trust sources are never replaced by automatic lookups.</summary>
public enum LyricsOrigin
{
    /// <summary>Fetched by a lyrics plugin.</summary>
    Provider,

    /// <summary>Read from the audio file's tags.</summary>
    Embedded,

    /// <summary>Picked by the user from a provider's search results.</summary>
    UserSelected,

    /// <summary>Typed or pasted by the user.</summary>
    UserWritten,
}

/// <summary>
/// The lyrics of one track. <see cref="Lines"/> carry timestamps when <see cref="IsSynced"/> is true; otherwise they are plain
/// text lines in order. An instrumental track has no lines.
/// </summary>
public sealed record TrackLyrics(IReadOnlyList<LyricLine> Lines, bool IsSynced, bool IsInstrumental, LyricsOrigin Origin, string Source = "")
{
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Whether an automatic lookup may replace this entry.</summary>
    public bool IsUserOwned => Origin is LyricsOrigin.UserSelected or LyricsOrigin.UserWritten;

    public static TrackLyrics Instrumental(LyricsOrigin origin, string source = "") => new([], false, true, origin, source);

    /// <summary>Builds lyrics from raw text, preferring the synced LRC text when it has timestamps.</summary>
    public static TrackLyrics? FromText(string? synced, string? plain, bool instrumental, LyricsOrigin origin, string source = "")
    {
        if (instrumental) return Instrumental(origin, source);
        IReadOnlyList<LyricLine> parsed = LrcParser.Parse(synced);
        if (parsed.Count > 0 && parsed.Any(line => line.Start is not null)) return new TrackLyrics(parsed, true, false, origin, source);
        parsed = LrcParser.Parse(plain);
        if (parsed.Count == 0) parsed = LrcParser.Parse(synced);
        if (parsed.Count == 0) return null;
        bool timed = parsed.Any(line => line.Start is not null);
        return new TrackLyrics(parsed, timed, false, origin, source);
    }

    /// <summary>The LRC text for synced lyrics, or plain lines otherwise.</summary>
    public string ToText() => IsSynced ? LrcParser.Format(Lines) : string.Join('\n', Lines.Select(line => line.Text));
}
