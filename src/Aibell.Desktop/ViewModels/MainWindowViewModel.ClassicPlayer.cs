using Aibell.Application.Playback;
using Aibell.Application.Settings;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;

namespace Aibell.Desktop.ViewModels;

/// <summary>What Classic Player Mode needs from the player: its saved layout, the transport, and a playback list it can own.</summary>
public sealed partial class MainWindowViewModel
{
    private ClassicPlayerPreferences classicPlayer = ClassicPlayerPreferences.Default;

    /// <summary>Raised once the saved preferences (and with them the classic playlist) have been read.</summary>
    public event EventHandler? PreferencesLoaded;

    public bool ArePreferencesLoaded => preferencesLoaded;

    /// <summary>Whether online features are allowed (Preferences, Online); the skin museum is hidden otherwise.</summary>
    public bool IsOnlineLookupEnabled => onlineLookup;

    public ClassicPlayerPreferences ClassicPlayer => classicPlayer;

    public PlaybackSnapshot PlaybackSnapshot => playbackEngine.Snapshot;

    public bool IsShuffleEnabled => playbackQueue.ShuffleEnabled;

    public RepeatMode CurrentRepeatMode => playbackQueue.RepeatMode;

    /// <summary>Changes whenever another list takes over playback; see <see cref="PlaybackListPosition"/>.</summary>
    public long PlaybackListVersion => playbackQueue.Version;

    /// <summary>The position, in the list last given to <see cref="PlayListAsync"/>, of the track playing from it; -1 if none.</summary>
    public int PlaybackListPosition => playbackQueue.CurrentSourceIndex;

    /// <summary>Whether the track playing came from the play queue rather than from the playback list.</summary>
    public bool IsPlayingFromPlayQueue => isPlayingQueuedTrack;

    public Task UpdateClassicPlayerAsync(Func<ClassicPlayerPreferences, ClassicPlayerPreferences> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        classicPlayer = change(classicPlayer).Normalized();
        return PersistPreferencesAsync(cancellationToken);
    }

    /// <summary>Plays <paramref name="tracks"/> as the playback list, starting at <paramref name="index"/>.</summary>
    public Task PlayListAsync(IReadOnlyList<Track> tracks, int index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return RunPlaybackOperation(async () =>
        {
            if (changingTrack || index < 0 || index >= tracks.Count) return;
            try
            {
                playbackQueue.Replace(tracks, index);
                await PlayCurrentAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException)
            {
                ReportPlaybackFailure(playbackEngine.Snapshot.Track, exception.Message, exception, null);
            }
            finally
            {
                NotifyNavigationState();
            }
        });
    }

    /// <summary>
    /// Replaces the playback list without interrupting the track playing: the list was edited, and Next and Previous should
    /// follow the edit. <paramref name="current"/> is the playing entry's new position, or -1 when it was removed.
    /// </summary>
    public void UpdatePlaybackList(IReadOnlyList<Track> tracks, int current)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (current >= 0 && current < tracks.Count) playbackQueue.Replace(tracks, current);
        else playbackQueue.Replace(tracks);
        NotifyNavigationState();
    }

    public Task SetRepeatAsync(RepeatMode mode, CancellationToken cancellationToken = default) => SetRepeatModeAsync(mode, cancellationToken);

    /// <summary>The tracks behind these files: library entries where the library has them, read from the file otherwise.</summary>
    public async Task<IReadOnlyList<Track>> ReadTracksAsync(IEnumerable<Uri> files, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Track> library = await repository.ListAsync(cancellationToken);
        Dictionary<Uri, Track> indexed = library.GroupBy(track => track.Location).ToDictionary(group => group.Key, group => group.First());
        List<Track> tracks = [];
        foreach (Uri location in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                tracks.Add(indexed.GetValueOrDefault(location) ?? await importService.ReadTrackAsync(location, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // Unreadable files are skipped, as the classic players did.
            }
        }
        return tracks;
    }

    public Task<IReadOnlyList<Track>> ListLibraryTracksAsync(CancellationToken cancellationToken = default) => repository.ListAsync(cancellationToken);

    /// <summary>The tracks of one of the library's playlists (smart ones included).</summary>
    public Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(PlaylistFilterViewModel playlist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        return playlist.Id is Guid id ? playlistRepository.GetTracksAsync(id, string.Empty, cancellationToken) : repository.ListAsync(cancellationToken);
    }

    /// <summary>The readable tracks of an M3U or PLS file.</summary>
    public async Task<IReadOnlyList<Track>> ReadPlaylistFileAsync(Uri file, CancellationToken cancellationToken = default)
    {
        if (playlistFiles is null) return [];
        return (await playlistFiles.PrepareAsync(file, cancellationToken)).Tracks;
    }

    public Task WritePlaylistFileAsync(Uri file, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default) =>
        playlistFiles?.ExportAsync(file, tracks, cancellationToken) ?? Task.CompletedTask;

    private async Task RestoreClassicPlayerAsync(ClassicPlayerPreferences? saved, CancellationToken cancellationToken)
    {
        classicPlayer = (saved ?? ClassicPlayerPreferences.Default).Normalized();
        if (classicPlayer.Playlist is not { Count: > 0 } entries) return;
        // Library tracks may have been edited since; the playlist shows them as they are now.
        IReadOnlyList<Track> library = await repository.ListAsync(cancellationToken);
        Dictionary<Guid, Track> byId = library.ToDictionary(track => track.Id);
        classicPlayer = classicPlayer with
        {
            Playlist = [.. entries.Select(entry => byId.TryGetValue(entry.Track.Id, out Track? track) ? entry with { Track = track } : entry)],
        };
    }

    private void RaisePreferencesLoaded() => PreferencesLoaded?.Invoke(this, EventArgs.Empty);
}
