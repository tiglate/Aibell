using Avalonia;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Aibell.Desktop.Localization;
using Aibell.Desktop.ViewModels;
using Aibell.Desktop.Services;
using Aibell.Application.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace Aibell.Desktop;

public sealed partial class MainWindow : Window, IDisposable
{
    private static readonly DataFormat<TrackRowViewModel> TrackRowDataFormat =
        DataFormat.CreateInProcessFormat<TrackRowViewModel>("aibell-playlist-track-row");
    private static readonly TrackColumnKind[] ColumnMenuOrder =
    [
        TrackColumnKind.Album,
        TrackColumnKind.AlbumArtist,
        TrackColumnKind.Artist,
        TrackColumnKind.BitRate,
        TrackColumnKind.BeatsPerMinute,
        TrackColumnKind.Channels,
        TrackColumnKind.Comment,
        TrackColumnKind.Composer,
        TrackColumnKind.DateAdded,
        TrackColumnKind.DateModified,
        TrackColumnKind.Description,
        TrackColumnKind.DiscNumber,
        TrackColumnKind.EpisodeNumber,
        TrackColumnKind.Location,
        TrackColumnKind.Genre,
        TrackColumnKind.Keywords,
        TrackColumnKind.LastPlayed,
        TrackColumnKind.LastSkipped,
        TrackColumnKind.PlayCount,
        TrackColumnKind.Rating,
        TrackColumnKind.SampleRate,
        TrackColumnKind.SeasonNumber,
        TrackColumnKind.ShowName,
        TrackColumnKind.FileSize,
        TrackColumnKind.SkipCount,
        TrackColumnKind.Source,
        TrackColumnKind.Duration,
        TrackColumnKind.TrackNumber,
        TrackColumnKind.Year
    ];
    private readonly MainWindowViewModel viewModel;
    private readonly IDesktopFilePicker filePicker;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private CancellationTokenSource? libraryViewCancellation;
    private bool isSeekingFromPointer;
    private bool isChangingVolumeFromPointer;
    private TrackColumnViewModel? resizingTrackColumn;
    private TrackColumnViewModel? pendingTrackColumnDrag;
    private bool isTrackColumnDragging;
    private double trackColumnDragStartX;
    private double resizePointerStart;
    private double resizeColumnStart;
    private bool disposed;
    private bool closeReady;
    private bool closeInProgress;

    public MainWindow()
        : this(App.Services.GetRequiredService<MainWindowViewModel>(), App.Services.GetService<IMediaSession>(), podcastViewModelFactory: App.Services.GetService<Func<Aibell.Application.Podcasts.IPodcastPlaybackHandoff, PodcastsViewModel>>(),
            audiobooksViewModelFactory: App.Services.GetService<Func<Aibell.Application.Audiobooks.IAudiobookPlaybackHandoff, AudiobooksViewModel>>(),
            discViewModelFactory: App.Services.GetService<Func<Aibell.Application.Discs.IDiscPlaybackHandoff, DiscViewModel>>(),
            artworkWindowFactory: App.Services.GetService<Func<Aibell.Domain.Media.Track, ArtworkWindow>>(),
            deviceSyncViewModelFactory: App.Services.GetService<Func<Aibell.Application.Devices.IDeviceTrackSource, DeviceSyncViewModel>>())
    {
    }

    public MainWindow(MainWindowViewModel viewModel, IMediaSession? mediaSession = null, IDesktopFilePicker? filePicker = null,
        Func<Aibell.Application.Podcasts.IPodcastPlaybackHandoff, PodcastsViewModel>? podcastViewModelFactory = null,
        Func<Aibell.Application.Audiobooks.IAudiobookPlaybackHandoff, AudiobooksViewModel>? audiobooksViewModelFactory = null,
        Func<Aibell.Application.Discs.IDiscPlaybackHandoff, DiscViewModel>? discViewModelFactory = null,
        Func<Aibell.Domain.Media.Track, ArtworkWindow>? artworkWindowFactory = null,
        Func<Aibell.Application.Devices.IDeviceTrackSource, DeviceSyncViewModel>? deviceSyncViewModelFactory = null)
    {
        this.viewModel = viewModel;
        this.podcastViewModelFactory = podcastViewModelFactory;
        this.audiobooksViewModelFactory = audiobooksViewModelFactory;
        this.discViewModelFactory = discViewModelFactory;
        this.artworkWindowFactory = artworkWindowFactory;
        this.deviceSyncViewModelFactory = deviceSyncViewModelFactory;
        this.mediaSession = mediaSession;
        this.filePicker = filePicker ?? new AvaloniaFilePicker();
        InitializeComponent();
        // Without a title bar from the window manager Avalonia would draw a light one that ignores the dark theme.
        if (Services.TitleBarPolicy.ShouldHideTitleBar(Environment.GetEnvironmentVariable)) WindowDecorations = WindowDecorations.None;
        AudiobooksPane.Picker = this.filePicker;
        RadioStationList.AddHandler(KeyDownEvent, RadioStationKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        viewModel.RadioFilterChanged += RadioFilterChanged;
        AddHandler(KeyDownEvent, TrackPropertiesKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, PreferencesKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, LibraryKeyDown, RoutingStrategies.Tunnel);
        PositionSeekSurface.AddHandler(
            PointerPressedEvent,
            BeginSeek,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        PositionSeekSurface.AddHandler(
            PointerReleasedEvent,
            SeekReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        PositionSeekSurface.AddHandler(
            PointerMovedEvent,
            SeekMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        VolumeSurface.AddHandler(
            PointerPressedEvent,
            BeginVolumeChange,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        VolumeSurface.AddHandler(
            PointerReleasedEvent,
            EndVolumeChange,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        VolumeSurface.AddHandler(
            PointerMovedEvent,
            MoveVolume,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        TrackColumnHeaders.AddHandler(
            PointerMovedEvent,
            TrackColumnHeaderMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        TrackColumnHeaders.AddHandler(
            PointerReleasedEvent,
            TrackColumnHeaderReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        DataContext = viewModel;
        InitializePodcasts();
        InitializePlayQueue();
        InitializeArtwork();
        InitializeKeyboard();
        InitializeErrorReporting();
        InitializeTaskbar();
        viewModel.StartListening();
        Opened += OnOpened;
        // Shutdown is coordinated here: drain tracked operations before closing owned dialogs.
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.LoadAsync(lifetimeCancellation.Token));
        if (CanRunUserAction) mediaSessionStartup = StartMediaSessionAsync();
        startupFilesTaken = true;
        if (CanRunUserAction && StartupFiles.Count > 0) await RunUserActionAsync(() => viewModel.OpenFilesAsync(StartupFiles, lifetimeCancellation.Token));
    }

    private bool startupFilesTaken;

    /// <summary>Files named on the command line; played once the library has loaded.</summary>
    internal List<Uri> StartupFiles { get; } = [];

    /// <summary>A second launch of the application: bring this window forward and play what that launch was given.</summary>
    internal async void OpenFromAnotherLaunch(IReadOnlyList<Uri> files)
    {
        if (disposed || closeInProgress) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (files.Count == 0) return;
        if (!startupFilesTaken) StartupFiles.AddRange(files);
        else await RunUserActionAsync(() => viewModel.OpenFilesAsync(files, lifetimeCancellation.Token));
    }

    private async void ImportFolder(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(async () =>
        {
            Uri? selected = await filePicker.PickFolderAsync(this,
                new FolderPickerOpenOptions
                {
                    Title = Loc.Get("Picker.ImportFolder"),
                    AllowMultiple = false
                });

            string? path = selected is { IsFile: true } ? selected.LocalPath : null;
            if (path is not null && CanRunUserAction)
            {
                await viewModel.ImportAsync(path, lifetimeCancellation.Token);
            }
        });
    }

    private async void SearchChanged(object? sender, TextChangedEventArgs eventArgs)
    {
        MirrorRadioSearch();
        await RunLibraryViewActionAsync(async cancellationToken =>
        {
            await Task.Delay(250, cancellationToken);
            await viewModel.SearchAsync(SearchBox.Text ?? string.Empty, cancellationToken);
        });
    }

    private async void PlaylistChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (PlaylistList.SelectedItem is not PlaylistFilterViewModel playlist)
        {
            return;
        }

        if (ReferenceEquals(viewModel.SelectedPlaylist, playlist))
        {
            return;
        }

        await RunLibraryViewActionAsync(token => viewModel.SelectPlaylistAsync(playlist, token));
    }

    private async void ArtistChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (ArtistList.SelectedItem is not ArtistFilterViewModel artist)
        {
            return;
        }

        if (ReferenceEquals(viewModel.SelectedArtist, artist))
        {
            return;
        }

        await RunLibraryViewActionAsync(token => viewModel.SelectArtistAsync(artist, token));
    }

    private async void GenreChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (GenreList.SelectedItem is not GenreFilterViewModel genre)
        {
            return;
        }

        if (ReferenceEquals(viewModel.SelectedGenre, genre))
        {
            return;
        }

        await RunLibraryViewActionAsync(token => viewModel.SelectGenreAsync(genre, token));
    }

    private async void AlbumChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (AlbumList.SelectedItem is not AlbumFilterViewModel album)
        {
            return;
        }

        if (ReferenceEquals(viewModel.SelectedAlbum, album))
        {
            return;
        }

        await RunLibraryViewActionAsync(token => viewModel.SelectAlbumAsync(album, token));
    }

    private async void CreatePlaylist(object? sender, RoutedEventArgs eventArgs)
    {
        string? name = await ShowTextPromptAsync(
            Loc.Get("Dialog.CreatePlaylist"),
            Loc.Get("Dialog.PlaylistName"),
            string.Empty,
            Loc.Get("Dialog.Save"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        viewModel.NewPlaylistName = name;
        await RunUserActionAsync(() => viewModel.CreatePlaylistAsync(lifetimeCancellation.Token));
    }

    private async void SelectAllMusic(object? sender, RoutedEventArgs eventArgs)
    {
        // Returning from Podcasts, Audio CD, Devices or Audiobooks keeps the exact Music browser, sort and selection that were showing before.
        if (viewModel.TryReturnFromOverlayToMusic()) return;
        PlaylistFilterViewModel? allMusic = viewModel.Playlists.FirstOrDefault(item => item.Id is null);
        if (allMusic is null)
        {
            return;
        }

        PlaylistList.SelectedItem = null;
        await RunLibraryViewActionAsync(token => viewModel.SelectPlaylistAsync(allMusic, token));
    }

    private async void RescanLibrarySource(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.RescanSelectedLibrarySourceAsync(lifetimeCancellation.Token));
    }

    private async void RemoveLibrarySource(object? sender, RoutedEventArgs eventArgs)
    {
        if (viewModel.SelectedLibrarySource is not { } source)
        {
            return;
        }

        bool confirmed = await ShowConfirmationAsync(
            Loc.Get("Dialog.RemoveFolder"),
            Loc.Format("Dialog.RemoveFolderPrompt", source.Path),
            Loc.Get("Dialog.Remove"));
        if (confirmed)
        {
            await RunUserActionAsync(() => viewModel.RemoveSelectedLibrarySourceAsync(lifetimeCancellation.Token));
        }
    }

    private async void AddTrackToPlaylist(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.AddSelectedTrackToPlaylistAsync(lifetimeCancellation.Token));
    }

    private void PopulateTrackContextMenu(
        object? sender,
        System.ComponentModel.CancelEventArgs eventArgs)
    {
        if (sender is not ContextMenu contextMenu ||
            contextMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Classes.Contains("addToPlaylist")) is not { } parent)
        {
            return;
        }

        TrackRowViewModel? track = contextMenu.PlacementTarget?.DataContext as TrackRowViewModel
            ?? viewModel.SelectedTrack;
        if (contextMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Classes.Contains("viewMetadata")) is { } metadata)
        {
            metadata.Tag = track;
            metadata.IsEnabled = !viewModel.IsBusy && track is not null;
        }
        if (contextMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Classes.Contains("getArtwork")) is { } artwork)
        {
            artwork.Tag = track;
            string? reason = ArtworkUnavailableReason(track);
            artwork.IsEnabled = reason is null && !viewModel.IsBusy;
            ToolTip.SetTip(artwork, reason ?? Loc.Get("Artwork.MenuTip"));
        }
        parent.IsEnabled = !viewModel.IsBusy && track is not null;
        parent.ItemsSource = BuildAddToPlaylistItems(track);
    }

    // Shared by the track context menu and Edit > Add to Playlist.
    private List<Control> BuildAddToPlaylistItems(TrackRowViewModel? track)
    {
        List<Control> items = [];
        foreach (PlaylistFilterViewModel playlist in viewModel.EditablePlaylists.Where(item => !item.IsSmart))
        {
            MenuItem item = new()
            {
                Header = playlist.DisplayName,
                Tag = playlist,
                IsEnabled = !viewModel.IsBusy
            };
            item.Click += AddTrackToSpecificPlaylist;
            items.Add(item);
        }

        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = Loc.Get("Menu.NoPlaylists"), IsEnabled = false });
        }

        items.Add(new Separator());
        MenuItem create = new()
        {
            Header = Loc.Get("Menu.NewPlaylist"),
            Tag = track,
            IsEnabled = !viewModel.IsBusy && track is not null
        };
        create.Click += CreatePlaylistWithTrack;
        items.Add(create);
        return items;
    }

    private async void CreatePlaylistWithTrack(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: TrackRowViewModel track } || viewModel.IsBusy)
        {
            return;
        }

        string? name = await ShowTextPromptAsync(
            Loc.Get("Dialog.CreatePlaylist"),
            Loc.Get("Dialog.PlaylistName"),
            string.Empty,
            Loc.Get("Dialog.Create"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunUserActionAsync(() => viewModel.CreatePlaylistWithTrackAsync(name, track, lifetimeCancellation.Token));
    }

    private async void AddTrackToSpecificPlaylist(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is MenuItem { Tag: PlaylistFilterViewModel playlist })
        {
            await RunUserActionAsync(() => viewModel.AddSelectedTrackToPlaylistAsync(
                playlist,
                lifetimeCancellation.Token));
        }
    }

    private async void RemoveTrackFromPlaylist(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.RemoveSelectedTrackFromPlaylistAsync(lifetimeCancellation.Token));
    }

    private void PlaylistItemPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is Control control &&
            control.DataContext is PlaylistFilterViewModel playlist &&
            eventArgs.GetCurrentPoint(control).Properties.PointerUpdateKind ==
                PointerUpdateKind.RightButtonPressed)
        {
            PlaylistList.SelectedItem = playlist;
        }
    }

    private void FolderItemPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is Control control &&
            control.DataContext is LibrarySourceViewModel source &&
            eventArgs.GetCurrentPoint(control).Properties.PointerUpdateKind ==
                PointerUpdateKind.RightButtonPressed)
        {
            FolderList.SelectedItem = source;
        }
    }

    private void TrackItemPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is not Control control || control.DataContext is not TrackRowViewModel track)
        {
            return;
        }

        PointerPointProperties properties = eventArgs.GetCurrentPoint(control).Properties;
        if (properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            if (control.ContextMenu is { } menu) menu.PlacementTarget = control;
            if (TrackList.SelectedItems?.Contains(track) != true)
            {
                TrackList.SelectedItem = track;
            }
        }
    }

    private async void TrackDragHandlePressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is not Control control ||
            control.DataContext is not TrackRowViewModel draggedTrack ||
            !draggedTrack.CanReorder ||
            eventArgs.GetCurrentPoint(control).Properties.PointerUpdateKind !=
                PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        if (TrackList.SelectedItems?.Contains(draggedTrack) != true)
        {
            TrackList.SelectedItem = draggedTrack;
        }
        Aibell.Domain.Media.Track[] tracks = SelectedLibraryTracks();
        eventArgs.Handled = true;
        DataTransfer dragData = new();
        if (tracks.Length == 1)
        {
            dragData.Add(DataTransferItem.Create(TrackRowDataFormat, draggedTrack));
        }
        dragData.Add(DataTransferItem.Create(TrackBatchDataFormat, tracks));
        await DragDrop.DoDragDropAsync(eventArgs, dragData, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void TrackColumnHeaderPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is not Border control ||
            control.DataContext is not TrackColumnViewModel column ||
            eventArgs.GetCurrentPoint(control).Properties.PointerUpdateKind !=
                PointerUpdateKind.LeftButtonPressed ||
            eventArgs.GetPosition(control).X > control.Bounds.Width - 6)
        {
            return;
        }

        pendingTrackColumnDrag = column;
        isTrackColumnDragging = false;
        trackColumnDragStartX = eventArgs.GetPosition(TrackColumnHeaders).X;
        eventArgs.Handled = true;
        eventArgs.Pointer.Capture(TrackColumnHeaders);
    }

    private void TrackColumnHeaderMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (pendingTrackColumnDrag is not { } source ||
            !eventArgs.GetCurrentPoint(TrackColumnHeaders).Properties.IsLeftButtonPressed)
        {
            return;
        }

        double pointerX = eventArgs.GetPosition(TrackColumnHeaders).X;
        if (!isTrackColumnDragging && Math.Abs(pointerX - trackColumnDragStartX) < 5)
        {
            return;
        }

        if (!isTrackColumnDragging)
        {
            isTrackColumnDragging = true;
            source.DragOpacity = 0.55;
            TrackColumnHeaders.Cursor = new Cursor(StandardCursorType.SizeAll);
        }

        TrackColumnViewModel? target = FindTrackColumnAt(pointerX);
        foreach (TrackColumnViewModel column in viewModel.TrackColumns)
        {
            column.DropIndicatorOpacity = column == target && column != source ? 1 : 0;
        }

        if (target is not null && target != source)
        {
            viewModel.MoveTrackColumn(source.Kind, target.Kind);
        }

        eventArgs.Handled = true;
    }

    private async void TrackColumnHeaderReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (pendingTrackColumnDrag is not { } source)
        {
            return;
        }

        bool wasDragging = isTrackColumnDragging;
        pendingTrackColumnDrag = null;
        isTrackColumnDragging = false;
        source.DragOpacity = 1;
        foreach (TrackColumnViewModel column in viewModel.TrackColumns)
        {
            column.DropIndicatorOpacity = 0;
        }

        TrackColumnHeaders.Cursor = new Cursor(StandardCursorType.Arrow);
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
        if (wasDragging)
        {
            await RunUserActionAsync(() => viewModel.PersistTrackColumnLayoutAsync(lifetimeCancellation.Token));
        }
        else
        {
            viewModel.SortTracks(source.SortField);
        }
    }

    private TrackColumnViewModel? FindTrackColumnAt(double pointerX)
    {
        double currentX = 0;
        foreach (TrackColumnViewModel column in viewModel.VisibleTrackColumns)
        {
            currentX += column.Width;
            if (pointerX < currentX)
            {
                return column;
            }
        }

        return viewModel.VisibleTrackColumns.LastOrDefault();
    }

    private void TrackColumnResizePressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is not Control { DataContext: TrackColumnViewModel column } control ||
            eventArgs.GetCurrentPoint(control).Properties.PointerUpdateKind !=
                PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        resizingTrackColumn = column;
        resizePointerStart = eventArgs.GetPosition(this).X;
        resizeColumnStart = column.Width;
        eventArgs.Pointer.Capture(control);
        eventArgs.Handled = true;
    }

    private void TrackColumnResizeMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (resizingTrackColumn is null ||
            !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        double delta = eventArgs.GetPosition(this).X - resizePointerStart;
        viewModel.ResizeTrackColumn(resizingTrackColumn.Kind, resizeColumnStart + delta);
        eventArgs.Handled = true;
    }

    private async void TrackColumnResizeReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (resizingTrackColumn is null)
        {
            return;
        }

        resizingTrackColumn = null;
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
        await RunUserActionAsync(() => viewModel.PersistTrackColumnLayoutAsync(lifetimeCancellation.Token));
    }

    private void TrackDragOver(object? sender, DragEventArgs eventArgs)
    {
        if (sender is not Border border ||
            border.DataContext is not TrackRowViewModel target ||
            eventArgs.DataTransfer.TryGetValue(TrackRowDataFormat) is not { } source ||
            source.Source.Id == target.Source.Id ||
            !viewModel.CanReorderPlaylist)
        {
            eventArgs.DragEffects = DragDropEffects.None;
            return;
        }

        bool insertAfter = eventArgs.GetPosition(border).Y >= border.Bounds.Height / 2;
        border.BorderBrush = Avalonia.Media.Brushes.DodgerBlue;
        border.BorderThickness = insertAfter
            ? new Avalonia.Thickness(0, 0, 0, 2)
            : new Avalonia.Thickness(0, 2, 0, 0);
        eventArgs.DragEffects = DragDropEffects.Move;
        eventArgs.Handled = true;
    }

    private void TrackDragLeave(object? sender, DragEventArgs eventArgs)
    {
        if (sender is Border border)
        {
            ClearDropIndicator(border);
        }
    }

    private async void TrackDrop(object? sender, DragEventArgs eventArgs)
    {
        if (sender is not Border border ||
            border.DataContext is not TrackRowViewModel target ||
            eventArgs.DataTransfer.TryGetValue(TrackRowDataFormat) is not { } source ||
            source.Source.Id == target.Source.Id ||
            !viewModel.CanReorderPlaylist)
        {
            eventArgs.DragEffects = DragDropEffects.None;
            return;
        }

        bool insertAfter = eventArgs.GetPosition(border).Y >= border.Bounds.Height / 2;
        ClearDropIndicator(border);
        eventArgs.DragEffects = DragDropEffects.Move;
        eventArgs.Handled = true;
        await RunUserActionAsync(() => viewModel.ReorderPlaylistTrackAsync(
            source.Source.Id,
            target.Source.Id,
            insertAfter,
            lifetimeCancellation.Token));
    }

    private static void ClearDropIndicator(Border border)
    {
        border.BorderBrush = null;
        border.BorderThickness = new Avalonia.Thickness(0);
    }

    private async void RenamePlaylist(object? sender, RoutedEventArgs eventArgs)
    {
        if (viewModel.SelectedPlaylist is not { Id: not null } playlist)
        {
            return;
        }

        string? name = await ShowTextPromptAsync(
            Loc.Get("Dialog.RenamePlaylist"),
            Loc.Get("Dialog.PlaylistName"),
            playlist.Name,
            Loc.Get("Dialog.Rename"));
        if (!string.IsNullOrWhiteSpace(name))
        {
            await RunUserActionAsync(() => viewModel.RenameSelectedPlaylistAsync(name, lifetimeCancellation.Token));
        }
    }

    private async void DeletePlaylist(object? sender, RoutedEventArgs eventArgs)
    {
        if (viewModel.SelectedPlaylist is not { Id: not null } playlist)
        {
            return;
        }

        bool confirmed = await ShowConfirmationAsync(
            Loc.Get("Dialog.DeletePlaylist"),
            Loc.Format("Dialog.DeletePlaylistPrompt", playlist.Name));
        if (confirmed)
        {
            await RunUserActionAsync(() => viewModel.DeleteSelectedPlaylistAsync(lifetimeCancellation.Token));
        }
    }

    private async void MoveTrackUp(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.MoveSelectedPlaylistTrackAsync(-1, lifetimeCancellation.Token));
    }

    private async void MoveTrackDown(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.MoveSelectedPlaylistTrackAsync(1, lifetimeCancellation.Token));
    }

    private void SortByTitle(object? sender, RoutedEventArgs eventArgs)
    {
        viewModel.SortTracks(TrackSortField.Title);
    }

    private void SortByArtist(object? sender, RoutedEventArgs eventArgs)
    {
        viewModel.SortTracks(TrackSortField.Artist);
    }

    private void SortByAlbum(object? sender, RoutedEventArgs eventArgs)
    {
        viewModel.SortTracks(TrackSortField.Album);
    }

    private void SortByGenre(object? sender, RoutedEventArgs eventArgs)
    {
        viewModel.SortTracks(TrackSortField.Genre);
    }

    private void SortByDuration(object? sender, RoutedEventArgs eventArgs)
    {
        viewModel.SortTracks(TrackSortField.Duration);
    }

    private void SortTrackColumn(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: TrackColumnViewModel column })
        {
            viewModel.SortTracks(column.SortField);
        }
    }

    private async void SetTrackRating(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is MenuItem { Tag: string value } && int.TryParse(value, out int rating))
        {
            await RunUserActionAsync(() => viewModel.SetSelectedTrackRatingAsync(rating, lifetimeCancellation.Token));
        }
    }

    private void PopulateColumnMenu(
        object? sender,
        System.ComponentModel.CancelEventArgs eventArgs)
    {
        if (sender is not ContextMenu contextMenu)
        {
            return;
        }

        contextMenu.ItemsSource = BuildColumnMenuItems();
    }

    // Shared by the column-header context menu and View > Columns.
    private List<object> BuildColumnMenuItems()
    {
        List<object> items = [];
        foreach (TrackColumnKind kind in ColumnMenuOrder)
        {
            TrackColumnViewModel? column = viewModel.TrackColumns.FirstOrDefault(
                candidate => candidate.Kind == kind);
            if (column is null)
            {
                continue;
            }

            MenuItem item = new()
            {
                Header = GetColumnMenuLabel(column),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
                StaysOpenOnClick = true,
                Tag = column
            };
            item.Click += ToggleTrackColumn;
            items.Add(item);
        }

        items.Add(new Separator());
        MenuItem restore = new() { Header = Loc.Get("Menu.RestoreDefaults") };
        restore.Click += RestoreDefaultColumns;
        items.Add(restore);
        return items;
    }

    private static string GetColumnMenuLabel(TrackColumnViewModel column)
    {
        return column.Kind switch
        {
            TrackColumnKind.TrackNumber => Loc.Get("Column.TrackNumber"),
            _ => column.Header
        };
    }

    private async void ToggleTrackColumn(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: TrackColumnViewModel column } item)
        {
            return;
        }

        viewModel.SetTrackColumnVisibility(column.Kind, !column.IsVisible);
        item.IsChecked = column.IsVisible;
        await RunUserActionAsync(() => viewModel.PersistTrackColumnLayoutAsync(lifetimeCancellation.Token));
    }

    private async void RestoreDefaultColumns(object? sender, RoutedEventArgs eventArgs)
    {
        viewModel.RestoreDefaultTrackColumns();
        await RunUserActionAsync(() => viewModel.PersistTrackColumnLayoutAsync(lifetimeCancellation.Token));
    }

    private async Task<string?> ShowTextPromptAsync(
        string title,
        string label,
        string initialValue,
        string acceptText)
    {
        if (!CanRunUserAction) return null;
        TextBox input = new()
        {
            Text = initialValue,
            Height = 34,
            Padding = new Avalonia.Thickness(10, 0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            SelectionStart = 0,
            SelectionEnd = initialValue.Length
        };
        Window dialog = new()
        {
            Title = title,
            Width = 420,
            Height = 205,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = Avalonia.Controls.WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            [!BackgroundProperty] = new DynamicResourceExtension("DialogBrush")
        };
        Button accept = new()
        {
            Content = acceptText,
            MinWidth = 92,
            Height = 30,
            Classes = { "dialogAccept" },
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            IsEnabled = !string.IsNullOrWhiteSpace(initialValue)
        };
        Button cancel = new() { Content = Loc.Get("Dialog.Cancel"), MinWidth = 92, Height = 30 };
        Button close = new()
        {
            Classes = { "toolbar" },
            Content = new PathIcon
            {
                Width = 14,
                Height = 14,
                Foreground = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.Parse("#E6E8EA")),
                Data = Avalonia.Media.Geometry.Parse(
                    "M5,3 L12,10 L19,3 L21,5 L14,12 L21,19 L19,21 L12,14 L5,21 L3,19 L10,12 L3,5 Z")
            },
            Width = 40,
            Height = 40,
            Padding = new Avalonia.Thickness(0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0)
        };
        accept.Click += (_, _) => dialog.Close(input.Text?.Trim());
        cancel.Click += (_, _) => dialog.Close(null);
        close.Click += (_, _) => dialog.Close(null);
        input.TextChanged += (_, _) =>
            accept.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter && accept.IsEnabled)
            {
                dialog.Close(input.Text?.Trim());
                eventArgs.Handled = true;
            }
            else if (eventArgs.Key == Key.Escape)
            {
                dialog.Close(null);
                eventArgs.Handled = true;
            }
        };

        Border header = new()
        {
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#303438")),
            BorderBrush = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#D52268")),
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 3),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,40"),
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Margin = new Avalonia.Thickness(16, 0),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Foreground = Avalonia.Media.Brushes.White,
                        FontSize = 14,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold
                    },
                    close
                }
            }
        };
        Grid.SetColumn(close, 1);
        header.PointerPressed += (_, eventArgs) =>
        {
            if (eventArgs.GetCurrentPoint(header).Properties.PointerUpdateKind ==
                PointerUpdateKind.LeftButtonPressed)
            {
                dialog.BeginMoveDrag(eventArgs);
            }
        };

        Border body = new()
        {
            [Grid.RowProperty] = 1,
            Padding = new Avalonia.Thickness(20, 15),
            Child = new StackPanel
            {
                Spacing = 7,
                Children =
                {
                    new TextBlock
                    {
                        Text = label.ToUpperInvariant(),
                        [!ForegroundProperty] = new DynamicResourceExtension("SecondaryTextBrush"),
                        FontSize = 10,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold
                    },
                    input
                }
            }
        };
        Border footer = new()
        {
            [Grid.RowProperty] = 2,
            Padding = new Avalonia.Thickness(14, 10),
            [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush"),
            [!BorderBrushProperty] = new DynamicResourceExtension("PanelBorderBrush"),
            BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8,
                Children = { cancel, accept }
            }
        };
        dialog.Content = new Border
        {
            BorderBrush = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#17191B")),
            BorderThickness = new Avalonia.Thickness(1),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("44,*,51"),
                Children = { header, body, footer }
            }
        };
        input.AttachedToVisualTree += (_, _) => input.Focus();
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task<bool> ShowConfirmationAsync(
        string title,
        string message,
        string? confirmText = null)
    {
        Window dialog = new()
        {
            Title = title,
            Width = 440,
            Height = 190,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = Avalonia.Controls.WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            [!BackgroundProperty] = new DynamicResourceExtension("DialogBrush")
        };
        Button confirm = new()
        {
            Content = confirmText ?? Loc.Get("Dialog.Delete"),
            MinWidth = 92,
            Height = 30,
            Classes = { "dialogAccept" },
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        };
        Button cancel = new() { Content = Loc.Get("Dialog.Cancel"), MinWidth = 92, Height = 30 };
        Button close = new()
        {
            Classes = { "toolbar" },
            Content = new PathIcon
            {
                Width = 14,
                Height = 14,
                Foreground = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.Parse("#E6E8EA")),
                Data = Avalonia.Media.Geometry.Parse(
                    "M5,3 L12,10 L19,3 L21,5 L14,12 L21,19 L19,21 L12,14 L5,21 L3,19 L10,12 L3,5 Z")
            },
            Width = 40,
            Height = 40,
            Padding = new Avalonia.Thickness(0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0)
        };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        close.Click += (_, _) => dialog.Close(false);
        dialog.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter)
            {
                dialog.Close(true);
                eventArgs.Handled = true;
            }
            else if (eventArgs.Key == Key.Escape)
            {
                dialog.Close(false);
                eventArgs.Handled = true;
            }
        };

        Border header = new()
        {
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#303438")),
            BorderBrush = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#D52268")),
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 3),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,40"),
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Margin = new Avalonia.Thickness(16, 0),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Foreground = Avalonia.Media.Brushes.White,
                        FontSize = 14,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold
                    },
                    close
                }
            }
        };
        Grid.SetColumn(close, 1);
        header.PointerPressed += (_, eventArgs) =>
        {
            if (eventArgs.GetCurrentPoint(header).Properties.PointerUpdateKind ==
                PointerUpdateKind.LeftButtonPressed)
            {
                dialog.BeginMoveDrag(eventArgs);
            }
        };

        Border body = new()
        {
            [Grid.RowProperty] = 1,
            Padding = new Avalonia.Thickness(20, 14),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("38,*"),
                Children =
                {
                    new Border
                    {
                        Width = 30,
                        Height = 30,
                        CornerRadius = new Avalonia.CornerRadius(15),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Background = new Avalonia.Media.SolidColorBrush(
                            Avalonia.Media.Color.Parse("#F5D4E1")),
                        Child = new TextBlock
                        {
                            Text = "!",
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            Foreground = new Avalonia.Media.SolidColorBrush(
                                Avalonia.Media.Color.Parse("#A91651")),
                            FontSize = 16,
                            FontWeight = Avalonia.Media.FontWeight.Bold
                        }
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 1,
                        Text = message,
                        Margin = new Avalonia.Thickness(8, 0, 0, 0),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    }
                }
            }
        };
        Border footer = new()
        {
            [Grid.RowProperty] = 2,
            Padding = new Avalonia.Thickness(14, 10),
            [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush"),
            [!BorderBrushProperty] = new DynamicResourceExtension("PanelBorderBrush"),
            BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8,
                Children = { cancel, confirm }
            }
        };
        dialog.Content = new Border
        {
            BorderBrush = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#17191B")),
            BorderThickness = new Avalonia.Thickness(1),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("44,*,51"),
                Children = { header, body, footer }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private CancellationToken ResetLibraryViewCancellation()
    {
        libraryViewCancellation?.Cancel();
        libraryViewCancellation?.Dispose();
        libraryViewCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token);
        return libraryViewCancellation.Token;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (closeReady)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (closeInProgress)
        {
            return;
        }

        closeInProgress = true;
        IsEnabled = false;
        viewModel.StatusMessage = Loc.Get("Main.Closing");
        libraryViewCancellation?.Cancel();
        viewModel.BeginLibraryShutdown();
        viewModel.CancelPlaylistPreparation();
        viewModel.CancelRadioPlaylistPreparation();
        viewModel.BeginPlaybackShutdown();
        viewModel.StopListening();
        await ShutdownAudiobooksAsync();
        await ShutdownDiscAsync();
        await ShutdownEqualizerAsync();
        await ShutdownDeviceSyncAsync();
        await ShutdownPodcastsAsync();
        await StopMediaSessionAsync();
        await viewModel.WaitForPlaybackOperationsAsync();
        await viewModel.ShutdownPodcastAutoRefreshAsync();
        await viewModel.ShutdownPodcastProgressAsync();
        await viewModel.WaitForDesktopNotificationsAsync();
        await viewModel.WaitForLibraryOperationsAsync();
        viewModel.CancelMetadataRecoveryScan();
        await viewModel.WaitForMetadataOperationsAsync();
        await WaitForTransportKeyboardAsync();
        await viewModel.FlushPreferencesAsync();
        await viewModel.WaitForArtworkAsync();
        await pendingCoverLoad;
        closeReady = true;
        // Even an already-drained save must close after this Closing event returns.
        Dispatcher.UIThread.Post(Close);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        Dispose();
    }

    private async void TogglePlayback(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.TogglePlaybackAsync(lifetimeCancellation.Token));
    }

    private async void StopPlayback(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.StopPlaybackAsync(lifetimeCancellation.Token));
    }

    private async void PlaySelected(object? sender, TappedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.TogglePlaybackAsync(lifetimeCancellation.Token));
    }

    private async void PreviousTrack(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.MovePreviousAsync(lifetimeCancellation.Token));
    }

    private async void NextTrack(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.MoveNextAsync(lifetimeCancellation.Token));
    }

    private async void ToggleShuffle(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.ToggleShuffleAsync(lifetimeCancellation.Token));
    }

    private async void CycleRepeat(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.CycleRepeatAsync(lifetimeCancellation.Token));
    }

    private async void ToggleMute(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(() => viewModel.ToggleMuteAsync(lifetimeCancellation.Token));
    }

    private void BeginSeek(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(PositionSeekSurface).Properties.PointerUpdateKind !=
            PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        isSeekingFromPointer = true;
        viewModel.BeginSeek();
        UpdateSeekPosition(eventArgs.GetPosition(PositionSeekSurface).X);
        eventArgs.Pointer.Capture(PositionSeekSurface);
        eventArgs.Handled = true;
    }

    private void SeekMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!isSeekingFromPointer ||
            !eventArgs.GetCurrentPoint(PositionSeekSurface).Properties.IsLeftButtonPressed)
        {
            return;
        }

        UpdateSeekPosition(eventArgs.GetPosition(PositionSeekSurface).X);
        eventArgs.Handled = true;
    }

    private async void SeekReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!isSeekingFromPointer)
        {
            return;
        }

        UpdateSeekPosition(eventArgs.GetPosition(PositionSeekSurface).X);
        try
        {
            await RunUserActionAsync(() => viewModel.SeekAsync(PositionProgress.Value, lifetimeCancellation.Token));
        }
        finally
        {
            isSeekingFromPointer = false;
            eventArgs.Pointer.Capture(null);
            viewModel.EndSeek();
        }

        eventArgs.Handled = true;
    }

    private void UpdateSeekPosition(double pointerX)
    {
        if (PositionSeekSurface.Bounds.Width <= 0)
        {
            return;
        }

        double ratio = Math.Clamp(pointerX / PositionSeekSurface.Bounds.Width, 0, 1);
        PositionProgress.Value = PositionProgress.Minimum +
            ((PositionProgress.Maximum - PositionProgress.Minimum) * ratio);
    }

    private void BeginVolumeChange(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(VolumeSurface).Properties.PointerUpdateKind !=
            PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        isChangingVolumeFromPointer = true;
        UpdateVolumePosition(eventArgs.GetPosition(VolumeSurface).X);
        eventArgs.Pointer.Capture(VolumeSurface);
        eventArgs.Handled = true;
    }

    private void MoveVolume(object? sender, PointerEventArgs eventArgs)
    {
        if (!isChangingVolumeFromPointer ||
            !eventArgs.GetCurrentPoint(VolumeSurface).Properties.IsLeftButtonPressed)
        {
            return;
        }

        UpdateVolumePosition(eventArgs.GetPosition(VolumeSurface).X);
        eventArgs.Handled = true;
    }

    private async void EndVolumeChange(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!isChangingVolumeFromPointer)
        {
            return;
        }

        UpdateVolumePosition(eventArgs.GetPosition(VolumeSurface).X);
        isChangingVolumeFromPointer = false;
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
        await RunUserActionAsync(() => viewModel.ChangeVolumeAsync(
            VolumeProgress.Value,
            lifetimeCancellation.Token));
    }

    private void UpdateVolumePosition(double pointerX)
    {
        if (VolumeSurface.Bounds.Width <= 0)
        {
            return;
        }

        double ratio = Math.Clamp(pointerX / VolumeSurface.Bounds.Width, 0, 1);
        VolumeProgress.Value = VolumeProgress.Minimum +
            ((VolumeProgress.Maximum - VolumeProgress.Minimum) * ratio);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        _ = StopMediaSessionAsync().ContinueWith(task =>
            System.Diagnostics.Trace.WriteLine($"Could not finish system media shutdown: {task.Exception}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        DisposeArtwork();
        DisposeKeyboard();
        libraryViewCancellation?.Cancel();
        libraryViewCancellation?.Dispose();
        viewModel.Dispose();
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
    }
}
