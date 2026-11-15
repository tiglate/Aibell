namespace Aibell.Mpd.Client;

public enum MpdState { Stopped, Playing, Paused }

/// <summary>The single-song mode: off, on, or "oneshot" (stop after this song, then switch off).</summary>
public enum MpdSingleMode { Off, On, OneShot }

/// <summary>A song as MPD lists it: its URI, its tags and, when playing from the queue, its place in it.</summary>
public sealed record MpdSong(
    string Uri,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Tags,
    TimeSpan? Duration = null,
    int? Position = null,
    int? Id = null,
    DateTimeOffset? LastModified = null)
{
    /// <summary>The first value of a tag (tag names are not case sensitive), or <c>null</c>.</summary>
    public string? Tag(string name) => Tags.TryGetValue(name, out IReadOnlyList<string>? values) && values.Count > 0 ? values[0] : null;

    public string? Title => Tag("Title");

    public string? Artist => Tag("Artist");

    public string? Album => Tag("Album");

    public string? AlbumArtist => Tag("AlbumArtist");

    /// <summary>The title, or the last part of the URI when the song has none.</summary>
    public string DisplayTitle => Title ?? Uri[(Uri.LastIndexOfAny(['/', '\\']) + 1)..];
}

public sealed record MpdStatus(
    int? Volume,
    bool Repeat,
    bool Random,
    MpdSingleMode SingleMode,
    bool Consume,
    int PlaylistVersion,
    int PlaylistLength,
    MpdState State,
    int? Song,
    int? SongId,
    TimeSpan? Elapsed,
    TimeSpan? Duration,
    int? Bitrate,
    TimeSpan? Crossfade,
    string? Audio,
    string? Error,
    string? Partition,
    bool IsUpdatingDatabase);

public sealed record MpdStats(int Artists, int Albums, int Songs, TimeSpan Uptime, TimeSpan DatabasePlaytime, DateTimeOffset? DatabaseUpdate, TimeSpan Playtime);

public sealed record MpdOutput(int Id, string Name, bool Enabled, string? Plugin);

public sealed record MpdStoredPlaylist(string Name, DateTimeOffset? LastModified);

public enum MpdEntryKind { Directory, Song, Playlist }

/// <summary>One item of a folder in MPD's music directory.</summary>
public sealed record MpdEntry(MpdEntryKind Kind, string Path, MpdSong? Song = null, DateTimeOffset? LastModified = null)
{
    /// <summary>The last part of the path.</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>An image returned by <c>albumart</c> or <c>readpicture</c>.</summary>
public sealed record MpdPicture(byte[] Data, string? MimeType);
