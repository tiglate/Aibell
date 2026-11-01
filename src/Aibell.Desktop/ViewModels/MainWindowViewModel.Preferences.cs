using Aibell.Desktop.Localization;
using Avalonia.Styling;
using Aibell.Application.Settings;
using Aibell.Domain.Playback;
using Aibell.Infrastructure.Podcasts;

namespace Aibell.Desktop.ViewModels;

public sealed record PreferencesData(ApplicationTheme Theme, bool ShowPlayQueue, double Volume, bool Muted,
    bool Shuffle, RepeatMode Repeat, bool ShowNotifications = false, string? PodcastFolder = null,
    bool PodcastAutoRefresh = true, bool OnlineLookup = false, PlaybackEngineKind PlaybackEngine = PlaybackEngineKind.Miniaudio,
    string? Language = null, bool RadioCatalog = false, string? SoundFont = null);

public sealed partial class MainWindowViewModel
{
    public ApplicationTheme AppearanceTheme { get; private set; } = ApplicationTheme.System;
    private string? language;
    private string? languageAtStart;
    private string? soundFontPath;
    private string? soundFontAtStart;
    private PlaybackEngineKind playbackEngineKind = PlaybackEngineDefaults.Preferred;
    private PlaybackEngineKind activePlaybackEngine = PlaybackEngineDefaults.Preferred;

    /// <summary>Records which player this run actually started with, which can differ from the saved choice if the built-in one could not load.</summary>
    public void ConfigurePlaybackEngine(PlaybackEngineKind active) => activePlaybackEngine = active;

    /// <summary>The player this run started with (the About window shows it); it can differ from the saved choice after a fallback.</summary>
    public PlaybackEngineKind ActivePlaybackEngine => activePlaybackEngine;
    public PreferencesData GetPreferences() => new(AppearanceTheme, IsPlayQueueVisible, Volume, preferredMuted,
        playbackQueue.ShuffleEnabled, playbackQueue.RepeatMode, ShowDesktopNotifications, PodcastAutoRefresh: podcastAutoRefresh, OnlineLookup: onlineLookup, PlaybackEngine: playbackEngineKind, Language: language, RadioCatalog: radioCatalogEnabled, SoundFont: soundFontPath);

    public Task SavePreferencesAsync(PreferencesData preferences, CancellationToken cancellationToken = default) =>
        RunPlaybackOperation(() => SavePreferencesCoreAsync(preferences, cancellationToken));

    private async Task SavePreferencesCoreAsync(PreferencesData preferences, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(preferences.Theme) || !Enum.IsDefined(preferences.Repeat) ||
            !Enum.IsDefined(preferences.PlaybackEngine) || !AppLanguages.IsValid(preferences.Language) || !double.IsFinite(preferences.Volume) || preferences.Volume is < 0 or > 1)
            throw new ArgumentException("Choose valid appearance and playback preferences.");
        // Validate the new download folder before anything else changes, so a bad path leaves every setting as it was.
        PodcastDownloadConfiguration? preparedFolder = await PreparePodcastFolderAsync(preferences.PodcastFolder, cancellationToken);
        if (playbackEngine.Snapshot.Track is not null)
        {
            await playbackEngine.SetVolumeAsync(preferences.Volume, cancellationToken);
            await playbackEngine.SetMutedAsync(preferences.Muted, cancellationToken);
        }
        if (!ShowDesktopNotifications && preferences.ShowNotifications && playbackEngine.Snapshot.Status == Aibell.Application.Playback.PlaybackStatus.Playing)
            notificationTrack = playbackEngine.Snapshot.Track?.Id;
        ShowDesktopNotifications = preferences.ShowNotifications;
        podcastAutoRefresh = preferences.PodcastAutoRefresh;
        onlineLookup = preferences.OnlineLookup;
        playbackEngineKind = preferences.PlaybackEngine;
        language = preferences.Language;
        soundFontPath = string.IsNullOrWhiteSpace(preferences.SoundFont) ? null : preferences.SoundFont.Trim();
        ApplyRadioCatalogEnabled(preferences.RadioCatalog);
        if (!ShowDesktopNotifications) CancelDesktopNotifications();
        AppearanceTheme = preferences.Theme; ApplyAppearanceTheme();
        IsPlayQueueVisible = preferences.ShowPlayQueue;
        Volume = preferences.Volume; preferredMuted = preferences.Muted;
        playbackQueue.SetShuffle(preferences.Shuffle); playbackQueue.SetRepeatMode(preferences.Repeat);
        ShuffleButtonText = preferences.Shuffle ? Loc.Get("Playback.ShuffleOn") : Loc.Get("Playback.ShuffleOff");
        RepeatButtonText = preferences.Repeat switch { RepeatMode.All => Loc.Get("Playback.RepeatAll"), RepeatMode.One => Loc.Get("Playback.RepeatOne"), _ => Loc.Get("Playback.RepeatOff") };
        MuteButtonText = preferences.Muted ? Loc.Get("Playback.Unmute") : Loc.Get("Playback.Mute");
        NotifyNavigationState();
        // The folder change must surface a failed write; ordinary writes only report it in the status line.
        if (preparedFolder is not null) await PersistPodcastFolderAsync(preparedFolder, cancellationToken);
        else await PersistPreferencesAsync(cancellationToken);
        StatusMessage = preparedFolder is null ? Loc.Get("Prefs.Saved")
            : Loc.Format("Prefs.SavedFolder", preparedFolder.Root);
        if (playbackEngineKind != activePlaybackEngine) StatusMessage += " " + Loc.Get("Prefs.EngineRestart");
        if (soundFontPath != soundFontAtStart) StatusMessage += " " + Loc.Get("Prefs.SoundFontRestart");
        if (language != languageAtStart) StatusMessage += " " + Loc.Get("Prefs.LanguageRestart");
    }

    private void ApplyAppearanceTheme()
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = AppearanceTheme switch
            {
                ApplicationTheme.Light => ThemeVariant.Light,
                ApplicationTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
    }
}
