using Aibell.Desktop.Localization;
using Aibell.Application.Discs;
using System.Collections.ObjectModel;
using System.Globalization;
using Aibell.Application.Library;
using Aibell.Application.Radio;
using Aibell.Application.Playback;
using Aibell.Application.Playlists;
using Aibell.Application.Settings;
using Aibell.Desktop.Services;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Domain.Playlists;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel(
    LibraryImportService importService,
    ITrackRepository repository,
    ILibraryBrowser libraryBrowser,
    ILibrarySourceRepository sourceRepository,
    IPlaylistRepository playlistRepository,
    IApplicationSettingsStore settingsStore,
    LibraryMonitoringService monitoringService,
    IPlaybackEngine playbackEngine,
    IArtworkProvider? artworkProvider = null,
    ITrackFileDetailsReader? trackFileDetailsReader = null,
    IMetadataFileWriter? metadataFileWriter = null,
    IMetadataEditRecovery? metadataEditRecovery = null,
    PlaylistFileService? playlistFiles = null,
    IDesktopNotifications? desktopNotifications = null,
    IRadioStationRepository? radioRepository = null,
    RadioPlaylistImportService? radioPlaylistFiles = null,
    ICuratedStationCatalog? curatedCatalog = null,
    ICoverImageLoader? radioLogos = null) : ObservableObject, IDisposable
{
    private readonly PlaybackQueueModel playbackQueue = new();

    private bool changingTrack;
    private bool isListening;
    private bool isUserSeeking;
    private TrackSortField sortField = TrackSortField.Default;
    private bool sortDescending;
    private LibraryViewKind activeView = LibraryViewKind.AllMusic;
    private bool preferencesLoaded;
    private Task pendingPreferencesSave = Task.CompletedTask;
    private bool preferredMuted;
    private (Guid? TrackId, PlaybackStatus Status) playbackIndicator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageRadio))]
    [NotifyPropertyChangedFor(nameof(CanImportRadioPlaylist))]
    [NotifyPropertyChangedFor(nameof(CanEditRadioStation))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(CanImportPlaylistFile))]
    [NotifyPropertyChangedFor(nameof(CanExportPlaylistFile))]
    [NotifyPropertyChangedFor(nameof(CanCreatePlaylist))]
    [NotifyPropertyChangedFor(nameof(CanAddToPlaylist))]
    [NotifyPropertyChangedFor(nameof(CanRemoveFromPlaylist))]
    [NotifyPropertyChangedFor(nameof(CanEditPlaylist))]
    [NotifyPropertyChangedFor(nameof(CanMovePlaylistTrack))]
    [NotifyPropertyChangedFor(nameof(CanReorderPlaylist))]
    [NotifyPropertyChangedFor(nameof(CanManageLibrarySource))]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = Loc.Get("Status.Loading");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanAddToPlaylist))]
    [NotifyPropertyChangedFor(nameof(CanRemoveFromPlaylist))]
    [NotifyPropertyChangedFor(nameof(CanMovePlaylistTrack))]
    private TrackRowViewModel? selectedTrack;

    [ObservableProperty]
    private string playbackButtonText = Loc.Get("Playback.Play");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaybackStopped))]
    private bool isPlaybackPlaying;

    public bool IsPlaybackStopped => !IsPlaybackPlaying;

    [ObservableProperty]
    private string nowPlaying = Loc.Get("Playback.Nothing");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private double positionSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private double durationSeconds = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercentText))]
    private double volume = 0.8;

    public string PositionText => FormatPlaybackTime(TimeSpan.FromSeconds(PositionSeconds));

    public string DurationText => IsRadioPlayback ? "LIVE" : FormatPlaybackTime(TimeSpan.FromSeconds(DurationSeconds));

    public string VolumePercentText => $"{Volume:P0}";

    [ObservableProperty]
    private string muteButtonText = Loc.Get("Playback.Mute");

    [ObservableProperty]
    private string shuffleButtonText = Loc.Get("Playback.ShuffleOff");

    [ObservableProperty]
    private string repeatButtonText = Loc.Get("Playback.RepeatOff");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReorderPlaylist))]
    private string searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveFromPlaylist))]
    [NotifyPropertyChangedFor(nameof(CanEditPlaylist))]
    [NotifyPropertyChangedFor(nameof(CanMovePlaylistTrack))]
    [NotifyPropertyChangedFor(nameof(CanReorderPlaylist))]
    [NotifyPropertyChangedFor(nameof(CanExportPlaylistFile))]
    private PlaylistFilterViewModel? selectedPlaylist;

    [ObservableProperty]
    private GenreFilterViewModel? selectedGenre;

    [ObservableProperty]
    private ArtistFilterViewModel? selectedArtist;

    [ObservableProperty]
    private AlbumFilterViewModel? selectedAlbum;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageLibrarySource))]
    private LibrarySourceViewModel? selectedLibrarySource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddToPlaylist))]
    private PlaylistFilterViewModel? playlistTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreatePlaylist))]
    private string newPlaylistName = string.Empty;

    public ObservableCollection<TrackRowViewModel> Tracks { get; } = [];

    public ObservableCollection<PlaylistFilterViewModel> Playlists { get; } = [];

    public ObservableCollection<PlaylistFilterViewModel> EditablePlaylists { get; } = [];

    public ObservableCollection<ArtistFilterViewModel> Artists { get; } = [];

    public ObservableCollection<AlbumFilterViewModel> Albums { get; } = [];

    public ObservableCollection<GenreFilterViewModel> Genres { get; } = [];

    public ObservableCollection<LibrarySourceViewModel> LibrarySources { get; } = [];

    public ObservableCollection<TrackColumnViewModel> TrackColumns { get; } = [];

    public ObservableCollection<TrackColumnViewModel> VisibleTrackColumns { get; } = [];

    public bool CanImport => !IsBusy;

    public bool CanManageLibrarySource => !IsBusy && SelectedLibrarySource is not null;

    public bool CanPlay => (IsRadioView && SelectedRadioId is not null) || (CanSearchLibrary && SelectedTrack is not null) || playbackEngine.Snapshot.Track is not null || playQueue.HasPending;

    public bool CanStop => playbackEngine.Snapshot is
    {
        Track: not null,
        Status: PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Ended
    } || (IsRadioPlayback && radioConnectionRequested);

    public bool CanCreatePlaylist => !IsBusy && !string.IsNullOrWhiteSpace(NewPlaylistName);

    public bool CanAddToPlaylist =>
        !IsBusy && SelectedTrack is not null && PlaylistTarget is { Id: not null, IsSmart: false };

    public bool CanRemoveFromPlaylist =>
        !IsBusy && activeView == LibraryViewKind.Playlist &&
        SelectedTrack is not null && SelectedPlaylist is { Id: not null, IsSmart: false };

    public bool CanEditPlaylist =>
        !IsBusy && activeView == LibraryViewKind.Playlist && SelectedPlaylist?.Id is not null;

    public bool CanMovePlaylistTrack => CanRemoveFromPlaylist;

    public bool CanReorderPlaylist =>
        !IsBusy && activeView == LibraryViewKind.Playlist &&
        SelectedPlaylist is { Id: not null, IsSmart: false } && SearchQuery.Length == 0;

    public string TitleHeaderText => FormatSortHeader(Loc.Get("Column.Title"), TrackSortField.Title);

    public string ArtistHeaderText => FormatSortHeader(Loc.Get("Column.Artist"), TrackSortField.Artist);

    public string AlbumHeaderText => FormatSortHeader(Loc.Get("Column.Album"), TrackSortField.Album);

    public string GenreHeaderText => FormatSortHeader(Loc.Get("Column.Genre"), TrackSortField.Genre);

    public string DurationHeaderText => FormatSortHeader(Loc.Get("Column.Time"), TrackSortField.Duration);

    public bool CanMute => playbackEngine.Snapshot.Track is not null;

    public bool CanGoPrevious => !changingTrack && !IsRadioPlayback && (playbackQueue.CanMovePrevious || (isPlayingQueuedTrack && playbackQueue.Current is not null));

    public bool CanGoNext => !changingTrack && !IsRadioPlayback && (playQueue.HasPending || (playbackQueue.CanMoveNext && playbackQueue.Current is not null));

    public bool IsEmpty => Tracks.Count == 0;

    public bool IsColumnBrowserVisible => activeView == LibraryViewKind.AllMusic && overlay == OverlayView.None;

    public void StartListening()
    {
        isListening = true;
        playbackEngine.StateChanged -= OnPlaybackStateChanged;
        playbackEngine.StateChanged += OnPlaybackStateChanged;
        monitoringService.LibrarySynchronized -= OnLibrarySynchronized;
        monitoringService.LibrarySynchronized += OnLibrarySynchronized;
        ObservePlaybackRequestBoundary(playbackEngine.Snapshot);
        ApplyPlaybackSnapshot(playbackEngine.Snapshot);
    }

    public void StopListening()
    {
        isListening = false;
        playbackEngine.StateChanged -= OnPlaybackStateChanged;
        monitoringService.LibrarySynchronized -= OnLibrarySynchronized;
        CancelArtwork();
        CancelDesktopNotifications();
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(async () =>
        {
            EnsureTrackColumns();
            await LoadPreferencesAsync(cancellationToken);
            await RefreshPlaylistsAsync(cancellationToken);
            await RefreshLibraryFacetsAsync(cancellationToken);
            await RefreshLibrarySourcesAsync(cancellationToken);
            IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
            ReplaceTracks(tracks, autoFitColumns: true);
            StatusMessage = FormatResultCount(tracks.Count);
        });
    }

    public async Task ImportAsync(string folder, CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(async () =>
        {
            await sourceRepository.AddAsync(folder, cancellationToken);
            monitoringService.Watch(folder);
            StatusMessage = Loc.Format("Library.Importing", folder);
            LibraryImportResult result = await importService.ImportAsync([folder], cancellationToken);
            await RefreshPlaylistsAsync(cancellationToken);
            await RefreshLibrarySourcesAsync(cancellationToken, folder);
            await RefreshLibraryFacetsAsync(cancellationToken);
            IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
            ReplaceTracks(tracks, autoFitColumns: true);
            StatusMessage = result.Issues.Count == 0
                ? Loc.Format("Library.Synced", result.Imported, result.Removed, FormatResultCount(tracks.Count))
                : Loc.Format("Library.SyncedIssues", result.Imported, result.Removed, result.Issues.Count);
        });
    }

    public Task SearchAsync(string query, CancellationToken cancellationToken = default) =>
        RunLibraryOperation(() => SearchCoreAsync(query, cancellationToken));

    private async Task SearchCoreAsync(string query, CancellationToken cancellationToken)
    {
        SearchQuery = query.Trim();
        if (overlay != OverlayView.None) return; // Podcasts has its own title search and the other overlays have nothing to search; Music results stay untouched.
        if (IsRadioView) { FilterRadioStations(resetScroll: true); return; }
        IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
        ReplaceTracks(tracks, autoFitColumns: true);
        StatusMessage = FormatResultCount(tracks.Count);
    }

    public Task SelectPlaylistAsync(PlaylistFilterViewModel? playlist, CancellationToken cancellationToken = default) =>
        RunLibraryOperation(() => SelectPlaylistCoreAsync(playlist, cancellationToken));

    private async Task SelectPlaylistCoreAsync(PlaylistFilterViewModel? playlist, CancellationToken cancellationToken)
    {
        SelectedPlaylist = playlist ?? Playlists.FirstOrDefault();
        SetActiveView(SelectedPlaylist?.Id is null
            ? LibraryViewKind.AllMusic
            : LibraryViewKind.Playlist);
        if (activeView == LibraryViewKind.AllMusic)
        {
            await ResetBrowserFiltersAsync(cancellationToken);
        }
        SetSort(SelectedPlaylist?.Id is null || SelectedPlaylist.IsSmart ? TrackSortField.Default : TrackSortField.PlaylistOrder);
        IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
        ReplaceTracks(tracks, autoFitColumns: true);
        StatusMessage = FormatResultCount(tracks.Count);
    }

    public Task SelectGenreAsync(GenreFilterViewModel genre, CancellationToken cancellationToken = default) =>
        RunLibraryOperation(() => SelectGenreCoreAsync(genre, cancellationToken));

    private async Task SelectGenreCoreAsync(GenreFilterViewModel genre, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(genre);
        SelectedGenre = genre;
        SetActiveView(LibraryViewKind.AllMusic);
        SetSort(TrackSortField.Default);
        await RefreshArtistAlbumFacetsAsync(null, null, cancellationToken);
        IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
        ReplaceTracks(tracks, autoFitColumns: true);
        StatusMessage = FormatResultCount(tracks.Count);
    }

    public Task SelectArtistAsync(ArtistFilterViewModel artist, CancellationToken cancellationToken = default) =>
        RunLibraryOperation(() => SelectArtistCoreAsync(artist, cancellationToken));

    private async Task SelectArtistCoreAsync(ArtistFilterViewModel artist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artist);
        SelectedArtist = artist;
        SetActiveView(LibraryViewKind.AllMusic);
        SetSort(TrackSortField.Default);
        await RefreshAlbumFacetsAsync(null, cancellationToken);
        IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
        ReplaceTracks(tracks, autoFitColumns: true);
        StatusMessage = FormatResultCount(tracks.Count);
    }

    public Task SelectAlbumAsync(AlbumFilterViewModel album, CancellationToken cancellationToken = default) =>
        RunLibraryOperation(() => SelectAlbumCoreAsync(album, cancellationToken));

    private async Task SelectAlbumCoreAsync(AlbumFilterViewModel album, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(album);
        SelectedAlbum = album;
        SetActiveView(LibraryViewKind.AllMusic);
        SetSort(TrackSortField.Default);
        IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
        ReplaceTracks(tracks, autoFitColumns: true);
        StatusMessage = FormatResultCount(tracks.Count);
    }

    public async Task RescanSelectedLibrarySourceAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedLibrarySource is not { } source)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            LibraryImportResult result = await importService
                .ImportAsync([source.Path], cancellationToken);
            await RefreshPlaylistsAsync(cancellationToken);
            await RefreshLibraryFacetsAsync(cancellationToken);
            IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
            ReplaceTracks(tracks);
            StatusMessage = Loc.Format("Library.Rescanned", source.Path, result.Imported, result.Removed, result.Issues.Count);
        });
    }

    public async Task RemoveSelectedLibrarySourceAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedLibrarySource is not { } source)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await monitoringService.StopWatchingAsync(source.Path, cancellationToken);
            int removedTracks;
            try
            {
                removedTracks = await sourceRepository.RemoveAsync(source.Path, cancellationToken);
            }
            catch
            {
                monitoringService.Watch(source.Path);
                throw;
            }

            await RefreshLibrarySourcesAsync(cancellationToken);
            await RefreshPlaylistsAsync(cancellationToken);
            await RefreshLibraryFacetsAsync(cancellationToken);
            IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
            ReplaceTracks(tracks);
            StatusMessage = Loc.Plural("Library.StoppedWatching", removedTracks, source.Path);
        });
    }

    public Task CreatePlaylistAsync(CancellationToken cancellationToken = default)
    {
        return CreatePlaylistAsync(NewPlaylistName, null, cancellationToken);
    }

    public Task CreatePlaylistWithTrackAsync(
        string name,
        TrackRowViewModel track,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        return CreatePlaylistAsync(name, track, cancellationToken);
    }

    private async Task CreatePlaylistAsync(
        string name,
        TrackRowViewModel? initialTrack,
        CancellationToken cancellationToken,
        bool importInitialTrack = false)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            Track? initialSource = initialTrack?.Source;
            if (importInitialTrack && initialSource is not null)
            {
                initialSource = await EnsureTrackInLibraryAsync(initialSource, cancellationToken);
                await RefreshLibraryFacetsAsync(cancellationToken);
                await RefreshLibrarySourcesAsync(cancellationToken);
            }
            Playlist playlist = initialTrack is null
                ? await playlistRepository.CreateAsync(name, cancellationToken)
                : await playlistRepository.CreateWithTrackAsync(name, initialSource!.Id, cancellationToken);
            NewPlaylistName = string.Empty;
            await RefreshPlaylistsAsync(cancellationToken, playlist.Id);
            SetActiveView(LibraryViewKind.Playlist);
            SetSort(TrackSortField.PlaylistOrder);
            IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
            ReplaceTracks(tracks);
            if (initialTrack is not null)
            {
                SelectedTrack = Tracks.FirstOrDefault(track => track.Source.Id == initialSource!.Id);
            }
            StatusMessage = initialTrack is null
                ? Loc.Format("Playlist.Created", playlist.Name)
                : Loc.Format("Playlist.CreatedWith", playlist.Name, initialTrack.Title);
        });
    }

    public async Task AddSelectedTrackToPlaylistAsync(
        CancellationToken cancellationToken = default)
    {
        if (PlaylistTarget is not { } playlist)
        {
            return;
        }

        await AddSelectedTrackToPlaylistAsync(playlist, cancellationToken);
    }

    public async Task AddSelectedTrackToPlaylistAsync(
        PlaylistFilterViewModel playlist,
        CancellationToken cancellationToken = default)
    {
        if (SelectedTrack is not { } selected || playlist.Id is not Guid playlistId || playlist.IsSmart)
        {
            return;
        }

        string playlistName = playlist.Name;
        await RunBusyAsync(async () =>
        {
            await playlistRepository.AddTrackAsync(playlistId, selected.Source.Id, cancellationToken);
            await RefreshPlaylistsAsync(cancellationToken);
            IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
            ReplaceTracks(tracks);
            StatusMessage = Loc.Format("Playlist.Added", selected.Title, playlistName);
        });
    }

    public async Task RemoveSelectedTrackFromPlaylistAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedPlaylist?.IsSmart == true || activeView != LibraryViewKind.Playlist ||
            SelectedTrack is not { } selected || SelectedPlaylist?.Id is not Guid playlistId)
        {
            return;
        }

        string playlistName = SelectedPlaylist.Name;
        await RunBusyAsync(async () =>
        {
            await playlistRepository.RemoveTrackAsync(
                playlistId,
                selected.Source.Id,
                cancellationToken);
            await RefreshPlaylistsAsync(cancellationToken);
            IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
            ReplaceTracks(tracks);
            StatusMessage = Loc.Format("Playlist.RemovedFrom", selected.Title, playlistName);
        });
    }

    public async Task RenameSelectedPlaylistAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (activeView != LibraryViewKind.Playlist ||
            SelectedPlaylist?.Id is not Guid playlistId || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await playlistRepository.RenameAsync(playlistId, name, cancellationToken);
            await RefreshPlaylistsAsync(cancellationToken, playlistId);
            StatusMessage = Loc.Format("Playlist.Renamed", name.Trim());
        });
    }

    public async Task DeleteSelectedPlaylistAsync(CancellationToken cancellationToken = default)
    {
        if (activeView != LibraryViewKind.Playlist || SelectedPlaylist?.Id is not Guid playlistId)
        {
            return;
        }

        string name = SelectedPlaylist.Name;
        await RunBusyAsync(async () =>
        {
            await playlistRepository.DeleteAsync(playlistId, cancellationToken);
            SelectedPlaylist = null;
            await RefreshPlaylistsAsync(cancellationToken);
            SetActiveView(LibraryViewKind.AllMusic);
            SetSort(TrackSortField.Default);
            IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
            ReplaceTracks(tracks);
            StatusMessage = Loc.Format("Playlist.Deleted", name);
        });
    }

    public async Task MoveSelectedPlaylistTrackAsync(
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (activeView != LibraryViewKind.Playlist ||
            SelectedPlaylist?.Id is not Guid playlistId || SelectedTrack is not { } selected)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await playlistRepository.MoveTrackAsync(
                playlistId,
                selected.Source.Id,
                offset,
                cancellationToken);
            SetSort(TrackSortField.PlaylistOrder);
            IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
            ReplaceTracks(tracks);
            SelectedTrack = Tracks.FirstOrDefault(item => item.Source.Id == selected.Source.Id);
            StatusMessage = offset < 0
                ? Loc.Format("Playlist.MovedUp", selected.Title)
                : Loc.Format("Playlist.MovedDown", selected.Title);
        });
    }

    public async Task ReorderPlaylistTrackAsync(
        Guid trackId,
        Guid targetTrackId,
        bool insertAfter,
        CancellationToken cancellationToken = default)
    {
        if (activeView != LibraryViewKind.Playlist ||
            SelectedPlaylist?.Id is not Guid playlistId || trackId == targetTrackId)
        {
            return;
        }

        TrackRowViewModel? moved = Tracks.FirstOrDefault(item => item.Source.Id == trackId);
        await RunBusyAsync(async () =>
        {
            List<Guid> orderedTrackIds = Tracks.Select(item => item.Source.Id).ToList();
            orderedTrackIds.Remove(trackId);
            int targetIndex = orderedTrackIds.IndexOf(targetTrackId);
            if (targetIndex < 0)
            {
                return;
            }

            orderedTrackIds.Insert(insertAfter ? targetIndex + 1 : targetIndex, trackId);
            await playlistRepository.ReorderTracksAsync(
                playlistId,
                orderedTrackIds,
                cancellationToken);
            SetSort(TrackSortField.PlaylistOrder);
            IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
            ReplaceTracks(tracks);
            SelectedTrack = Tracks.FirstOrDefault(item => item.Source.Id == trackId);
            StatusMessage = moved is null
                ? Loc.Get("Playlist.OrderUpdated")
                : Loc.Format("Playlist.Moved", moved.Title);
        });
    }

    public void SortTracks(TrackSortField requestedField)
    {
        if (requestedField is TrackSortField.Default or TrackSortField.PlaylistOrder)
        {
            return;
        }

        bool descending = sortField == requestedField && !sortDescending;
        SetSort(requestedField, descending);
        ReplaceTracks(Tracks.Select(item => item.Source));
        StatusMessage = descending ? Loc.Format("Sort.Descending", SortFieldName(requestedField)) : Loc.Format("Sort.Ascending", SortFieldName(requestedField));
    }

    public void SetTrackColumnVisibility(TrackColumnKind kind, bool isVisible)
    {
        TrackColumnViewModel? column = TrackColumns.FirstOrDefault(item => item.Kind == kind);
        if (column is null || column.IsVisible == isVisible ||
            (kind == TrackColumnKind.Title && !isVisible))
        {
            return;
        }

        if (!isVisible && VisibleTrackColumns.Count == 1)
        {
            return;
        }

        column.IsVisible = isVisible;
        RefreshVisibleTrackColumns();
    }

    public void RestoreDefaultTrackColumns()
    {
        TrackColumns.Clear();
        foreach (TrackColumnViewModel column in CreateDefaultTrackColumns())
        {
            TrackColumns.Add(column);
        }

        RefreshVisibleTrackColumns();
    }

    public void MoveTrackColumn(TrackColumnKind kind, int offset)
    {
        int index = TrackColumns.ToList().FindIndex(item => item.Kind == kind);
        int target = index + offset;
        if (index < 0 || target < 0 || target >= TrackColumns.Count)
        {
            return;
        }

        TrackColumns.Move(index, target);
        RefreshVisibleTrackColumns();
    }

    public void MoveTrackColumn(TrackColumnKind source, TrackColumnKind target)
    {
        int sourceIndex = TrackColumns.ToList().FindIndex(item => item.Kind == source);
        int targetIndex = TrackColumns.ToList().FindIndex(item => item.Kind == target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        TrackColumns.Move(sourceIndex, targetIndex);
        RefreshVisibleTrackColumns();
    }

    public void ResizeTrackColumn(TrackColumnKind kind, double width)
    {
        TrackColumnViewModel? column = TrackColumns.FirstOrDefault(item => item.Kind == kind);
        if (column is not null && double.IsFinite(width))
        {
            column.Width = Math.Clamp(width, 42, 600);
        }
    }

    public Task PersistTrackColumnLayoutAsync(CancellationToken cancellationToken = default)
    {
        return PersistPreferencesAsync(cancellationToken);
    }

    public async Task SetSelectedTrackRatingAsync(
        int rating,
        CancellationToken cancellationToken = default)
    {
        if (SelectedTrack is not { } selected || rating is < 0 or > 5)
        {
            return;
        }

        await repository.SetRatingAsync(selected.Source.Id, rating, cancellationToken);
        Track[] tracks = Tracks
            .Select(item => item.Source.Id == selected.Source.Id
                ? item.Source with { Rating = rating }
                : item.Source)
            .ToArray();
        ReplaceTracks(tracks);
        await RefreshSmartPlaylistsAsync(cancellationToken);
        SelectedTrack = Tracks.FirstOrDefault(item => item.Source.Id == selected.Source.Id);
        StatusMessage = rating == 0
            ? Loc.Format("Rating.Cleared", selected.Title)
            : Loc.Plural("Rating.Set", rating, selected.Title);
    }

    public Task TogglePlaybackAsync(CancellationToken cancellationToken = default) =>
        RunPlaybackOperation(() => TogglePlaybackCoreAsync(cancellationToken));

    private async Task TogglePlaybackCoreAsync(CancellationToken cancellationToken)
    {
        if (changingTrack) return;
        try
        {
            PlaybackSnapshot current = playbackEngine.Snapshot;
            if (IsRadioView && SelectedRadioId is { } stationId && current.Track?.Id != stationId)
            { await PlayRadioStationAsync(stationId, cancellationToken); return; }
            if (IsRadioPlayback && (IsRadioView || SelectedTrack is null))
            {
                if (current.Status == PlaybackStatus.Playing || radioConnectionRequested) await StopPlaybackAsync(cancellationToken);
                else await PlayRadioStationAsync(playingRadioStation!.Id, cancellationToken);
                return;
            }
            if (current.Track is null && playQueue.HasPending)
            {
                await PlayNextQueuedAsync(cancellationToken);
                return;
            }
            // The hidden Music selection must not hijack transport while Podcasts or Audio CD is showing.
            if (SelectedTrack is null || !CanSearchLibrary)
            {
                if (current.Track is null)
                {
                    await PlayNextQueuedAsync(cancellationToken);
                    return;
                }

                if (current.Status == PlaybackStatus.Playing)
                {
                    await playbackEngine.PauseAsync(cancellationToken);
                }
                else
                {
                    await playbackEngine.PlayAsync(cancellationToken);
                }

                return;
            }

            if (current.Track?.Id != SelectedTrack.Source.Id)
            {
                playbackQueue.Replace(Tracks.Select(row => row.Source));
                playbackQueue.TrySelect(SelectedTrack.Source.Id);
                await PlayCurrentAsync(cancellationToken);
            }
            else if (current.Status == PlaybackStatus.Playing)
            {
                await playbackEngine.PauseAsync(cancellationToken);
            }
            else
            {
                await playbackEngine.PlayAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException)
        {
            ReportPlaybackFailure(playbackEngine.Snapshot.Track, exception.Message, exception, null);
        }
    }

    public Task MoveNextAsync(CancellationToken cancellationToken = default) =>
        RunPlaybackOperation(() => NavigatePlaybackAsync(previous: false, cancellationToken));

    public Task StopPlaybackAsync(CancellationToken cancellationToken = default) =>
        RunPlaybackOperation(() => StopPlaybackCoreAsync(cancellationToken));

    private async Task StopPlaybackCoreAsync(CancellationToken cancellationToken)
    {
        playbackStopGeneration++;
        if (!CanStop)
        {
            return;
        }

        try
        {
            await FlushPodcastProgressAsync(cancellationToken);
            await playbackEngine.StopAsync(cancellationToken);
            if (IsRadioPlayback)
            {
                radioConnectionRequested = false;
                ApplyPlaybackSnapshot(playbackEngine.Snapshot);
                StatusMessage = Loc.Get("Playback.RadioStopped");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException)
        {
            ReportPlaybackFailure(playbackEngine.Snapshot.Track, exception.Message, exception, null);
        }
    }

    public Task MovePreviousAsync(CancellationToken cancellationToken = default) =>
        RunPlaybackOperation(() => NavigatePlaybackAsync(previous: true, cancellationToken));

    public async Task ToggleShuffleAsync(CancellationToken cancellationToken = default)
    {
        playbackQueue.SetShuffle(!playbackQueue.ShuffleEnabled);
        ShuffleButtonText = playbackQueue.ShuffleEnabled ? Loc.Get("Playback.ShuffleOn") : Loc.Get("Playback.ShuffleOff");
        NotifyNavigationState();
        await PersistPreferencesAsync(cancellationToken);
    }

    public Task CycleRepeatAsync(CancellationToken cancellationToken = default) =>
        SetRepeatModeAsync(playbackQueue.RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        }, cancellationToken);

    private async Task SetRepeatModeAsync(RepeatMode mode, CancellationToken cancellationToken)
    {
        playbackQueue.SetRepeatMode(mode);
        RepeatButtonText = mode switch
        {
            RepeatMode.All => Loc.Get("Playback.RepeatAll"),
            RepeatMode.One => Loc.Get("Playback.RepeatOne"),
            _ => Loc.Get("Playback.RepeatOff")
        };
        await PersistPreferencesAsync(cancellationToken);
    }

    public async Task ToggleMuteAsync(CancellationToken cancellationToken = default)
    {
        if (playbackEngine.Snapshot.Track is null)
        {
            return;
        }

        try
        {
            preferredMuted = !playbackEngine.Snapshot.IsMuted;
            await playbackEngine.SetMutedAsync(preferredMuted, cancellationToken);
            await PersistPreferencesAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
        }
    }

    public async Task SeekAsync(double seconds, CancellationToken cancellationToken = default)
    {
        if (!CanSeekPlayback) return;
        try
        {
            await SeekPlaybackAsync(TimeSpan.FromSeconds(seconds), cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
        }
    }

    public void BeginSeek()
    {
        isUserSeeking = true;
    }

    public void EndSeek()
    {
        isUserSeeking = false;
        PositionSeconds = playbackEngine.Snapshot.Position.TotalSeconds;
    }

    public async Task ChangeVolumeAsync(double value, CancellationToken cancellationToken = default)
    {
        Volume = Math.Round(
            Math.Clamp(value, 0, 1),
            2,
            MidpointRounding.AwayFromZero);
        try
        {
            if (playbackEngine.Snapshot.Track is not null)
            {
                await playbackEngine.SetVolumeAsync(Volume, cancellationToken);
            }

            await PersistPreferencesAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private Task RunBusyAsync(Func<Task> action) => RunLibraryOperation(() => RunBusyCoreAsync(action));

    private async Task RunBusyCoreAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.Get("Common.OperationCancelled");
        }
        catch (Exception exception)
        {
            StatusMessage = Loc.Format("Common.OperationFailed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadPreferencesAsync(CancellationToken cancellationToken)
    {
        if (preferencesLoaded)
        {
            return;
        }

        ApplicationPreferences preferences = await settingsStore.LoadAsync(cancellationToken);
        ShowDesktopNotifications = preferences.ShowDesktopNotifications;
        podcastAutoRefresh = preferences.PodcastAutoRefresh;
        onlineLookup = preferences.OnlineLookup;
        playbackEngineKind = preferences.PlaybackEngine;
        language = preferences.Language;
        languageAtStart ??= language;
        radioCatalogEnabled = preferences.RadioCatalogEnabled;
        soundFontPath = preferences.SoundFontPath;
        soundFontAtStart ??= soundFontPath;
        podcastRate = preferences.PodcastPlaybackRate is 1.0 or 1.25 or 1.5 or 2.0 ? preferences.PodcastPlaybackRate : 1.0;
        podcastFolderState = new(preferences.PodcastDownloadFolder, preferences.PodcastDownloadFolderHistory ?? []);
        AppearanceTheme = preferences.Theme;
        ApplyAppearanceTheme();
        ApplyTrackColumnPreferences(preferences.TrackColumns);
        IsPlayQueueVisible = preferences.IsPlayQueueVisible;
        PlayQueueWidth = preferences.PlayQueueWidth;
        SidebarWidth = preferences.SidebarWidth;
        SidebarPlaylistsFraction = preferences.SidebarPlaylistsFraction;
        SidebarNowPlayingHeight = preferences.SidebarNowPlayingHeight;
        SidebarLibraryFraction = preferences.SidebarLibraryFraction;
        IsSidebarVisible = preferences.IsSidebarVisible;
        Volume = preferences.Volume;
        preferredMuted = preferences.IsMuted;
        await RestoreEqualizerAsync(preferences.Equalizer, cancellationToken);
        playbackQueue.SetShuffle(preferences.ShuffleEnabled);
        playbackQueue.SetRepeatMode(preferences.RepeatMode);
        ShuffleButtonText = preferences.ShuffleEnabled ? Loc.Get("Playback.ShuffleOn") : Loc.Get("Playback.ShuffleOff");
        RepeatButtonText = preferences.RepeatMode switch
        {
            RepeatMode.All => Loc.Get("Playback.RepeatAll"),
            RepeatMode.One => Loc.Get("Playback.RepeatOne"),
            _ => Loc.Get("Playback.RepeatOff")
        };
        MuteButtonText = preferredMuted ? Loc.Get("Playback.Unmute") : Loc.Get("Playback.Mute");
        if (preferences.PlayQueue is { Count: > 0 } entries)
        {
            IReadOnlyList<Track> library = await repository.ListAsync(cancellationToken);
            Dictionary<Guid, Track> libraryTracks = library.ToDictionary(track => track.Id);
            playQueue.Restore(entries.Select(entry =>
                libraryTracks.TryGetValue(entry.Track.Id, out Track? track)
                    ? entry with { Track = track }
                    : entry));
        }
        RefreshPlayQueue();
        preferencesLoaded = true;
    }

    public async Task FlushPreferencesAsync()
    {
        Task pending;
        do
        {
            pending = pendingPreferencesSave;
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // Shutdown still drains later writes after a cancelled control save.
            }
        }
        while (pending != pendingPreferencesSave);
    }

    private ApplicationPreferences BuildPreferences(PodcastFolderState folder)
    {
        return new(
            Volume,
            preferredMuted,
            playbackQueue.ShuffleEnabled,
            playbackQueue.RepeatMode,
            TrackColumns
                .Select(column => new TrackColumnPreference(
                    column.Kind.ToString(),
                    column.IsVisible,
                    column.Width))
                .ToArray(),
            IsPlayQueueVisible,
            PlayQueueWidth,
            // Tracks on a disc cannot be restored next session, so they are not saved with the queue.
            playQueue.Items.Where(entry => !DiscLocation.TryParse(entry.Track.Location, out _, out _)).ToArray(),
            AppearanceTheme,
            ShowDesktopNotifications,
            folder.Folder,
            folder.History,
            podcastRate,
            podcastAutoRefresh,
            onlineLookup,
            IsSidebarVisible,
            SidebarWidth,
            SidebarPlaylistsFraction,
            SidebarNowPlayingHeight,
            SidebarLibraryFraction,
            equalizer,
            playbackEngineKind,
            language,
            radioCatalogEnabled,
            soundFontPath);
    }

    private Task PersistPreferencesAsync(CancellationToken cancellationToken)
    {
        if (!preferencesLoaded)
        {
            return Task.CompletedTask;
        }

        ApplicationPreferences preferences = BuildPreferences(podcastFolderState);
        pendingPreferencesSave = SavePreferencesAsync(
            pendingPreferencesSave, preferences, cancellationToken);
        return pendingPreferencesSave;
    }

    private async Task SavePreferencesAsync(
        Task previousSave,
        ApplicationPreferences preferences,
        CancellationToken cancellationToken)
    {
        // A cancelled control operation must not prevent later queue changes saving.
        try
        {
            await previousSave;
        }
        catch
        {
            // Earlier writes reported their own outcome; a failed folder transaction must not block later saves.
        }

        try
        {
            // The snapshot was taken when the change was accepted; the folder is whatever has been committed by now.
            PodcastFolderState folder = podcastFolderState;
            await settingsStore.SaveAsync(preferences with
            {
                PodcastDownloadFolder = folder.Folder,
                PodcastDownloadFolderHistory = folder.History
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            StatusMessage = Loc.Format("Playback.SaveFailed", exception.Message);
        }
    }

    private async Task RefreshPlaylistsAsync(
        CancellationToken cancellationToken,
        Guid? selectPlaylistId = null)
    {
        Guid? selectedId = selectPlaylistId ?? SelectedPlaylist?.Id;
        Guid? targetId = PlaylistTarget?.Id;
        IReadOnlyList<Playlist> storedPlaylists = await playlistRepository
            .ListAsync(cancellationToken);

        Playlists.Clear();
        EditablePlaylists.Clear();
        Playlists.Add(new PlaylistFilterViewModel(null, Loc.Get("Library.AllMusic"), null));
        foreach (Playlist playlist in storedPlaylists)
        {
            PlaylistFilterViewModel item = new(
                playlist.Id,
                playlist.Name,
                playlist.TrackIds.Count,
                playlist.Definition);
            Playlists.Add(item);
            EditablePlaylists.Add(item);
        }

        SelectedPlaylist = Playlists.FirstOrDefault(item => item.Id == selectedId)
            ?? Playlists[0];
        PlaylistTarget = EditablePlaylists.FirstOrDefault(item => item.Id == targetId && !item.IsSmart)
            ?? EditablePlaylists.FirstOrDefault(item => !item.IsSmart);
    }

    private async Task RefreshLibraryFacetsAsync(CancellationToken cancellationToken)
    {
        string? genreName = SelectedGenre?.Name;
        string? artistName = SelectedArtist?.Name;
        (string? Artist, string? Title)? albumSelection = SelectedAlbum is null
            ? null
            : (SelectedAlbum.Artist, SelectedAlbum.Title);
        IReadOnlyList<GenreSummary> storedGenres = await libraryBrowser
            .ListGenresAsync(cancellationToken);

        Genres.Clear();
        Genres.Add(new GenreFilterViewModel(null, storedGenres.Sum(item => item.TrackCount), true));
        foreach (GenreSummary genre in storedGenres)
        {
            Genres.Add(new GenreFilterViewModel(genre.Name, genre.TrackCount, false));
        }

        SelectedGenre = Genres.FirstOrDefault(item =>
            string.Equals(item.Name, genreName, StringComparison.OrdinalIgnoreCase)) ?? Genres[0];
        await RefreshArtistAlbumFacetsAsync(
            artistName,
            albumSelection,
            cancellationToken);
    }

    private async Task RefreshArtistAlbumFacetsAsync(
        string? selectedArtistName,
        (string? Artist, string? Title)? selectedAlbum,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ArtistSummary> storedArtists = await libraryBrowser
            .ListArtistsAsync(SelectedGenre?.Name, cancellationToken);
        Artists.Clear();
        Artists.Add(new ArtistFilterViewModel(
            null,
            storedArtists.Sum(item => item.TrackCount),
            true));
        foreach (ArtistSummary artist in storedArtists)
        {
            Artists.Add(new ArtistFilterViewModel(artist.Name, artist.TrackCount, false));
        }

        SelectedArtist = Artists.FirstOrDefault(item =>
            string.Equals(item.Name, selectedArtistName, StringComparison.OrdinalIgnoreCase)) ?? Artists[0];
        await RefreshAlbumFacetsAsync(selectedAlbum, cancellationToken);
    }

    private async Task RefreshAlbumFacetsAsync(
        (string? Artist, string? Title)? selectedAlbum,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AlbumSummary> storedAlbums = await libraryBrowser.ListAlbumsAsync(
            SelectedGenre?.Name,
            SelectedArtist?.Name,
            cancellationToken);
        Albums.Clear();
        Albums.Add(new AlbumFilterViewModel(
            null,
            null,
            storedAlbums.Sum(item => item.TrackCount),
            true));
        foreach (AlbumSummary album in storedAlbums)
        {
            Albums.Add(new AlbumFilterViewModel(
                album.Artist,
                album.Title,
                album.TrackCount,
                false));
        }

        SelectedAlbum = Albums.FirstOrDefault(item =>
            selectedAlbum is { } selected &&
            string.Equals(item.Artist, selected.Artist, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Title, selected.Title, StringComparison.OrdinalIgnoreCase)) ?? Albums[0];
    }

    private async Task ResetBrowserFiltersAsync(CancellationToken cancellationToken)
    {
        SelectedGenre = Genres.FirstOrDefault();
        await RefreshArtistAlbumFacetsAsync(null, null, cancellationToken);
    }

    private async Task RefreshLibrarySourcesAsync(
        CancellationToken cancellationToken,
        string? selectPath = null)
    {
        string? selectedPath = selectPath ?? SelectedLibrarySource?.Path;
        IReadOnlyList<string> storedSources = await sourceRepository.ListAsync(cancellationToken);
        LibrarySources.Clear();
        foreach (string source in storedSources)
        {
            LibrarySources.Add(new LibrarySourceViewModel(source));
        }

        SelectedLibrarySource = LibrarySources.FirstOrDefault(item =>
            string.Equals(item.Path, selectedPath, StringComparison.Ordinal));
    }

    private void SetActiveView(LibraryViewKind view)
    {
        activeView = view;
        overlay = OverlayView.None;
        OnPropertyChanged(nameof(IsColumnBrowserVisible)); OnPropertyChanged(nameof(IsPodcastsView)); OnPropertyChanged(nameof(IsDiscView)); OnPropertyChanged(nameof(IsDevicesView)); OnPropertyChanged(nameof(IsAudiobooksView)); OnPropertyChanged(nameof(CanSearchLibrary));
        OnPropertyChanged(nameof(IsRadioView)); OnPropertyChanged(nameof(IsMusicView));
        OnPropertyChanged(nameof(SearchPlaceholder)); OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanRemoveFromPlaylist));
        OnPropertyChanged(nameof(CanEditPlaylist));
        OnPropertyChanged(nameof(CanMovePlaylistTrack));
        OnPropertyChanged(nameof(CanReorderPlaylist));
    }

    private Task<IReadOnlyList<Track>> QueryVisibleTracksAsync(CancellationToken cancellationToken)
    {
        return activeView switch
        {
            LibraryViewKind.Playlist when SelectedPlaylist?.Id is Guid playlistId =>
                playlistRepository.GetTracksAsync(playlistId, SearchQuery, cancellationToken),
            _ => libraryBrowser.GetTracksAsync(
                SelectedGenre?.Name,
                SelectedAlbum?.Title is not null
                    ? SelectedAlbum.Artist
                    : SelectedArtist?.Name,
                SelectedAlbum?.Title,
                SearchQuery,
                cancellationToken)
        };
    }

    private void EnsureTrackColumns()
    {
        if (TrackColumns.Count > 0)
        {
            return;
        }

        foreach (TrackColumnViewModel column in CreateDefaultTrackColumns())
        {
            TrackColumns.Add(column);
        }
        RefreshVisibleTrackColumns(rebuildRows: false);
    }

    private static IEnumerable<TrackColumnViewModel> CreateDefaultTrackColumns()
    {
        return
        [
            new(TrackColumnKind.Title, Loc.Get("Column.Title"), 250, true),
            new(TrackColumnKind.TrackNumber, "#", 44, true),
            new(TrackColumnKind.Duration, Loc.Get("Column.Time"), 58, true),
            new(TrackColumnKind.Artist, Loc.Get("Column.Artist"), 170, true),
            new(TrackColumnKind.Album, Loc.Get("Column.Album"), 190, true),
            new(TrackColumnKind.Genre, Loc.Get("Column.Genre"), 135, true),
            new(TrackColumnKind.Rating, Loc.Get("Column.Rating"), 92, true),
            new(TrackColumnKind.Year, Loc.Get("Column.Year"), 58, true),
            new(TrackColumnKind.AlbumArtist, Loc.Get("Column.AlbumArtist"), 170, false),
            new(TrackColumnKind.BitRate, Loc.Get("Column.BitRate"), 82, false),
            new(TrackColumnKind.BeatsPerMinute, Loc.Get("Column.Bpm"), 60, false),
            new(TrackColumnKind.Channels, Loc.Get("Column.Channels"), 82, false),
            new(TrackColumnKind.Comment, Loc.Get("Column.Comment"), 190, false),
            new(TrackColumnKind.Composer, Loc.Get("Column.Composer"), 170, false),
            new(TrackColumnKind.DateAdded, Loc.Get("Column.DateAdded"), 130, false),
            new(TrackColumnKind.DateModified, Loc.Get("Column.DateModified"), 130, false),
            new(TrackColumnKind.Description, Loc.Get("Column.Description"), 220, false),
            new(TrackColumnKind.DiscNumber, Loc.Get("Column.DiscNumber"), 90, false),
            new(TrackColumnKind.EpisodeNumber, Loc.Get("Column.EpisodeNumber"), 110, false),
            new(TrackColumnKind.Location, Loc.Get("Column.Location"), 320, false),
            new(TrackColumnKind.Keywords, Loc.Get("Column.Keywords"), 170, false),
            new(TrackColumnKind.LastPlayed, Loc.Get("Column.LastPlayed"), 130, false),
            new(TrackColumnKind.LastSkipped, Loc.Get("Column.LastSkipped"), 130, false),
            new(TrackColumnKind.PlayCount, Loc.Get("Column.PlayCount"), 82, false),
            new(TrackColumnKind.SampleRate, Loc.Get("Column.SampleRate"), 92, false),
            new(TrackColumnKind.SeasonNumber, Loc.Get("Column.SeasonNumber"), 105, false),
            new(TrackColumnKind.ShowName, Loc.Get("Column.ShowName"), 170, false),
            new(TrackColumnKind.FileSize, Loc.Get("Column.Size"), 80, false),
            new(TrackColumnKind.SkipCount, Loc.Get("Column.SkipCount"), 82, false),
            new(TrackColumnKind.Source, Loc.Get("Column.Source"), 150, false)
        ];
    }

    private void ApplyTrackColumnPreferences(
        IReadOnlyList<TrackColumnPreference>? preferences)
    {
        if (preferences is null || preferences.Count == 0)
        {
            return;
        }

        Dictionary<TrackColumnKind, TrackColumnViewModel> columns = TrackColumns
            .ToDictionary(column => column.Kind);
        List<TrackColumnViewModel> ordered = [];
        foreach (TrackColumnPreference preference in preferences)
        {
            if (!Enum.TryParse(preference.Kind, ignoreCase: true, out TrackColumnKind kind) ||
                !columns.Remove(kind, out TrackColumnViewModel? column))
            {
                continue;
            }

            column.IsVisible = preference.IsVisible;
            if (preference.Width is double width && double.IsFinite(width))
            {
                column.Width = Math.Clamp(width, 42, 600);
            }
            ordered.Add(column);
        }

        ordered.AddRange(TrackColumns.Where(column => columns.ContainsKey(column.Kind)));
        if (ordered.All(column => !column.IsVisible))
        {
            ordered[0].IsVisible = true;
        }

        TrackColumns.Clear();
        foreach (TrackColumnViewModel column in ordered)
        {
            TrackColumns.Add(column);
        }

        RefreshVisibleTrackColumns(rebuildRows: false);
    }

    private void RefreshVisibleTrackColumns(bool rebuildRows = true)
    {
        VisibleTrackColumns.Clear();
        foreach (TrackColumnViewModel column in TrackColumns.Where(item => item.IsVisible))
        {
            VisibleTrackColumns.Add(column);
        }

        if (rebuildRows && Tracks.Count > 0)
        {
            ReplaceTracks(Tracks.Select(item => item.Source).ToArray());
        }
    }

    private void ReplaceTracks(IEnumerable<Track> tracks, bool autoFitColumns = false)
    {
        EnsureTrackColumns();
        PlaybackSnapshot snapshot = playbackEngine.Snapshot;
        Guid? currentTrackId = snapshot.Track?.Id;
        Guid? selectedTrackId = SelectedTrack?.Source.Id;
        Track[] trackArray = ApplySort(tracks).ToArray();
        if (autoFitColumns)
        {
            AutoFitTrackColumns(trackArray);
        }
        bool isPlaylistTrack = activeView == LibraryViewKind.Playlist &&
            SelectedPlaylist is { Id: not null, IsSmart: false };
        bool canReorder = isPlaylistTrack && SearchQuery.Length == 0;
        Tracks.Clear();
        foreach (Track track in trackArray)
        {
            if (refreshedTracks.ContainsKey(track.Id) || playbackEngine.Snapshot.Track?.Id == track.Id)
                refreshedTracks[track.Id] = track;
            if (track.Metadata is not null) editedMetadata[track.Id] = TrackMetadata.FromTrack(track);
            TrackRowViewModel row = TrackRowViewModel.FromTrack(
                track,
                VisibleTrackColumns,
                isPlaylistTrack,
                canReorder);
            row.UpdatePlayback(snapshot);
            Tracks.Add(row);
        }

        if (currentTrackId is null)
        {
            playbackQueue.Replace(trackArray);
        }
        else if (!isPlayingQueuedTrack && playbackQueue.Current?.Id != currentTrackId)
        {
            playbackQueue.TrySelect(currentTrackId.Value);
        }

        SelectedTrack = selectedTrackId is Guid selectedId
            ? Tracks.FirstOrDefault(row => row.Source.Id == selectedId)
            : null;

        OnPropertyChanged(nameof(IsEmpty));
        NotifyNavigationState();
    }

    private void AutoFitTrackColumns(Track[] tracks)
    {
        foreach (TrackColumnViewModel column in TrackColumns)
        {
            double headerWidth = EstimateTextWidth(column.Header) + 52;
            double contentWidth = tracks.Length == 0
                ? 0
                : tracks.Max(track => EstimateTextWidth(
                    TrackRowViewModel.GetCellText(track, column.Kind))) + 18;
            double maximum = column.Kind == TrackColumnKind.Location ? 520 : 420;
            column.Width = Math.Clamp(
                Math.Ceiling(Math.Max(headerWidth, contentWidth)),
                42,
                maximum);
        }
    }

    private static double EstimateTextWidth(string value)
    {
        double width = 0;
        foreach (char character in value)
        {
            width += character switch
            {
                'i' or 'l' or 'I' or '1' or '.' or ',' or ':' or ';' => 3.5,
                'M' or 'W' or 'm' or 'w' or '@' => 9.5,
                '★' or '☆' => 11,
                _ when char.IsUpper(character) => 7.5,
                _ => 6.5
            };
        }

        return width;
    }

    private IEnumerable<Track> ApplySort(IEnumerable<Track> tracks)
    {
        StringComparer comparer = StringComparer.CurrentCultureIgnoreCase;
        return (sortField, sortDescending) switch
        {
            (TrackSortField.Default, _) when tracks.Any(track => track.Metadata is not null) => tracks
                .OrderBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortArtist) ? track.Artist : track.Metadata.SortArtist, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortAlbum) ? track.Album : track.Metadata.SortAlbum, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortTitle) ? track.Title : track.Metadata.SortTitle, comparer),
            (TrackSortField.Title, false) => tracks.OrderBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortTitle) ? track.Title : track.Metadata.SortTitle, comparer),
            (TrackSortField.Title, true) => tracks.OrderByDescending(track => string.IsNullOrWhiteSpace(track.Metadata?.SortTitle) ? track.Title : track.Metadata.SortTitle, comparer),
            (TrackSortField.Artist, false) => tracks
                .OrderBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortArtist) ? track.Artist : track.Metadata.SortArtist, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortAlbum) ? track.Album : track.Metadata.SortAlbum, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortTitle) ? track.Title : track.Metadata.SortTitle, comparer),
            (TrackSortField.Artist, true) => tracks
                .OrderByDescending(track => string.IsNullOrWhiteSpace(track.Metadata?.SortArtist) ? track.Artist : track.Metadata.SortArtist, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortAlbum) ? track.Album : track.Metadata.SortAlbum, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortTitle) ? track.Title : track.Metadata.SortTitle, comparer),
            (TrackSortField.Album, false) => tracks
                .OrderBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortAlbum) ? track.Album : track.Metadata.SortAlbum, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortTitle) ? track.Title : track.Metadata.SortTitle, comparer),
            (TrackSortField.Album, true) => tracks
                .OrderByDescending(track => string.IsNullOrWhiteSpace(track.Metadata?.SortAlbum) ? track.Album : track.Metadata.SortAlbum, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortTitle) ? track.Title : track.Metadata.SortTitle, comparer),
            (TrackSortField.Genre, false) => tracks
                .OrderBy(track => track.Genre, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortArtist) ? track.Artist : track.Metadata.SortArtist, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortAlbum) ? track.Album : track.Metadata.SortAlbum, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortTitle) ? track.Title : track.Metadata.SortTitle, comparer),
            (TrackSortField.Genre, true) => tracks
                .OrderByDescending(track => track.Genre, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortArtist) ? track.Artist : track.Metadata.SortArtist, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortAlbum) ? track.Album : track.Metadata.SortAlbum, comparer)
                .ThenBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortTitle) ? track.Title : track.Metadata.SortTitle, comparer),
            (TrackSortField.Rating, false) => tracks.OrderBy(track => track.Rating),
            (TrackSortField.Rating, true) => tracks.OrderByDescending(track => track.Rating),
            (TrackSortField.Year, false) => tracks.OrderBy(track => track.Year),
            (TrackSortField.Year, true) => tracks.OrderByDescending(track => track.Year),
            (TrackSortField.TrackNumber, false) => tracks.OrderBy(track => track.TrackNumber),
            (TrackSortField.TrackNumber, true) => tracks.OrderByDescending(track => track.TrackNumber),
            (TrackSortField.AlbumArtist, false) => tracks.OrderBy(track => string.IsNullOrWhiteSpace(track.Metadata?.SortAlbumArtist) ? track.AlbumArtist : track.Metadata.SortAlbumArtist, comparer),
            (TrackSortField.AlbumArtist, true) => tracks.OrderByDescending(track => string.IsNullOrWhiteSpace(track.Metadata?.SortAlbumArtist) ? track.AlbumArtist : track.Metadata.SortAlbumArtist, comparer),
            (TrackSortField.Location, false) => tracks.OrderBy(track => track.Location.AbsoluteUri, comparer),
            (TrackSortField.Location, true) => tracks.OrderByDescending(track => track.Location.AbsoluteUri, comparer),
            (TrackSortField.Duration, false) => tracks.OrderBy(track => track.Duration),
            (TrackSortField.Duration, true) => tracks.OrderByDescending(track => track.Duration),
            _ => ApplyAdditionalColumnSort(tracks, comparer)
        };
    }

    private IEnumerable<Track> ApplyAdditionalColumnSort(
        IEnumerable<Track> tracks,
        StringComparer comparer)
    {
        TrackColumnViewModel? column = TrackColumns.FirstOrDefault(item => item.SortField == sortField);
        if (column is null || sortField is TrackSortField.Default or TrackSortField.PlaylistOrder)
        {
            return tracks;
        }

        return (column.Kind, sortDescending) switch
        {
            (TrackColumnKind.BitRate, false) => tracks.OrderBy(track => track.BitRate),
            (TrackColumnKind.BitRate, true) => tracks.OrderByDescending(track => track.BitRate),
            (TrackColumnKind.BeatsPerMinute, false) => tracks.OrderBy(track => track.BeatsPerMinute),
            (TrackColumnKind.BeatsPerMinute, true) => tracks.OrderByDescending(track => track.BeatsPerMinute),
            (TrackColumnKind.Channels, false) => tracks.OrderBy(track => track.Channels),
            (TrackColumnKind.Channels, true) => tracks.OrderByDescending(track => track.Channels),
            (TrackColumnKind.DateAdded, false) => tracks.OrderBy(track => track.DateAdded),
            (TrackColumnKind.DateAdded, true) => tracks.OrderByDescending(track => track.DateAdded),
            (TrackColumnKind.DateModified, false) => tracks.OrderBy(track => track.DateModified),
            (TrackColumnKind.DateModified, true) => tracks.OrderByDescending(track => track.DateModified),
            (TrackColumnKind.DiscNumber, false) => tracks.OrderBy(track => track.DiscNumber),
            (TrackColumnKind.DiscNumber, true) => tracks.OrderByDescending(track => track.DiscNumber),
            (TrackColumnKind.EpisodeNumber, false) => tracks.OrderBy(track => track.EpisodeNumber),
            (TrackColumnKind.EpisodeNumber, true) => tracks.OrderByDescending(track => track.EpisodeNumber),
            (TrackColumnKind.LastPlayed, false) => tracks.OrderBy(track => track.LastPlayed),
            (TrackColumnKind.LastPlayed, true) => tracks.OrderByDescending(track => track.LastPlayed),
            (TrackColumnKind.LastSkipped, false) => tracks.OrderBy(track => track.LastSkipped),
            (TrackColumnKind.LastSkipped, true) => tracks.OrderByDescending(track => track.LastSkipped),
            (TrackColumnKind.PlayCount, false) => tracks.OrderBy(track => track.PlayCount),
            (TrackColumnKind.PlayCount, true) => tracks.OrderByDescending(track => track.PlayCount),
            (TrackColumnKind.SampleRate, false) => tracks.OrderBy(track => track.SampleRate),
            (TrackColumnKind.SampleRate, true) => tracks.OrderByDescending(track => track.SampleRate),
            (TrackColumnKind.SeasonNumber, false) => tracks.OrderBy(track => track.SeasonNumber),
            (TrackColumnKind.SeasonNumber, true) => tracks.OrderByDescending(track => track.SeasonNumber),
            (TrackColumnKind.FileSize, false) => tracks.OrderBy(track => track.FileSize),
            (TrackColumnKind.FileSize, true) => tracks.OrderByDescending(track => track.FileSize),
            (TrackColumnKind.SkipCount, false) => tracks.OrderBy(track => track.SkipCount),
            (TrackColumnKind.SkipCount, true) => tracks.OrderByDescending(track => track.SkipCount),
            (_, true) => tracks.OrderByDescending(
                track => TrackRowViewModel.GetCellText(track, column.Kind), comparer),
            _ => tracks.OrderBy(
                track => TrackRowViewModel.GetCellText(track, column.Kind), comparer)
        };
    }

    private void SetSort(TrackSortField field, bool descending = false)
    {
        sortField = field;
        sortDescending = descending;
        foreach (TrackColumnViewModel column in TrackColumns)
        {
            column.SortIndicator = column.SortField == field
                ? descending ? "▼" : "▲"
                : string.Empty;
        }
        OnPropertyChanged(nameof(TitleHeaderText));
        OnPropertyChanged(nameof(ArtistHeaderText));
        OnPropertyChanged(nameof(AlbumHeaderText));
        OnPropertyChanged(nameof(GenreHeaderText));
        OnPropertyChanged(nameof(DurationHeaderText));
    }

    private static string SortFieldName(TrackSortField field) => (field switch
    {
        TrackSortField.Title => Loc.Get("Column.Title"),
        TrackSortField.Artist => Loc.Get("Column.Artist"),
        TrackSortField.Album => Loc.Get("Column.Album"),
        TrackSortField.Genre => Loc.Get("Column.Genre"),
        TrackSortField.Duration => Loc.Get("Column.Time"),
        _ => field.ToString()
    }).ToLower(System.Globalization.CultureInfo.CurrentCulture);

    private string FormatSortHeader(string label, TrackSortField field)
    {
        return sortField == field
            ? $"{label} {(sortDescending ? '▼' : '▲')}"
            : label;
    }

    private static string FormatPlaybackTime(TimeSpan value)
    {
        TimeSpan nonNegative = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return nonNegative.TotalHours >= 1
            ? nonNegative.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : nonNegative.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private string FormatResultCount(int count)
    {
        if (SearchQuery.Length > 0)
        {
            return Loc.Plural("Result.Matching", count);
        }

        return activeView == LibraryViewKind.Playlist && SelectedPlaylist is not null
            ? Loc.Plural("Result.InPlaylist", count, SelectedPlaylist.Name)
            : SelectedAlbum?.Title is { } album
                ? Loc.Plural("Result.OnAlbum", count, album)
                : SelectedArtist?.Name is { } artist
                    ? Loc.Plural("Result.ByArtist", count, artist)
                    : SelectedGenre?.Name is { } genre
                        ? Loc.Plural("Result.InGenre", count, genre)
                        : Loc.Plural("Result.InLibrary", count);
    }

    /// <summary>Shows an application-wide error summary from any thread without letting it mask later status updates.</summary>
    public void ShowUnexpectedError(string message) => Dispatcher.UIThread.Post(() => StatusMessage = message);

    private void OnPlaybackStateChanged(object? sender, PlaybackSnapshot playbackSnapshot)
    {
        ObservePodcastProgress(playbackSnapshot);
        ObservePlaybackRequestBoundary(playbackSnapshot);
        Dispatcher.UIThread.Post(() =>
        {
            if (!isListening)
            {
                return;
            }
            PlaybackSnapshot current = playbackEngine.Snapshot;
            ApplyPlaybackSnapshot(current);
            UpdateDesktopNotification(current);
            ProcessPlaybackEnd(playbackSnapshot);
        });
    }

    private void OnLibrarySynchronized(object? sender, LibrarySynchronizedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (!isListening || IsBusy)
            {
                return;
            }

            await LoadAsync();
            if (!isListening) return;
            CancelArtwork();
            UpdateArtwork(playbackEngine.Snapshot.Track);
            if (eventArgs.Result.Removed > 0)
            {
                StatusMessage = Loc.Format("Library.Updated.Removed", eventArgs.Result.Removed);
            }
            else if (eventArgs.Result.Issues.Count > 0)
            {
                StatusMessage = Loc.Format("Library.Updated.Unreadable", eventArgs.Result.Issues.Count);
            }
        });
    }

    private void ApplyPlaybackSnapshot(PlaybackSnapshot playbackSnapshot)
    {
        UpdateArtwork(playbackSnapshot.Track is { } artTrack && refreshedTracks.TryGetValue(artTrack.Id, out Track? refreshed) ? refreshed : playbackSnapshot.Track);
        (Guid? TrackId, PlaybackStatus Status) indicator =
            playbackSnapshot.Status is PlaybackStatus.Playing or PlaybackStatus.Paused
                ? (playbackSnapshot.Track?.Id, playbackSnapshot.Status)
                : (null, PlaybackStatus.Stopped);
        if (playbackIndicator != indicator)
        {
            foreach (TrackRowViewModel row in Tracks)
            {
                row.UpdatePlayback(playbackSnapshot);
            }
            UpdatePlayQueueIndicators(playbackSnapshot);
            playbackIndicator = indicator;
        }

        if (IsRadioPlayback && playbackSnapshot.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted) radioConnectionRequested = false;
        PlaybackButtonText = IsRadioPlayback && radioConnectionRequested ? Loc.Get("Playback.StopRadio") : playbackSnapshot.Status == PlaybackStatus.Playing ? Loc.Get("Playback.Pause") : Loc.Get("Playback.Play");
        IsPlaybackPlaying = playbackSnapshot.Status == PlaybackStatus.Playing;
        NowPlaying = playbackSnapshot.Track is null
            ? Loc.Get("Playback.Nothing")
            : editedMetadata.TryGetValue(playbackSnapshot.Track.Id, out TrackMetadata? edited)
                ? $"{edited.Title} — {edited.Artist}"
                : $"{playbackSnapshot.Track.Title} — {playbackSnapshot.Track.Artist}";
        if (!isUserSeeking)
        {
            PositionSeconds = playbackSnapshot.Position.TotalSeconds;
        }
        DurationSeconds = Math.Max(1, playbackSnapshot.Track?.Duration.TotalSeconds ?? 1);
        Volume = playbackSnapshot.Volume;
        MuteButtonText = playbackSnapshot.IsMuted ? Loc.Get("Playback.Unmute") : Loc.Get("Playback.Mute");
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanMute));
        NotifyRadioPlaybackState();
        if (playbackSnapshot.Error is not null)
        {
            if (playbackSnapshot.Status == PlaybackStatus.Faulted) ReportPlaybackFailure(playbackSnapshot.Track, playbackSnapshot.Error, null, playbackSnapshot.ErrorDetails);
            else StatusMessage = playbackSnapshot.Error;
            PodcastPlayingChanged?.Invoke(this, EventArgs.Empty);
        }
        if (playbackSnapshot.Status is PlaybackStatus.Stopped or PlaybackStatus.Ended or PlaybackStatus.Faulted) ClearSessionActivity();
        PodcastPlayingChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task PlayCurrentAsync(CancellationToken cancellationToken)
    {
        if (playbackQueue.Current is not { } track) return;
        isPlayingQueuedTrack = false;
        playQueue.CompleteCurrent();
        RefreshPlayQueue();
        await PlayTrackAsync(track, cancellationToken);
    }

    private async Task PlayTrackAsync(Track track, CancellationToken cancellationToken, TimeSpan? initialPosition = null, bool propagateFailure = false)
    {
        long stopGeneration = playbackStopGeneration;
        playingRadioStation = null; radioConnectionRequested = false;
        changingTrack = true;
        CancelArtwork();
        NotifyNavigationState();
        try
        {
            SelectedTrack = Tracks.FirstOrDefault(row => row.Source.Id == track.Id);
            ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            await FlushPodcastProgressAsync(cancellationToken);
            ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            await PreparePodcastInputAsync(track, cancellationToken);
            ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            NoteSessionActivity(track);
            await playbackEngine.LoadAsync(track, cancellationToken);
            ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            await playbackEngine.SetVolumeAsync(Volume, cancellationToken);
            ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            await playbackEngine.SetMutedAsync(preferredMuted, cancellationToken);
            ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            if (initialPosition is { } position && position > TimeSpan.Zero)
            {
                await playbackEngine.SeekAsync(position, cancellationToken);
                ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            }
            await playbackEngine.PlayAsync(cancellationToken);
            DateTimeOffset playedAt = DateTimeOffset.UtcNow;
            await repository.RecordPlaybackStartedAsync(
                track.Id,
                playedAt,
                cancellationToken);
            UpdateTrackStatistics(
                track.Id,
                source => source with
                {
                    PlayCount = source.PlayCount + 1,
                    LastPlayed = playedAt
                });
            await RefreshSmartPlaylistsAsync(cancellationToken);
        }
        catch (Exception exception) when (!propagateFailure && exception is (InvalidOperationException or DllNotFoundException))
        {
            ReportPlaybackFailure(track, exception.Message, exception, null);
        }
        finally
        {
            CompletePlaybackTransition();
        }
    }

    private Task AdvanceAfterEndAsync() => RunPlaybackOperation(AdvanceAfterEndCoreAsync);

    private async Task AdvanceAfterEndCoreAsync()
    {
        if (changingTrack || !isListening || playbackEngine.Snapshot.Status != PlaybackStatus.Ended) return;
        if (IsRadioPlayback) { StatusMessage = Loc.Get("Playback.RadioEnded"); NotifyNavigationState(); return; }
        if (isPlayingQueuedTrack && playQueue.Current is not null && playbackQueue.RepeatMode == RepeatMode.One)
        {
            await PlayTrackAsync(playQueue.Current.Track, CancellationToken.None);
            return;
        }
        if (await PlayNextQueuedAsync(CancellationToken.None)) return;
        if (!playbackQueue.TryAdvanceAfterEnd())
        {
            StatusMessage = Loc.Get("Playback.EndOfQueue");
            NotifyNavigationState();
            return;
        }
        await PlayCurrentAsync(CancellationToken.None);
    }

    private async Task RecordManualSkipAsync(CancellationToken cancellationToken)
    {
        PlaybackSnapshot snapshot = playbackEngine.Snapshot;
        if (snapshot.Track is null ||
            snapshot.Status is PlaybackStatus.Stopped or PlaybackStatus.Ended)
        {
            return;
        }

        DateTimeOffset skippedAt = DateTimeOffset.UtcNow;
        await repository.RecordSkippedAsync(
            snapshot.Track.Id,
            skippedAt,
            cancellationToken);
        UpdateTrackStatistics(
            snapshot.Track.Id,
            source => source with
            {
                SkipCount = source.SkipCount + 1,
                LastSkipped = skippedAt
            });
        await RefreshSmartPlaylistsAsync(cancellationToken);
    }

    private void UpdateTrackStatistics(Guid trackId, Func<Track, Track> update)
    {
        int index = Tracks.ToList().FindIndex(row => row.Source.Id == trackId);
        if (index < 0)
        {
            return;
        }

        TrackRowViewModel previous = Tracks[index];
        TrackRowViewModel updated = TrackRowViewModel.FromTrack(
            update(previous.Source),
            VisibleTrackColumns,
            previous.IsPlaylistTrack,
            previous.CanReorder);
        updated.UpdatePlayback(playbackEngine.Snapshot);
        bool wasSelected = SelectedTrack?.Source.Id == trackId;
        Tracks[index] = updated;
        if (wasSelected)
        {
            SelectedTrack = updated;
        }
    }

    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(CanImportRadioPlaylist));
        OnPropertyChanged(nameof(CanManageRadio)); OnPropertyChanged(nameof(CanEditRadioStation));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }
}

public sealed partial class TrackRowViewModel(
    Track source,
    string title,
    string artist,
    string album,
    string genre,
    string duration,
    IReadOnlyList<TrackCellViewModel> cells,
    bool isPlaylistTrack,
    bool canReorder) : ObservableObject
{
    public Track Source { get; } = source;
    public string Title { get; } = title;
    public string Artist { get; } = artist;
    public string Album { get; } = album;
    public string Genre { get; } = genre;
    public string Duration { get; } = duration;
    public IReadOnlyList<TrackCellViewModel> Cells { get; } = cells;
    public bool IsPlaylistTrack { get; } = isPlaylistTrack;
    public bool CanReorder { get; } = canReorder;

    /// <summary>
    /// The row's spoken name. The row template has several children, so a screen reader cannot derive a name from it and
    /// falls back to this method; without it Narrator announces the type name.
    /// </summary>
    public override string ToString() => string.Join(", ",
        new[] { Title, Artist, Album, Duration }.Where(part => !string.IsNullOrWhiteSpace(part)));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaying))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(HasPlaybackIndicator))]
    [NotifyPropertyChangedFor(nameof(PlaybackIndicatorText))]
    private PlaybackStatus playbackState;

    public bool IsPlaying => PlaybackState == PlaybackStatus.Playing;
    public bool IsPaused => PlaybackState == PlaybackStatus.Paused;
    public bool HasPlaybackIndicator => IsPlaying || IsPaused;
    public string PlaybackIndicatorText => IsPlaying ? Loc.Get("Playback.Playing") : IsPaused ? Loc.Get("Playback.Paused") : string.Empty;

    public void UpdatePlayback(PlaybackSnapshot snapshot)
    {
        PlaybackState = snapshot.Track?.Id == Source.Id &&
            snapshot.Status is PlaybackStatus.Playing or PlaybackStatus.Paused
                ? snapshot.Status
                : PlaybackStatus.Stopped;
    }

    public static TrackRowViewModel FromTrack(
        Track track,
        IEnumerable<TrackColumnViewModel> columns,
        bool isPlaylistTrack,
        bool canReorder)
    {
        string duration = track.Duration.TotalHours >= 1
            ? track.Duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : track.Duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        TrackCellViewModel[] cells = columns
            .Select(column => new TrackCellViewModel(
                GetCellText(track, column.Kind, duration),
                column))
            .ToArray();
        return new TrackRowViewModel(
            track,
            track.Title,
            track.Artist,
            track.Album,
            string.IsNullOrWhiteSpace(track.Genre) ? Loc.Get("Common.Unknown") : track.Genre,
            duration,
            cells,
            isPlaylistTrack,
            canReorder);
    }

    public static string GetCellText(Track track, TrackColumnKind kind)
    {
        string duration = track.Duration.TotalHours >= 1
            ? track.Duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : track.Duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        return GetCellText(track, kind, duration);
    }

    private static string GetCellText(Track track, TrackColumnKind kind, string duration)
    {
        return kind switch
        {
            TrackColumnKind.Title => track.Title,
            TrackColumnKind.TrackNumber => track.TrackNumber > 0 ? track.TrackNumber.ToString(CultureInfo.InvariantCulture) : string.Empty,
            TrackColumnKind.Duration => duration,
            TrackColumnKind.Artist => track.Artist,
            TrackColumnKind.Album => track.Album,
            TrackColumnKind.Genre => string.IsNullOrWhiteSpace(track.Genre) ? Loc.Get("Common.Unknown") : track.Genre,
            TrackColumnKind.Rating => track.Rating == 0
                ? "☆☆☆☆☆"
                : $"{new string('★', track.Rating)}{new string('☆', 5 - track.Rating)}",
            TrackColumnKind.Year => track.Year > 0 ? track.Year.ToString(CultureInfo.InvariantCulture) : string.Empty,
            TrackColumnKind.AlbumArtist => string.IsNullOrWhiteSpace(track.AlbumArtist)
                ? track.Artist
                : track.AlbumArtist,
            TrackColumnKind.BitRate => track.BitRate > 0
                ? $"{track.BitRate.ToString(CultureInfo.CurrentCulture)} kbps"
                : string.Empty,
            TrackColumnKind.BeatsPerMinute => track.BeatsPerMinute > 0
                ? track.BeatsPerMinute.ToString("0.#", CultureInfo.CurrentCulture)
                : string.Empty,
            TrackColumnKind.Channels => track.Channels > 0
                ? track.Channels.ToString(CultureInfo.CurrentCulture)
                : string.Empty,
            TrackColumnKind.Comment => track.Comment,
            TrackColumnKind.Composer => track.Composer,
            TrackColumnKind.DateAdded => FormatDate(track.DateAdded),
            TrackColumnKind.DateModified => FormatDate(track.DateModified),
            TrackColumnKind.Description => track.Description,
            TrackColumnKind.DiscNumber => FormatPositiveNumber(track.DiscNumber),
            TrackColumnKind.EpisodeNumber => FormatPositiveNumber(track.EpisodeNumber),
            TrackColumnKind.Location => track.Location.IsFile
                ? track.Location.LocalPath
                : track.Location.AbsoluteUri,
            TrackColumnKind.Keywords => track.Keywords,
            TrackColumnKind.LastPlayed => FormatDate(track.LastPlayed),
            TrackColumnKind.LastSkipped => FormatDate(track.LastSkipped),
            TrackColumnKind.PlayCount => track.PlayCount.ToString(CultureInfo.CurrentCulture),
            TrackColumnKind.SampleRate => track.SampleRate > 0
                ? $"{track.SampleRate.ToString("N0", CultureInfo.CurrentCulture)} Hz"
                : string.Empty,
            TrackColumnKind.SeasonNumber => FormatPositiveNumber(track.SeasonNumber),
            TrackColumnKind.ShowName => track.ShowName,
            TrackColumnKind.FileSize => track.FileSize > 0
                ? FormatFileSize(track.FileSize)
                : string.Empty,
            TrackColumnKind.SkipCount => track.SkipCount.ToString(CultureInfo.CurrentCulture),
            TrackColumnKind.Source => track.Source,
            _ => string.Empty
        };
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;
    }

    private static string FormatPositiveNumber(int value)
    {
        return value > 0 ? value.ToString(CultureInfo.CurrentCulture) : string.Empty;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }
}

public sealed record TrackCellViewModel(
    string Text,
    TrackColumnViewModel Column);

public sealed partial class TrackColumnViewModel(
    TrackColumnKind kind,
    string header,
    double initialWidth,
    bool isVisible) : ObservableObject
{
    public TrackColumnKind Kind { get; } = kind;

    public string Header { get; } = header;

    [ObservableProperty]
    private double width = initialWidth;

    [ObservableProperty]
    private string sortIndicator = string.Empty;

    [ObservableProperty]
    private double dragOpacity = 1;

    [ObservableProperty]
    private double dropIndicatorOpacity;

    [ObservableProperty]
    private bool isVisible = isVisible;

    public TrackSortField SortField => Kind switch
    {
        TrackColumnKind.Title => TrackSortField.Title,
        TrackColumnKind.TrackNumber => TrackSortField.TrackNumber,
        TrackColumnKind.Duration => TrackSortField.Duration,
        TrackColumnKind.Artist => TrackSortField.Artist,
        TrackColumnKind.Album => TrackSortField.Album,
        TrackColumnKind.Genre => TrackSortField.Genre,
        TrackColumnKind.Rating => TrackSortField.Rating,
        TrackColumnKind.Year => TrackSortField.Year,
        TrackColumnKind.AlbumArtist => TrackSortField.AlbumArtist,
        TrackColumnKind.Location => TrackSortField.Location,
        TrackColumnKind.BitRate => TrackSortField.BitRate,
        TrackColumnKind.BeatsPerMinute => TrackSortField.BeatsPerMinute,
        TrackColumnKind.Channels => TrackSortField.Channels,
        TrackColumnKind.Comment => TrackSortField.Comment,
        TrackColumnKind.Composer => TrackSortField.Composer,
        TrackColumnKind.DateAdded => TrackSortField.DateAdded,
        TrackColumnKind.DateModified => TrackSortField.DateModified,
        TrackColumnKind.Description => TrackSortField.Description,
        TrackColumnKind.DiscNumber => TrackSortField.DiscNumber,
        TrackColumnKind.EpisodeNumber => TrackSortField.EpisodeNumber,
        TrackColumnKind.Keywords => TrackSortField.Keywords,
        TrackColumnKind.LastPlayed => TrackSortField.LastPlayed,
        TrackColumnKind.LastSkipped => TrackSortField.LastSkipped,
        TrackColumnKind.PlayCount => TrackSortField.PlayCount,
        TrackColumnKind.SampleRate => TrackSortField.SampleRate,
        TrackColumnKind.SeasonNumber => TrackSortField.SeasonNumber,
        TrackColumnKind.ShowName => TrackSortField.ShowName,
        TrackColumnKind.FileSize => TrackSortField.FileSize,
        TrackColumnKind.SkipCount => TrackSortField.SkipCount,
        TrackColumnKind.Source => TrackSortField.Source,
        _ => TrackSortField.Default
    };
}

public sealed record PlaylistFilterViewModel(Guid? Id, string Name, int? TrackCount, SmartPlaylistDefinition? Definition = null)
{
    public bool IsSmart => Definition is not null;
    public string DisplayName => Definition is { Rules.Count: 0 } ? $"◇ {Name} (unavailable)"
        : TrackCount is int count ? $"{(IsSmart ? "◇ " : "")}{Name} ({count})" : Name;
}

public sealed record GenreFilterViewModel(string? Name, int TrackCount, bool IsAll)
{
    public string DisplayName => IsAll
        ? Loc.Format("Common.AllCount", TrackCount)
        : Loc.Format("Common.NameCount", string.IsNullOrWhiteSpace(Name) ? Loc.Get("Common.Unknown") : Name, TrackCount);
}

public sealed record ArtistFilterViewModel(string? Name, int TrackCount, bool IsAll)
{
    public string DisplayName => IsAll
        ? Loc.Format("Common.AllCount", TrackCount)
        : Loc.Format("Common.NameCount", string.IsNullOrWhiteSpace(Name) ? Loc.Get("Common.Unknown") : Name, TrackCount);
}

public sealed record AlbumFilterViewModel(
    string? Artist,
    string? Title,
    int TrackCount,
    bool IsAll)
{
    public string DisplayName => IsAll
        ? Loc.Format("Common.AllCount", TrackCount)
        : Loc.Format("Common.NameCount", string.IsNullOrWhiteSpace(Title) ? Loc.Get("Common.Unknown") : Title, TrackCount);
}

public sealed record LibrarySourceViewModel(string Path)
{
    public string DisplayName => System.IO.Path.GetFileName(Path.TrimEnd(
        System.IO.Path.DirectorySeparatorChar,
        System.IO.Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
        ? name
        : Path;

    // Spoken name of the folder row (a record's generated ToString would read out "LibrarySourceViewModel { Path = ... }").
    public override string ToString() => DisplayName == Path ? Path : $"{DisplayName}, {Path}";
}

public enum LibraryViewKind
{
    AllMusic,
    Playlist,
    Radio
}

public enum TrackSortField
{
    Default,
    PlaylistOrder,
    Title,
    Artist,
    Album,
    Genre,
    Rating,
    Year,
    TrackNumber,
    AlbumArtist,
    Location,
    Duration,
    BitRate,
    BeatsPerMinute,
    Channels,
    Comment,
    Composer,
    DateAdded,
    DateModified,
    Description,
    DiscNumber,
    EpisodeNumber,
    Keywords,
    LastPlayed,
    LastSkipped,
    PlayCount,
    SampleRate,
    SeasonNumber,
    ShowName,
    FileSize,
    SkipCount,
    Source
}

public enum TrackColumnKind
{
    Title,
    TrackNumber,
    Duration,
    Artist,
    Album,
    Genre,
    Rating,
    Year,
    AlbumArtist,
    Location,
    BitRate,
    BeatsPerMinute,
    Channels,
    Comment,
    Composer,
    DateAdded,
    DateModified,
    Description,
    DiscNumber,
    EpisodeNumber,
    Keywords,
    LastPlayed,
    LastSkipped,
    PlayCount,
    SampleRate,
    SeasonNumber,
    ShowName,
    FileSize,
    SkipCount,
    Source
}
