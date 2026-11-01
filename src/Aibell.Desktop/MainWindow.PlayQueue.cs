using Aibell.Desktop.Localization;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private static readonly DataFormat<Track[]> TrackBatchDataFormat =
        DataFormat.CreateInProcessFormat<Track[]>("aibell-track-batch");
    private static readonly DataFormat<PlayQueueEntryViewModel> QueueEntryDataFormat =
        DataFormat.CreateInProcessFormat<PlayQueueEntryViewModel>("aibell-play-queue-entry");
    private PointerPressedEventArgs? pendingLibraryDrag;
    private TrackRowViewModel? pressedLibraryRow;
    private Track[]? preservedDragSelection;
    private Point libraryDragStart;
    private PointerPressedEventArgs? pendingQueueDrag;
    private PlayQueueEntryViewModel? pressedQueueEntry;
    private Point queueDragStart;
    private bool draggingTracks;
    private ColumnDefinition SidebarColumn => LibraryLayout.ColumnDefinitions[0];
    private ColumnDefinition QueueColumn => LibraryLayout.ColumnDefinitions[4];

    private static bool PassedDragThreshold(Point current, Point start) =>
        Math.Abs(current.X - start.X) >= 5 || Math.Abs(current.Y - start.Y) >= 5;

    private void InitializePlayQueue()
    {
        viewModel.PropertyChanged += QueuePanelPropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= QueuePanelPropertyChanged;
        UpdateQueuePanelLayout();
        LibraryLayout.SizeChanged += (_, _) => UpdateQueuePanelLayout();
        TrackList.AddHandler(PointerPressedEvent, LibraryRowsPointerPressed, RoutingStrategies.Tunnel, true);
        TrackList.AddHandler(PointerMovedEvent, LibraryRowsPointerMoved, RoutingStrategies.Bubble, true);
        TrackList.AddHandler(PointerReleasedEvent, LibraryRowsPointerReleased, RoutingStrategies.Bubble, true);
        PlayQueueList.AddHandler(PointerPressedEvent, QueueRowsPointerPressed, RoutingStrategies.Tunnel, true);
        PlayQueueList.AddHandler(PointerMovedEvent, QueueRowsPointerMoved, RoutingStrategies.Bubble, true);
        PlayQueueList.AddHandler(PointerReleasedEvent, (_, _) => pendingQueueDrag = null, RoutingStrategies.Bubble, true);
    }

    private void QueuePanelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(viewModel.IsPlayQueueVisible) or nameof(viewModel.PlayQueueWidth)
            or nameof(viewModel.IsSidebarVisible) or nameof(viewModel.SidebarWidth)
            or nameof(viewModel.SidebarPlaylistsFraction) or nameof(viewModel.SidebarLibraryFraction) or nameof(viewModel.SidebarNowPlayingHeight))
        {
            UpdateQueuePanelLayout();
        }
    }

    private void UpdateQueuePanelLayout()
    {
        // The centre column needs 280px; the two side panels share what is left.
        double available = LibraryLayout.Bounds.Width - 280 - 10;
        double queueMaximum = Math.Clamp(available - (viewModel.IsSidebarVisible ? 160 : 28), 200, 480);
        QueueColumn.MinWidth = viewModel.IsPlayQueueVisible ? 200 : 28;
        QueueColumn.MaxWidth = viewModel.IsPlayQueueVisible ? queueMaximum : 28;
        QueueColumn.Width = new GridLength(viewModel.IsPlayQueueVisible ? Math.Min(viewModel.PlayQueueWidth, queueMaximum) : 28);

        double queueWidth = viewModel.IsPlayQueueVisible ? QueueColumn.Width.Value : 28;
        double sidebarMaximum = Math.Clamp(available - queueWidth, 160, 480);
        SidebarColumn.MinWidth = viewModel.IsSidebarVisible ? 160 : 28;
        SidebarColumn.MaxWidth = viewModel.IsSidebarVisible ? sidebarMaximum : 28;
        SidebarColumn.Width = new GridLength(viewModel.IsSidebarVisible ? Math.Min(viewModel.SidebarWidth, sidebarMaximum) : 28);

        RowDefinitions outer = NowPlayingSplitter.Parent is Grid grid ? grid.RowDefinitions : [];
        if (outer.Count == 3)
        {
            outer[0].MinHeight = 160;
            outer[2].MinHeight = 120;
            outer[2].MaxHeight = 600;
            outer[2].Height = new GridLength(viewModel.SidebarNowPlayingHeight);
        }
        RowDefinitions sections = SidebarSections.RowDefinitions;
        double library = viewModel.SidebarLibraryFraction;
        double rest = 1 - library;
        sections[0].MinHeight = 60;
        sections[2].MinHeight = 60;
        sections[4].MinHeight = 60;
        sections[0].Height = new GridLength(library, GridUnitType.Star);
        sections[2].Height = new GridLength(rest * viewModel.SidebarPlaylistsFraction, GridUnitType.Star);
        sections[4].Height = new GridLength(rest * (1 - viewModel.SidebarPlaylistsFraction), GridUnitType.Star);
    }

    private async void ToggleSidebar(object? sender, RoutedEventArgs eventArgs) =>
        await RunUserActionAsync(() => viewModel.ToggleSidebarAsync(lifetimeCancellation.Token));

    private async void SidebarResizeCompleted(object? sender, VectorEventArgs eventArgs)
    {
        RowDefinitions sections = SidebarSections.RowDefinitions;
        double library = sections[0].ActualHeight;
        double playlists = sections[2].ActualHeight;
        double folders = sections[4].ActualHeight;
        double total = library + playlists + folders;
        double libraryFraction = total > 0 ? library / total : 0.3;
        double fraction = playlists + folders > 0 ? playlists / (playlists + folders) : 0.5;
        double nowPlaying = NowPlayingSplitter.Parent is Grid { RowDefinitions.Count: 3 } grid
            ? grid.RowDefinitions[2].ActualHeight
            : viewModel.SidebarNowPlayingHeight;
        await RunUserActionAsync(() => viewModel.ResizeSidebarAsync(
            SidebarColumn.ActualWidth, libraryFraction, fraction, nowPlaying, lifetimeCancellation.Token));
    }

    private async void TogglePlayQueue(object? sender, RoutedEventArgs eventArgs) =>
        await RunUserActionAsync(() => viewModel.TogglePlayQueueAsync(lifetimeCancellation.Token));

    private async void QueueResizeCompleted(object? sender, VectorEventArgs eventArgs) =>
        await RunUserActionAsync(() => viewModel.ResizePlayQueueAsync(QueueColumn.ActualWidth, lifetimeCancellation.Token));

    private void ClearPlayQueue(object? sender, RoutedEventArgs eventArgs) => viewModel.ClearPlayQueue();

    private Track[] SelectedLibraryTracks() => viewModel.Tracks
        .Where(row => TrackList.SelectedItems?.Contains(row) == true)
        .Select(row => row.Source).ToArray();

    private void QueueSelectedNext(object? sender, RoutedEventArgs eventArgs) =>
        viewModel.QueueTracks(SelectedLibraryTracks(), next: true);

    private void QueueSelectedLast(object? sender, RoutedEventArgs eventArgs) =>
        viewModel.QueueTracks(SelectedLibraryTracks());

    private static T? RowAt<T>(object? source) where T : class => source is Control control
        ? control.GetVisualAncestors().Prepend(control).OfType<Control>()
            .Select(ancestor => ancestor.DataContext).OfType<T>().FirstOrDefault()
        : null;

    private void LibraryRowsPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(TrackList).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
            eventArgs.Source is Control { Classes: var classes } && classes.Contains("dragHandle") ||
            RowAt<TrackRowViewModel>(eventArgs.Source) is not { } row)
        {
            return;
        }
        pendingLibraryDrag = eventArgs;
        pressedLibraryRow = row;
        libraryDragStart = eventArgs.GetPosition(TrackList);
        preservedDragSelection = null;
        Track[] selected = SelectedLibraryTracks();
        if (selected.Length > 1 && TrackList.SelectedItems?.Contains(row) == true &&
            (eventArgs.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta)) == 0)
        {
            preservedDragSelection = selected;
            eventArgs.Handled = true;
        }
    }

    private async void LibraryRowsPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (draggingTracks || pendingLibraryDrag is not { } trigger || pressedLibraryRow is null ||
            !eventArgs.GetCurrentPoint(TrackList).Properties.IsLeftButtonPressed ||
            !PassedDragThreshold(eventArgs.GetPosition(TrackList), libraryDragStart))
        {
            return;
        }
        Track[] tracks = preservedDragSelection ?? SelectedLibraryTracks();
        if (tracks.Length == 0) tracks = [pressedLibraryRow.Source];
        pendingLibraryDrag = null;
        preservedDragSelection = null;
        draggingTracks = true;
        DataTransfer data = new();
        data.Add(DataTransferItem.Create(TrackBatchDataFormat, tracks));
        if (tracks.Length == 1 && pressedLibraryRow.CanReorder)
        {
            data.Add(DataTransferItem.Create(TrackRowDataFormat, pressedLibraryRow));
        }
        eventArgs.Handled = true;
        try
        {
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            draggingTracks = false;
        }
    }

    private void LibraryRowsPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (pendingLibraryDrag is not null && preservedDragSelection is not null && pressedLibraryRow is not null)
        {
            TrackList.SelectedItem = pressedLibraryRow;
        }
        pendingLibraryDrag = null;
        preservedDragSelection = null;
    }

    private void QueueRowsPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(PlayQueueList).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed &&
            RowAt<PlayQueueEntryViewModel>(eventArgs.Source) is { } entry)
        {
            pendingQueueDrag = eventArgs;
            pressedQueueEntry = entry;
            queueDragStart = eventArgs.GetPosition(PlayQueueList);
        }
    }

    private async void QueueRowsPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (draggingTracks || pendingQueueDrag is not { } trigger || pressedQueueEntry is null || !pressedQueueEntry.CanReorder ||
            !eventArgs.GetCurrentPoint(PlayQueueList).Properties.IsLeftButtonPressed ||
            !PassedDragThreshold(eventArgs.GetPosition(PlayQueueList), queueDragStart)) return;
        pendingQueueDrag = null;
        draggingTracks = true;
        DataTransfer data = new();
        data.Add(DataTransferItem.Create(QueueEntryDataFormat, pressedQueueEntry));
        try
        {
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
        }
        finally { draggingTracks = false; }
    }

    private void PlayQueueDragOver(object? sender, DragEventArgs eventArgs)
    {
        bool accepted = !viewModel.IsBusy &&
            (eventArgs.DataTransfer.Contains(TrackBatchDataFormat) ||
             eventArgs.DataTransfer.Contains(QueueEntryDataFormat) ||
             eventArgs.DataTransfer.Contains(DataFormat.File));
        eventArgs.DragEffects = accepted
            ? eventArgs.DataTransfer.Contains(QueueEntryDataFormat) ? DragDropEffects.Move : DragDropEffects.Copy
            : DragDropEffects.None;
        QueueDropHighlight.IsVisible = accepted;
        eventArgs.Handled = true;
    }

    private void PlayQueueDragLeave(object? sender, DragEventArgs eventArgs) => QueueDropHighlight.IsVisible = false;

    private async void PlayQueueDrop(object? sender, DragEventArgs eventArgs)
    {
        QueueDropHighlight.IsVisible = false;
        eventArgs.Handled = true;
        if (viewModel.IsBusy) { eventArgs.DragEffects = DragDropEffects.None; return; }
        if (eventArgs.DataTransfer.TryGetValue(QueueEntryDataFormat) is { } source)
        {
            PlayQueueEntryViewModel? target = RowAt<PlayQueueEntryViewModel>(eventArgs.Source);
            target ??= viewModel.QueueItems.LastOrDefault();
            if (target is not null)
            {
                Control? targetControl = eventArgs.Source as Control;
                bool after = targetControl is null || eventArgs.GetPosition(targetControl).Y >= targetControl.Bounds.Height / 2;
                viewModel.MoveQueueEntry(source.Id, target.Id, after);
            }
            eventArgs.DragEffects = DragDropEffects.Move;
        }
        else if (eventArgs.DataTransfer.TryGetValue(TrackBatchDataFormat) is { } tracks)
        {
            viewModel.QueueTracks(tracks);
            eventArgs.DragEffects = DragDropEffects.Copy;
        }
        else if (eventArgs.DataTransfer.TryGetFiles() is { } files)
        {
            Uri[] locations = files.OfType<IStorageFile>().Select(file => file.Path).ToArray();
            if (locations.Length == 0)
            {
                viewModel.StatusMessage = Loc.Get("Queue.DropHint");
                eventArgs.DragEffects = DragDropEffects.None;
                return;
            }
            await RunUserActionAsync(() => viewModel.QueueFilesAsync(locations, lifetimeCancellation.Token));
            eventArgs.DragEffects = DragDropEffects.Copy;
        }
        else eventArgs.DragEffects = DragDropEffects.None;
    }

    private async void OpenFiles(object? sender, RoutedEventArgs eventArgs)
    {
        await RunUserActionAsync(async () =>
        {
            IReadOnlyList<Uri> selected = await filePicker.PickFilesAsync(this, new FilePickerOpenOptions
            {
                Title = Loc.Get("Picker.OpenFiles"),
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType(Loc.Get("Picker.MusicFiles"))
                    {
                        Patterns = ["*.aac", "*.aiff", "*.alac", "*.flac", "*.m4a", "*.m4b", "*.mid", "*.midi", "*.mp3", "*.ogg", "*.oga", "*.opus", "*.rmi", "*.rsn", "*.spc", "*.wav", "*.wma"]
                    },
                    FilePickerFileTypes.All
                ]
            });
            Uri[] files = [.. selected.Where(uri => uri.IsFile)];
            if (files.Length > 0 && CanRunUserAction) await viewModel.OpenFilesAsync(files, lifetimeCancellation.Token);
        });
    }

    // Files dropped anywhere else in the window play right away; the queue panel and the library rows keep their own drop targets.
    private void WindowDragOver(object? sender, DragEventArgs eventArgs)
    {
        bool accepted = !viewModel.IsBusy && eventArgs.DataTransfer.Contains(DataFormat.File);
        eventArgs.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private async void WindowDrop(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (viewModel.IsBusy || eventArgs.DataTransfer.TryGetFiles() is not { } dropped)
        {
            eventArgs.DragEffects = DragDropEffects.None;
            return;
        }
        Uri[] files = [.. dropped.OfType<IStorageFile>().Select(file => file.Path)];
        if (files.Length == 0)
        {
            viewModel.StatusMessage = Loc.Get("Queue.DropHint");
            eventArgs.DragEffects = DragDropEffects.None;
            return;
        }
        eventArgs.DragEffects = DragDropEffects.Copy;
        await RunUserActionAsync(() => viewModel.OpenFilesAsync(files, lifetimeCancellation.Token));
    }

    private void QueueItemPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: PlayQueueEntryViewModel entry } &&
            eventArgs.GetCurrentPoint(PlayQueueList).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            PlayQueueList.SelectedItem = entry;
        }
    }

    private async void PlayQueuedTrack(object? sender, RoutedEventArgs eventArgs)
    {
        if (viewModel.SelectedQueueEntry is { } entry)
            await RunUserActionAsync(() => viewModel.PlayQueueEntryAsync(entry.Id, lifetimeCancellation.Token));
    }

    private async void PlayQueueMenuTrack(object? sender, RoutedEventArgs eventArgs)
    {
        if (QueueEntryFrom(sender) is { } entry)
            await RunUserActionAsync(() => viewModel.PlayQueueEntryAsync(entry.Id, lifetimeCancellation.Token));
    }

    // The queue's context menu tags each item with its entry; the main menu has no tag and acts on the selected entry.
    private PlayQueueEntryViewModel? QueueEntryFrom(object? sender) =>
        (sender as MenuItem)?.Tag as PlayQueueEntryViewModel ?? viewModel.SelectedQueueEntry;

    private void RemoveQueuedTrack(object? sender, RoutedEventArgs eventArgs)
    {
        if (QueueEntryFrom(sender) is { } entry) viewModel.RemoveQueueEntry(entry.Id);
    }

    private async void AddQueuedTrackToLibrary(object? sender, RoutedEventArgs eventArgs)
    {
        if (QueueEntryFrom(sender) is { } entry)
            await RunUserActionAsync(() => viewModel.AddQueueEntryToLibraryAsync(entry, lifetimeCancellation.Token));
    }

    private void PopulateQueueContextMenu(object? sender, CancelEventArgs eventArgs)
    {
        if (sender is not ContextMenu menu ||
            (menu.PlacementTarget?.DataContext as PlayQueueEntryViewModel ?? viewModel.SelectedQueueEntry) is not { } entry) return;
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            item.Tag = entry;
            item.IsEnabled = !viewModel.IsBusy;
        }
        MenuItem parent = menu.Items.OfType<MenuItem>().Single(item => item.Classes.Contains("addToPlaylist"));
        parent.ItemsSource = BuildQueueAddToPlaylistItems(entry);
    }

    // Shared by the queue's context menu and Controls > Play Queue > Add to Playlist.
    private List<Control> BuildQueueAddToPlaylistItems(PlayQueueEntryViewModel entry)
    {
        List<Control> children = [];
        foreach (PlaylistFilterViewModel playlist in viewModel.EditablePlaylists.Where(item => !item.IsSmart))
        {
            MenuItem item = new() { Header = playlist.DisplayName, Tag = new QueuePlaylistAction(entry, playlist) };
            item.Click += AddQueuedTrackToPlaylist;
            children.Add(item);
        }
        if (children.Count == 0) children.Add(new MenuItem { Header = Loc.Get("Menu.NoPlaylists"), IsEnabled = false });
        children.Add(new Separator());
        MenuItem create = new() { Header = Loc.Get("Menu.NewPlaylist"), Tag = entry };
        create.Click += CreatePlaylistWithQueuedTrack;
        children.Add(create);
        return children;
    }

    private async void AddQueuedTrackToPlaylist(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is MenuItem { Tag: QueuePlaylistAction action })
            await RunUserActionAsync(() => viewModel.AddQueueEntryToPlaylistAsync(action.Entry, action.Playlist, lifetimeCancellation.Token));
    }

    private async void CreatePlaylistWithQueuedTrack(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: PlayQueueEntryViewModel entry }) return;
        string? name = await ShowTextPromptAsync(Loc.Get("Dialog.CreatePlaylist"), Loc.Get("Dialog.PlaylistName"), string.Empty, Loc.Get("Dialog.Create"));
        if (!string.IsNullOrWhiteSpace(name))
            await RunUserActionAsync(() => viewModel.CreatePlaylistWithQueueEntryAsync(name, entry, lifetimeCancellation.Token));
    }

    private async void ShowQueuedFile(object? sender, RoutedEventArgs eventArgs)
    {
        if (QueueEntryFrom(sender) is not { } entry) return;
        Uri location = entry.Row.Source.Location;
        if (!location.IsFile || !File.Exists(location.LocalPath))
        {
            viewModel.StatusMessage = Loc.Get("Queue.Gone");
            return;
        }
        try
        {
            if (!await RevealFileAsync(location) &&
                !await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Path.GetDirectoryName(location.LocalPath)!)))
                viewModel.StatusMessage = Loc.Get("Queue.NoFolder");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            viewModel.StatusMessage = Loc.Format("Queue.ShowFileFailed", exception.Message);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> RevealFileAsync(Uri location)
    {
        ProcessStartInfo start = new()
        {
            UseShellExecute = false,
            RedirectStandardError = !OperatingSystem.IsWindows(),
            RedirectStandardOutput = !OperatingSystem.IsWindows()
        };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = "explorer.exe";
            start.ArgumentList.Add("/select," + location.LocalPath);
        }
        else
        {
            start.FileName = "dbus-send";
            foreach (string argument in new[] { "--session", "--print-reply", "--reply-timeout=2000",
                "--dest=org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
                "org.freedesktop.FileManager1.ShowItems", "array:string:" + location.AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal), "string:" })
                start.ArgumentList.Add(argument);
        }
        try
        {
            using Process? process = Process.Start(start);
            if (process is null) return false;
            if (OperatingSystem.IsWindows()) return true;
            Task<string> output = process.StandardOutput.ReadToEndAsync(lifetimeCancellation.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(lifetimeCancellation.Token);
            await process.WaitForExitAsync(lifetimeCancellation.Token);
            await Task.WhenAll(output, error);
            return process.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }

    private sealed record QueuePlaylistAction(PlayQueueEntryViewModel Entry, PlaylistFilterViewModel Playlist);
}
