using Aibell.Application.Library;
using Aibell.Application.Lyrics;
using Aibell.Application.Playback;
using Aibell.Desktop.Localization;
using Aibell.Domain.Lyrics;
using Aibell.Domain.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool lyricsEnabled = true;
    private LyricsViewModel? lyrics;

    /// <summary>The lyrics panel's state; null when no lyrics service is available.</summary>
    public LyricsViewModel? Lyrics => lyrics ??= lyricsService is null ? null : new LyricsViewModel(lyricsService);

    /// <summary>Whether the lyrics feature is on in Preferences (and a service exists). The side panel shows its tabs only then.</summary>
    public bool LyricsEnabled => lyricsEnabled;

    public bool IsLyricsAvailable => lyricsEnabled && lyricsService is not null;

    /// <summary>The side panel shows the lyrics instead of the play queue. Lyrics are the first tab and the one shown at start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueueTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsLyricsTabSelected))]
    private bool lyricsTabRequested = true;

    public bool IsLyricsTabSelected => IsLyricsAvailable && LyricsTabRequested;

    public bool IsQueueTabSelected => !IsLyricsTabSelected;

    public void ShowQueueTab() => LyricsTabRequested = false;

    public void ShowLyricsTab() => LyricsTabRequested = true;

    private void ApplyLyricsEnabled(bool enabled)
    {
        lyricsEnabled = enabled;
        OnPropertyChanged(nameof(IsLyricsAvailable));
        OnPropertyChanged(nameof(IsLyricsTabSelected));
        OnPropertyChanged(nameof(IsQueueTabSelected));
        if (Lyrics is not { } panel) return;
        panel.SetEnabled(enabled);
        // Switching back on picks up the track that is playing now.
        if (enabled) UpdateLyrics(playbackEngine.Snapshot);
    }

    private bool IsInPodcastFolder(Uri location)
    {
        string root = PodcastDownloadRoot;
        if (!location.IsFile || string.IsNullOrWhiteSpace(root)) return false;
        string relative = Path.GetRelativePath(root, location.LocalPath);
        return !Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal);
    }

    private void UpdateLyrics(PlaybackSnapshot snapshot)
    {
        if (!lyricsEnabled || Lyrics is not { } panel) return;
        Track? track = snapshot.Status is PlaybackStatus.Playing or PlaybackStatus.Paused ? snapshot.Track : null;
        // Podcast episodes and radio are spoken or streamed content: there is nothing to look up for them.
        if (track is not null && (IsRadioPlayback || IsInPodcastFolder(track.Location))) track = track with { Source = "Podcast" };
        panel.SetTrack(track);
        panel.SetPosition(snapshot.Position);
    }

    /// <summary>Whether the lyrics can be written into the track's file: a local, writable file that is not playing.</summary>
    public async Task<string?> GetLyricsFileWriteBlockAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (metadataFileWriter is null || trackFileDetailsReader is null || !track.Location.IsFile) return Loc.Get("Lyrics.NoFileWrite");
        if (playbackEngine.Snapshot is { Track: { } playing, Status: not PlaybackStatus.Stopped } && playing.Location == track.Location)
            return Loc.Get("Lyrics.StopToWrite");
        string extension = Path.GetExtension(track.Location.LocalPath);
        if (!new[] { ".flac", ".m4a", ".ogg", ".opus", ".mp3" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return Loc.Get("Lyrics.FormatNoWrite");
        TrackFileDetails details = await trackFileDetailsReader.ReadAsync(track.Location, cancellationToken);
        return details.SourceHash is null ? Loc.Get("Lyrics.NoFileWrite") : details.FileWriteRestriction;
    }

    /// <summary>
    /// Writes the lyrics into the track's tags through the same verified, backed-up path as the track properties dialog
    /// (a backup is kept and can be restored there), then brings the library's file state up to date.
    /// </summary>
    public Task SaveLyricsToFileAsync(Track track, string text, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(() => SaveLyricsToFileCoreAsync(track, text, cancellationToken));

    private async Task SaveLyricsToFileCoreAsync(Track track, string text, CancellationToken cancellationToken)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current library operation to finish.");
        if (metadataFileWriter is null || trackFileDetailsReader is null) throw new NotSupportedException(Loc.Get("Lyrics.NoFileWrite"));
        if (changingTrack) throw new InvalidOperationException("Wait for playback to finish changing tracks.");
        EnsureFileIsNotPlaying(track);
        TrackFileDetails details = await trackFileDetailsReader.ReadAsync(track.Location, cancellationToken);
        if (details.SourceHash is null || details.FileWriteRestriction is not null) throw new NotSupportedException(details.FileWriteRestriction ?? Loc.Get("Lyrics.NoFileWrite"));
        MetadataEdit patch = new(Lyrics: text);
        patch.Validate();
        TrackFileEdit.ValidateFormat(patch, Path.GetExtension(track.Location.LocalPath));
        IsBusy = true; changingTrack = true;
        MetadataFileWriteResult? result = null;
        try
        {
            result = await metadataFileWriter.WriteAsync(track.Location, patch, details.SourceHash, cancellationToken);
            if (repository is ITrackMetadataRepository writer)
            {
                Track current = refreshedTracks.TryGetValue(track.Id, out Track? latest) ? latest : track;
                Track updated = await writer.SaveMetadataAsync(track.Id, TrackMetadata.FromTrack(current), FileState(track.Location), CancellationToken.None);
                refreshedTracks[track.Id] = updated;
            }
            StatusMessage = Loc.Format("Lyrics.SavedToFile", result.Backup.Backup.LocalPath);
        }
        finally
        {
            IsBusy = false;
            CompletePlaybackTransition();
        }
    }
}
