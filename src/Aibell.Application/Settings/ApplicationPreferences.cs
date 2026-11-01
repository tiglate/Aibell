using Aibell.Domain.Playback;

namespace Aibell.Application.Settings;

/// <summary>
/// The player to use when the user has not chosen one. Linux gets the PipeWire output, with ALSA
/// and miniaudio as fallbacks when no PipeWire daemon is running; the other systems get miniaudio.
/// </summary>
public static class PlaybackEngineDefaults
{
    /// <summary>The player to use when nothing has been chosen.</summary>
    public static PlaybackEngineKind Preferred => OperatingSystem.IsLinux()
        ? PlaybackEngineKind.PipeWire
        : PlaybackEngineKind.Miniaudio;
}

public sealed record ApplicationPreferences(
    double Volume,
    bool IsMuted,
    bool ShuffleEnabled,
    RepeatMode RepeatMode,
    IReadOnlyList<TrackColumnPreference>? TrackColumns = null,
    bool IsPlayQueueVisible = true,
    double PlayQueueWidth = 260,
    IReadOnlyList<PlayQueueEntry>? PlayQueue = null,
    ApplicationTheme Theme = ApplicationTheme.System,
    bool ShowDesktopNotifications = false,
    string? PodcastDownloadFolder = null,
    IReadOnlyList<string>? PodcastDownloadFolderHistory = null,
    double PodcastPlaybackRate = 1.0,
    bool PodcastAutoRefresh = true,
    bool OnlineLookup = false,
    bool IsSidebarVisible = true,
    double SidebarWidth = 240,
    double SidebarPlaylistsFraction = 0.5,
    double SidebarNowPlayingHeight = 250,
    double SidebarLibraryFraction = 0.3,
    EqualizerSettings? Equalizer = null,
    PlaybackEngineKind PlaybackEngine = PlaybackEngineKind.Miniaudio,
    string? Language = null,
    bool RadioCatalogEnabled = false,
    string? SoundFontPath = null)
{
    public static ApplicationPreferences Default { get; } = new(
        Volume: 0.8,
        IsMuted: false,
        ShuffleEnabled: false,
        RepeatMode.Off,
        PlaybackEngine: PlaybackEngineDefaults.Preferred);
}

public enum ApplicationTheme { System, Light, Dark }

/// <summary>
/// Which player produces the sound: the built-in miniaudio engine, LibVLC, the built-in engine with the managed C# decoders ("C# Playback"),
/// or the managed PipeWire and ALSA outputs on Linux.
/// Applied when the application starts.
/// </summary>
public enum PlaybackEngineKind
{
    /// <summary>The built-in player through miniaudio.</summary>
    Miniaudio,

    /// <summary>LibVLC.</summary>
    Vlc,

    /// <summary>The built-in player with the managed C# decoders, still through miniaudio.</summary>
    Managed,

    /// <summary>The built-in player through the managed PipeWire output (Linux).</summary>
    PipeWire,

    /// <summary>The built-in player through the ALSA output (Linux).</summary>
    Alsa,
}

public sealed record TrackColumnPreference(
    string Kind,
    bool IsVisible,
    double? Width = null);
