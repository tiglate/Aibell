using Aibell.Desktop.Localization;
using System.Collections.ObjectModel;
using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly PlayQueueModel playQueue = new();
    private bool isPlayingQueuedTrack;

    public ObservableCollection<PlayQueueEntryViewModel> QueueItems { get; } = [];
    public bool IsQueueEmpty => QueueItems.Count == 0;
    public string PlayQueueHeader => Loc.Format("Queue.Header", QueueItems.Count);
    public string QueueSummary => QueueItems.Count == 1 ? "1 track" : $"{QueueItems.Count} tracks";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayQueueCollapsed))]
    private bool isPlayQueueVisible = true;

    public bool IsPlayQueueCollapsed => !IsPlayQueueVisible;

    [ObservableProperty]
    private double playQueueWidth = 260;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarCollapsed))]
    private bool isSidebarVisible = true;

    public bool IsSidebarCollapsed => !IsSidebarVisible;

    [ObservableProperty]
    private double sidebarWidth = 240;

    [ObservableProperty]
    private double sidebarPlaylistsFraction = 0.5;

    [ObservableProperty]
    private double sidebarNowPlayingHeight = 250;

    [ObservableProperty]
    private double sidebarLibraryFraction = 0.3;

    [ObservableProperty]
    private PlayQueueEntryViewModel? selectedQueueEntry;

    public void QueueTracks(IEnumerable<Track> tracks, bool next = false)
    {
        Track[] batch = tracks.ToArray();
        if (batch.Length == 0)
        {
            return;
        }
        playQueue.Enqueue(batch, next);
        IsPlayQueueVisible = true;
        RefreshPlayQueue();
        StatusMessage = next ? Loc.Plural("Queue.QueuedNext", batch.Length) : Loc.Plural("Queue.QueuedLast", batch.Length);
    }

    public async Task QueueFilesAsync(IEnumerable<Uri> files, CancellationToken cancellationToken = default)
    {
        Uri[] locations = files.ToArray();
        await RunBusyAsync(async () =>
        {
            IReadOnlyList<Track> library = await repository.ListAsync(cancellationToken);
            List<Track> tracks = [];
            int failures = 0;
            foreach (Uri location in locations)
            {
                try
                {
                    tracks.Add(library.FirstOrDefault(track => track.Location == location)
                        ?? await importService.ReadTrackAsync(location, cancellationToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                }
            }
            QueueTracks(tracks);
            if (failures > 0)
            {
                StatusMessage = Loc.Format("Queue.QueuedFailures", tracks.Count, failures);
            }
        });
    }

    /// <summary>Queues the files (library or not, nothing is imported) and starts the first one that could be read.</summary>
    public async Task OpenFilesAsync(IEnumerable<Uri> files, CancellationToken cancellationToken = default)
    {
        int before = playQueue.Items.Count;
        await QueueFilesAsync(files, cancellationToken);
        if (playQueue.Items.Count > before)
        {
            await PlayQueueEntryAsync(playQueue.Items[before].Id, cancellationToken);
        }
    }

    public Task PlayQueueEntryAsync(Guid entryId, CancellationToken cancellationToken = default) =>
        RunPlaybackOperation(() => PlayQueueEntryCoreAsync(entryId, cancellationToken));

    private async Task PlayQueueEntryCoreAsync(Guid entryId, CancellationToken cancellationToken, TimeSpan? initialPosition = null, bool propagateFailure = false)
    {
        bool wasCurrent = playQueue.Current?.Id == entryId;
        if (changingTrack && propagateFailure) throw new InvalidOperationException("Wait for the current playback transition to finish.");
        if (changingTrack || !playQueue.TrySelect(entryId))
        {
            return;
        }
        isPlayingQueuedTrack = true;
        RefreshPlayQueue();
        if (initialPosition is null && wasCurrent && playbackEngine.Snapshot.Track?.Id == playQueue.Current!.Track.Id &&
            playbackEngine.Snapshot.Status is PlaybackStatus.Playing or PlaybackStatus.Paused)
        {
            try
            {
                await playbackEngine.PlayAsync(cancellationToken);
            }
            catch (InvalidOperationException exception) when (!propagateFailure)
            {
                StatusMessage = exception.Message;
            }
            return;
        }
        await PlayTrackAsync(playQueue.Current!.Track, cancellationToken, initialPosition, propagateFailure);
    }

    public void RemoveQueueEntry(Guid entryId)
    {
        playQueue.Remove(entryId);
        RefreshPlayQueue();
    }

    public void MoveQueueEntry(Guid entryId, Guid targetId, bool insertAfter)
    {
        playQueue.Move(entryId, targetId, insertAfter);
        RefreshPlayQueue();
    }

    public void ClearPlayQueue()
    {
        playQueue.Clear();
        RefreshPlayQueue();
        StatusMessage = Loc.Get("Queue.Cleared");
    }

    public async Task TogglePlayQueueAsync(CancellationToken cancellationToken = default)
    {
        IsPlayQueueVisible = !IsPlayQueueVisible;
        await PersistPreferencesAsync(cancellationToken);
    }

    public async Task ResizePlayQueueAsync(double width, CancellationToken cancellationToken = default)
    {
        PlayQueueWidth = double.IsFinite(width) ? Math.Clamp(width, 200, 480) : 260;
        await PersistPreferencesAsync(cancellationToken);
    }

    public async Task ToggleSidebarAsync(CancellationToken cancellationToken = default)
    {
        IsSidebarVisible = !IsSidebarVisible;
        await PersistPreferencesAsync(cancellationToken);
    }

    public async Task ResizeSidebarAsync(
        double width, double libraryFraction, double playlistsFraction, double nowPlayingHeight,
        CancellationToken cancellationToken = default)
    {
        SidebarLibraryFraction = double.IsFinite(libraryFraction) ? Math.Clamp(libraryFraction, 0.1, 0.8) : 0.3;
        SidebarWidth = double.IsFinite(width) ? Math.Clamp(width, 160, 480) : 240;
        SidebarPlaylistsFraction = double.IsFinite(playlistsFraction) ? Math.Clamp(playlistsFraction, 0.1, 0.9) : 0.5;
        SidebarNowPlayingHeight = double.IsFinite(nowPlayingHeight) ? Math.Clamp(nowPlayingHeight, 120, 600) : 250;
        await PersistPreferencesAsync(cancellationToken);
    }

    public async Task AddQueueEntryToLibraryAsync(PlayQueueEntryViewModel entry, CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(async () =>
        {
            await EnsureTrackInLibraryAsync(entry.Row.Source, cancellationToken);
            await RefreshPlaylistsAsync(cancellationToken);
            await RefreshLibraryFacetsAsync(cancellationToken);
            await RefreshLibrarySourcesAsync(cancellationToken);
            ReplaceTracks(await QueryVisibleTracksAsync(cancellationToken));
            StatusMessage = $"{entry.Row.Title} is in your library.";
        });
    }

    public async Task AddQueueEntryToPlaylistAsync(
        PlayQueueEntryViewModel entry,
        PlaylistFilterViewModel playlist,
        CancellationToken cancellationToken = default)
    {
        if (playlist.Id is not Guid playlistId || playlist.IsSmart)
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            Track track = await EnsureTrackInLibraryAsync(entry.Row.Source, cancellationToken);
            await playlistRepository.AddTrackAsync(playlistId, track.Id, cancellationToken);
            await RefreshPlaylistsAsync(cancellationToken);
            await RefreshLibraryFacetsAsync(cancellationToken);
            await RefreshLibrarySourcesAsync(cancellationToken);
            ReplaceTracks(await QueryVisibleTracksAsync(cancellationToken));
            StatusMessage = Loc.Format("Playlist.Added", track.Title, playlist.Name);
        });
    }

    public Task CreatePlaylistWithQueueEntryAsync(
        string name, PlayQueueEntryViewModel entry, CancellationToken cancellationToken = default)
    {
        return CreatePlaylistAsync(name, entry.Row, cancellationToken, importInitialTrack: true);
    }

    private async Task<Track> EnsureTrackInLibraryAsync(Track track, CancellationToken cancellationToken)
    {
        IReadOnlyList<Track> library = await repository.ListAsync(cancellationToken);
        return library.FirstOrDefault(item => item.Location == track.Location)
            ?? await importService.ImportTrackAsync(track.Location, cancellationToken);
    }

    private async Task<bool> PlayNextQueuedAsync(CancellationToken cancellationToken)
    {
        if (!playQueue.TryAdvance())
        {
            RefreshPlayQueue();
            return false;
        }
        isPlayingQueuedTrack = true;
        RefreshPlayQueue();
        await PlayTrackAsync(playQueue.Current!.Track, cancellationToken);
        return true;
    }

    private void RefreshPlayQueue()
    {
        Guid? selectedId = SelectedQueueEntry?.Id;
        QueueItems.Clear();
        for (int index = 0; index < playQueue.Items.Count; index++)
        {
            PlayQueueEntry entry = playQueue.Items[index];
            QueueItems.Add(new PlayQueueEntryViewModel(entry.Id, index + 1,
                TrackRowViewModel.FromTrack(entry.Track, [], false, false), entry.Id != playQueue.Current?.Id));
        }
        SelectedQueueEntry = QueueItems.FirstOrDefault(entry => entry.Id == selectedId);
        UpdatePlayQueueIndicators(playbackEngine.Snapshot);
        OnPropertyChanged(nameof(IsQueueEmpty));
        OnPropertyChanged(nameof(PlayQueueHeader));
        OnPropertyChanged(nameof(QueueSummary));
        NotifyNavigationState();
        _ = PersistPreferencesAsync(CancellationToken.None);
    }

    private void UpdatePlayQueueIndicators(PlaybackSnapshot snapshot)
    {
        foreach (PlayQueueEntryViewModel entry in QueueItems)
        {
            entry.Row.UpdatePlayback(isPlayingQueuedTrack && playQueue.Current?.Id == entry.Id
                ? snapshot : snapshot with { Track = null });
        }
    }
}

public sealed record PlayQueueEntryViewModel(Guid Id, int Position, TrackRowViewModel Row, bool CanReorder = true)
{
    public string Details => $"{Row.Title}{Environment.NewLine}{Row.Artist} — {Row.Album}";
}
