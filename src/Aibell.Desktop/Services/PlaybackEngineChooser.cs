using Aibell.Application.Settings;
using Aibell.Infrastructure.Settings;
using Aibell.Playback.Alsa;
using Aibell.Playback.PipeWire;

namespace Aibell.Desktop.Services;

/// <summary>
/// The saved choice decides the player for the whole run. If the chosen player cannot run here, the next one that can takes over
/// and the reason is logged to <c>logs/playback.log</c>; the saved choice stays, so Preferences still show what the user picked.
/// </summary>
public static class PlaybackEngineChooser
{
    public delegate bool BuiltInAvailability(out string? problem);

    /// <summary>The SoundFont the user chose for MIDI, if any.</summary>
    public static string? SavedSoundFont(string settingsPath)
    {
        try
        {
            using JsonApplicationSettingsStore settings = new(settingsPath);
            return settings.LoadAsync().GetAwaiter().GetResult().SoundFontPath;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static PlaybackEngineKind Choose(string settingsPath, string dataDirectory, BuiltInAvailability builtInAvailable)
    {
        ArgumentNullException.ThrowIfNull(builtInAvailable);
        PlaybackEngineKind saved = PlaybackEngineDefaults.Preferred;
        try
        {
            using JsonApplicationSettingsStore settings = new(settingsPath);
            saved = settings.LoadAsync().GetAwaiter().GetResult().PlaybackEngine;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        if (saved == PlaybackEngineKind.Vlc) return saved;
        if (CanRun(saved, builtInAvailable, out string? problem)) return saved;

        // The chosen player cannot run here, so the next one that can takes over.
        foreach (PlaybackEngineKind fallback in FallbacksFor(saved))
        {
            if (!CanRun(fallback, builtInAvailable, out string? fallbackProblem)) continue;
            Log(dataDirectory, $"Using {Describe(fallback)} because {Describe(saved)} is unavailable: {problem ?? fallbackProblem}");
            return fallback;
        }
        Log(dataDirectory, $"Using VLC because {Describe(saved)} is unavailable: {problem}");
        return PlaybackEngineKind.Vlc;
    }

    /// <summary>The players to try, in order, when the chosen one cannot run.</summary>
    private static IEnumerable<PlaybackEngineKind> FallbacksFor(PlaybackEngineKind chosen) => chosen switch
    {
        PlaybackEngineKind.PipeWire => [PlaybackEngineKind.Alsa, PlaybackEngineKind.Miniaudio],
        PlaybackEngineKind.Alsa => [PlaybackEngineKind.PipeWire, PlaybackEngineKind.Miniaudio],
        _ => [PlaybackEngineKind.Miniaudio],
    };

    /// <summary>Whether a player can run on this machine.</summary>
    private static bool CanRun(PlaybackEngineKind kind, BuiltInAvailability builtInAvailable, out string? problem)
    {
        switch (kind)
        {
            case PlaybackEngineKind.Vlc:
                problem = null;
                return true;
            case PlaybackEngineKind.PipeWire:
                return PipeWireRuntime.IsAvailable(out problem);
            case PlaybackEngineKind.Alsa:
                return AlsaRuntime.IsAvailable(out problem);
            default:
                return builtInAvailable(out problem);
        }
    }

    /// <summary>The name of a player, for the log.</summary>
    private static string Describe(PlaybackEngineKind kind) => kind switch
    {
        PlaybackEngineKind.PipeWire => "PipeWire",
        PlaybackEngineKind.Alsa => "ALSA",
        PlaybackEngineKind.Vlc => "VLC",
        PlaybackEngineKind.Managed => "the C# player",
        _ => "the built-in player",
    };

    private static void Log(string dataDirectory, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));
            File.AppendAllText(Path.Combine(dataDirectory, "logs", "playback.log"), $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }
}
