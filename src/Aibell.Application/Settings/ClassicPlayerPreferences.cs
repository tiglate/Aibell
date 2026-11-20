using Aibell.Domain.Playback;

namespace Aibell.Application.Settings;

/// <summary>
/// Classic Player Mode: whether it is on, the skin and the layout of its window, and its own playlist (which, unlike the
/// Play Queue, keeps every track after it plays). <see cref="SeparateWindows"/> stays null until the user picks a layout, so
/// the window can follow the platform's default (one window under a tiling compositor, separate windows elsewhere).
/// </summary>
public sealed record ClassicPlayerPreferences(
    bool IsEnabled = false,
    string? SkinId = null,
    bool DoubleSize = false,
    bool AlwaysOnTop = false,
    bool ShowEqualizer = true,
    bool ShowPlaylist = true,
    bool MainShaded = false,
    bool EqualizerShaded = false,
    bool PlaylistShaded = false,
    int PlaylistExtraRows = 2,
    bool ShowRemainingTime = false,
    int PlaylistCurrent = -1,
    IReadOnlyList<PlayQueueEntry>? Playlist = null,
    bool? SeparateWindows = null,
    ClassicWindowPosition? MainPosition = null,
    ClassicWindowPosition? EqualizerPosition = null,
    ClassicWindowPosition? PlaylistPosition = null,
    int PlaylistExtraColumns = 0)
{
    /// <summary>The playlist can widen by this many 25-pixel steps beyond the classic 275 pixels.</summary>
    public const int MaximumExtraColumns = 40;

    /// <summary>The playlist can grow by this many 29-pixel steps below its smallest size.</summary>
    public const int MaximumExtraRows = 24;

    public static ClassicPlayerPreferences Default { get; } = new();

    /// <summary>The same preferences with values a hand-edited file could break brought back into range.</summary>
    public ClassicPlayerPreferences Normalized()
    {
        PlayQueueEntry[] playlist = (Playlist ?? [])
            .Where(entry => entry is not null && entry.Id != Guid.Empty && entry.Track is not null && entry.Track.Id != Guid.Empty &&
                            entry.Track.Location is { IsAbsoluteUri: true } && entry.Track.Title is not null &&
                            entry.Track.Artist is not null && entry.Track.Album is not null)
            .DistinctBy(entry => entry.Id)
            .ToArray();
        string? skin = string.IsNullOrWhiteSpace(SkinId) || Path.GetFileName(SkinId) != SkinId ? null : SkinId;
        return this with
        {
            SkinId = skin,
            PlaylistExtraRows = Math.Clamp(PlaylistExtraRows, 0, MaximumExtraRows),
            PlaylistExtraColumns = Math.Clamp(PlaylistExtraColumns, 0, MaximumExtraColumns),
            PlaylistCurrent = PlaylistCurrent >= 0 && PlaylistCurrent < playlist.Length ? PlaylistCurrent : -1,
            Playlist = playlist,
        };
    }
}

/// <summary>Where one of the separate classic windows was left, in screen pixels (its top-left corner).</summary>
public sealed record ClassicWindowPosition(int X, int Y);
