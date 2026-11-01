using Aibell.Desktop.Localization;
using Aibell.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Aibell.Desktop;

/// <summary>
/// The main menu. Every action reuses the handler of the button or context-menu item it mirrors, so there is one copy of each
/// behavior. What is enabled follows the selected library item (Music, Radio, Audio CD, Devices, Podcasts, Audiobooks) and the
/// selection inside it: <see cref="RefreshMenuState"/> runs each time a top-level menu opens, which also covers pane view models
/// that are created lazily. Shortcuts the window already handles in key handlers are display-only (<c>InputGesture</c>) so they
/// never fire twice; only new shortcuts carry a <c>HotKey</c>, and their handlers check state themselves.
/// </summary>
public sealed partial class MainWindow
{
    private void MenuOpened(object? sender, RoutedEventArgs args) => RefreshMenuState();

    internal void RefreshMenuState()
    {
        if (disposed || closeInProgress) return;
        RefreshFileMenu();
        RefreshEditMenu();
        RefreshViewMenu();
        RefreshControlsMenu();
        RefreshLibraryMenu();
    }

    private void RefreshFileMenu()
    {
        MenuOpenFiles.IsEnabled = !viewModel.IsBusy;
        MenuAddFolder.IsEnabled = viewModel.CanImport;
        MenuRescanFolder.IsEnabled = MenuRemoveFolder.IsEnabled = viewModel.CanManageLibrarySource;
        MenuNewPlaylist.IsEnabled = MenuNewSmartPlaylist.IsEnabled = !viewModel.IsBusy;
        MenuRenamePlaylist.IsEnabled = MenuDeletePlaylist.IsEnabled = viewModel.CanEditPlaylist;
        MenuEditRules.IsEnabled = viewModel.CanEditPlaylist && viewModel.SelectedPlaylist is { IsSmart: true };
        MenuImportPlaylist.IsEnabled = viewModel.CanImportPlaylistFile;
        MenuExportPlaylist.IsEnabled = viewModel.CanExportPlaylistFile;
    }

    private void RefreshEditMenu()
    {
        TrackRowViewModel? track = viewModel.IsMusicView ? viewModel.SelectedTrack : null;
        bool hasTrack = track is not null && !viewModel.IsBusy;
        MenuSelectAll.IsEnabled = viewModel.IsMusicView && viewModel.Tracks.Count > 0;
        MenuViewMetadata.IsEnabled = hasTrack;
        string? artworkReason = ArtworkUnavailableReason(track);
        MenuGetArtwork.IsEnabled = hasTrack && artworkReason is null;
        ToolTip.SetTip(MenuGetArtwork, hasTrack ? artworkReason ?? Loc.Get("Artwork.MenuTip") : null);
        MenuAddToPlaylist.IsEnabled = hasTrack;
        MenuAddToPlaylist.ItemsSource = hasTrack ? BuildAddToPlaylistItems(track) : null;
        MenuQueueNext.IsEnabled = MenuQueueLast.IsEnabled = hasTrack;
        MenuRating.IsEnabled = hasTrack;
        MenuRemoveFromPlaylist.IsEnabled = hasTrack && viewModel.CanRemoveFromPlaylist;
        MenuMoveUp.IsEnabled = MenuMoveDown.IsEnabled = hasTrack && viewModel.CanReorderPlaylist;
        MenuFind.IsEnabled = viewModel.CanSearchLibrary;
        MenuPreferences.IsEnabled = !viewModel.IsBusy;
    }

    private void RefreshViewMenu()
    {
        MenuViewMusic.IsChecked = viewModel.IsMusicView;
        MenuViewRadio.IsChecked = viewModel.IsRadioView;
        MenuViewDisc.IsChecked = viewModel.IsDiscView;
        MenuViewDevices.IsChecked = viewModel.IsDevicesView;
        MenuViewPodcasts.IsChecked = viewModel.IsPodcastsView;
        MenuViewAudiobooks.IsChecked = viewModel.IsAudiobooksView;
        MenuViewRadio.IsEnabled = viewModel.CanUseRadio;
        MenuViewDisc.IsEnabled = viewModel.CanUseDisc && discViewModelFactory is not null;
        MenuViewDevices.IsEnabled = viewModel.CanUseDevices && deviceSyncViewModelFactory is not null;
        MenuViewPodcasts.IsEnabled = podcastViewModelFactory is not null;
        MenuViewAudiobooks.IsEnabled = audiobooksViewModelFactory is not null;
        MenuSortBy.IsEnabled = viewModel.IsMusicView;
        MenuColumns.IsEnabled = viewModel.IsMusicView;
        MenuColumns.ItemsSource = viewModel.IsMusicView ? BuildColumnMenuItems() : null;
        MenuShowSidebar.IsChecked = viewModel.IsSidebarVisible;
        MenuShowQueue.IsChecked = viewModel.IsPlayQueueVisible;
    }

    private void RefreshControlsMenu()
    {
        MenuPlayPause.Header = viewModel.PlaybackButtonText;
        MenuPlayPause.IsEnabled = viewModel.CanPlay;
        MenuStop.IsEnabled = viewModel.CanStop;
        MenuPrevious.IsEnabled = viewModel.CanGoPrevious;
        MenuNext.IsEnabled = viewModel.CanGoNext;
        MenuSeekBackward.IsEnabled = MenuSeekForward.IsEnabled = viewModel.CanSeekPlayback && PositionSeekSurface.IsEnabled;
        MenuShuffle.Header = viewModel.ShuffleButtonText;
        MenuShuffle.IsChecked = viewModel.IsShuffleOn;
        MenuRepeat.Header = viewModel.RepeatButtonText;
        MenuRepeat.IsChecked = viewModel.IsRepeatActive;
        MenuMute.Header = viewModel.MuteButtonText;
        MenuMute.IsChecked = viewModel.IsMutedState;
        MenuMute.IsEnabled = viewModel.CanMute;
        MenuVolumeUp.IsEnabled = MenuVolumeDown.IsEnabled = VolumeSurface.IsEnabled;
        MenuEqualizer.IsEnabled = viewModel.CanUseEqualizer;

        PlayQueueEntryViewModel? entry = viewModel.SelectedQueueEntry;
        bool hasEntry = entry is not null && !viewModel.IsBusy;
        MenuQueuePlay.IsEnabled = MenuQueueShowFile.IsEnabled = MenuQueueAddToLibrary.IsEnabled = MenuQueueRemove.IsEnabled = hasEntry;
        MenuQueueAddToPlaylist.IsEnabled = hasEntry;
        MenuQueueAddToPlaylist.ItemsSource = hasEntry ? BuildQueueAddToPlaylistItems(entry!) : null;
        MenuQueueClear.IsEnabled = !viewModel.IsQueueEmpty && viewModel.CanImport;
    }

    private void RefreshLibraryMenu()
    {
        RefreshRadioMenu();
        RefreshPodcastsMenu();
        RefreshDiscMenu();
        RefreshDevicesMenu();
        RefreshAudiobooksMenu();
    }

    private void RefreshRadioMenu()
    {
        bool radio = viewModel.IsRadioView;
        MenuRadio.IsEnabled = radio;
        RadioCardViewModel? card = radio ? viewModel.SelectedRadioCard : null;
        bool browser = radio && viewModel.ShowRadioBrowser;
        bool mine = browser && viewModel.IsRadioMyMode;
        MenuRadioCatalog.IsEnabled = radio && viewModel.CanUseRadioCatalog && !viewModel.IsRadioCatalogMode;
        MenuRadioMine.IsEnabled = radio && viewModel.CanUseRadioCatalog && viewModel.IsRadioCatalogMode;
        MenuRadioEnableCatalog.IsEnabled = radio && viewModel.ShowRadioCatalogOptIn;
        MenuRadioPlay.IsEnabled = browser && viewModel.SelectedRadioId is not null;
        MenuRadioSave.IsEnabled = card is { CanSave: true };
        MenuRadioUnsave.IsEnabled = card is { CanUnsave: true };
        MenuRadioAdd.IsEnabled = mine && viewModel.CanManageRadio;
        MenuRadioEdit.IsEnabled = MenuRadioRemove.IsEnabled = mine && viewModel.CanEditRadioStation;
        MenuRadioImport.IsEnabled = mine && viewModel.CanImportRadioPlaylist;
    }

    private void RefreshPodcastsMenu()
    {
        PodcastsViewModel? model = viewModel.IsPodcastsView ? podcasts : null;
        PodcastEpisodeItem? episode = model?.SelectedEpisode;
        MenuPodcasts.IsEnabled = model is not null;
        MenuPodcastSubscribe.IsEnabled = model is { CanSubscribe: true };
        MenuPodcastRefresh.IsEnabled = model is { CanRefresh: true };
        MenuPodcastRefreshAll.IsEnabled = model is { CanRefreshAll: true };
        MenuPodcastRemove.IsEnabled = model is { CanRemove: true };
        MenuPodcastPlay.IsEnabled = episode is { ShowPlay: true };
        MenuPodcastPause.IsEnabled = episode is { CanPause: true };
        MenuPodcastResume.IsEnabled = model is { CanResumeSelected: true };
        MenuPodcastStartOver.IsEnabled = episode is { ShowStartOver: true };
        MenuPodcastSavePosition.IsEnabled = model is { CanSaveSelectedPosition: true };
        MenuPodcastRating.IsEnabled = episode is not null;
        MenuPodcastDownload.IsEnabled = episode is { CanDownload: true };
        MenuPodcastCancelDownload.IsEnabled = episode is { CanCancel: true };
        MenuPodcastDeleteFile.IsEnabled = episode is { CanDeleteDownload: true };
        MenuPodcastDownloadNew.IsEnabled = model is { CanDownloadNew: true };
        MenuPodcastMarkSeen.IsEnabled = model is { HasNewEpisodes: true };
        MenuPodcastCancel.IsEnabled = model is { IsWorking: true };
    }

    private void RefreshDiscMenu()
    {
        DiscViewModel? model = viewModel.IsDiscView ? disc : null;
        MenuDisc.IsEnabled = model is not null;
        MenuDiscRead.IsEnabled = model is { CanRefresh: true };
        MenuDiscLookUp.IsEnabled = model is { CanLookUp: true };
        MenuDiscArtist.IsEnabled = model is { CanShowArtist: true };
        MenuDiscPlay.IsEnabled = model is { CanPlay: true };
        MenuDiscCancel.IsEnabled = model is { IsBusy: true };
    }

    private void RefreshDevicesMenu()
    {
        DeviceSyncViewModel? model = viewModel.IsDevicesView ? deviceSync : null;
        MenuDevices.IsEnabled = model is not null;
        MenuDevicesRefresh.IsEnabled = model is { CanChange: true };
        MenuDevicesPreview.IsEnabled = model is { CanPreview: true };
        MenuDevicesStart.IsEnabled = model is { CanSync: true };
        MenuDevicesCancel.IsEnabled = model is { CanCancel: true };
    }

    private void RefreshAudiobooksMenu()
    {
        AudiobooksViewModel? model = viewModel.IsAudiobooksView ? audiobooks : null;
        MenuAudiobooks.IsEnabled = model is not null;
        MenuBookAdd.IsEnabled = model is { CanAdd: true };
        MenuBookPlay.IsEnabled = model is { CanPlay: true };
        MenuBookResume.IsEnabled = model is { CanResume: true };
        MenuBookChapters.IsEnabled = model is { CanShowChapters: true };
        MenuBookPlayChapter.IsEnabled = model is { CanPlayChapter: true };
        MenuBookSavePosition.IsEnabled = model is { CanSave: true };
        MenuBookRemove.IsEnabled = model is { CanRemove: true };
        MenuBookCancel.IsEnabled = model is { IsBusy: true };
    }

    // File, Edit and View.

    private void QuitApplication(object? sender, RoutedEventArgs args) => Close();

    private void FindInLibrary(object? sender, RoutedEventArgs args) => FocusSearch();

    private void FocusSearch()
    {
        TextBox target = viewModel.IsRadioView && viewModel.ShowRadioBrowser ? RadioSearchBox : SearchBox;
        target.Focus(NavigationMethod.Tab);
        target.SelectAll();
    }

    private void SelectAllTracks(object? sender, RoutedEventArgs args)
    {
        if (!viewModel.IsMusicView) return;
        TrackList.SelectAll();
        TrackList.Focus();
    }

    // Controls.

    private async void SeekBackward(object? sender, RoutedEventArgs args) =>
        await RunTransportKeyboardActionAsync(() => ApplyKeyboardSeekAsync(Key.Left));

    private async void SeekForward(object? sender, RoutedEventArgs args) =>
        await RunTransportKeyboardActionAsync(() => ApplyKeyboardSeekAsync(Key.Right));

    private async void VolumeUp(object? sender, RoutedEventArgs args) =>
        await RunTransportKeyboardActionAsync(() => ApplyKeyboardVolumeAsync(Key.Up));

    private async void VolumeDown(object? sender, RoutedEventArgs args) =>
        await RunTransportKeyboardActionAsync(() => ApplyKeyboardVolumeAsync(Key.Down));

    // Library: Podcasts. Per-episode actions apply to the selected episode.

    private async void PodcastSubscribe(object? sender, RoutedEventArgs args) { if (podcasts is { } m) await RunUserActionAsync(m.SubscribeAsync); }
    private async void PodcastRefresh(object? sender, RoutedEventArgs args) { if (podcasts is { } m) await RunUserActionAsync(m.RefreshSelectedAsync); }
    private async void PodcastRefreshAll(object? sender, RoutedEventArgs args) { if (podcasts is { } m) await RunUserActionAsync(m.RefreshAllAsync); }
    private async void PodcastRemove(object? sender, RoutedEventArgs args) { if (podcasts is not null) await RunUserActionAsync(PodcastsPane.RemoveSelectedAsync); }
    private async void PodcastPlay(object? sender, RoutedEventArgs args) { if (podcasts is { SelectedEpisode: { } item } m) await RunUserActionAsync(() => m.PlayAsync(item)); }
    private async void PodcastPause(object? sender, RoutedEventArgs args) { if (podcasts is { SelectedEpisode: { } item } m) await RunUserActionAsync(() => m.PauseAsync(item)); }
    private async void PodcastResume(object? sender, RoutedEventArgs args) { if (podcasts is { } m) await RunUserActionAsync(m.ResumeSelectedAsync); }
    private async void PodcastStartOver(object? sender, RoutedEventArgs args) { if (podcasts is { SelectedEpisode: { } item } m) await RunUserActionAsync(() => m.StartOverAsync(item)); }
    private async void PodcastSavePosition(object? sender, RoutedEventArgs args) { if (podcasts is { } m) await RunUserActionAsync(m.SaveSelectedPositionAsync); }
    private async void PodcastDownload(object? sender, RoutedEventArgs args) { if (podcasts is { SelectedEpisode: { } item } m) await RunUserActionAsync(() => m.DownloadAsync(item)); }
    private void PodcastCancelDownload(object? sender, RoutedEventArgs args) { if (podcasts is { SelectedEpisode: { } item } m) m.CancelDownload(item); }
    private async void PodcastDeleteFile(object? sender, RoutedEventArgs args) { if (podcasts is not null) await RunUserActionAsync(PodcastsPane.DeleteSelectedDownloadAsync); }
    private async void PodcastDownloadNew(object? sender, RoutedEventArgs args) { if (podcasts is { } m) await RunUserActionAsync(m.DownloadNewAsync); }
    private async void PodcastMarkAllSeen(object? sender, RoutedEventArgs args) { if (podcasts is { } m) await RunUserActionAsync(m.MarkAllSeenAsync); }
    private void PodcastCancelOperation(object? sender, RoutedEventArgs args) => podcasts?.CancelFeedOperation();

    private async void PodcastRate(object? sender, RoutedEventArgs args)
    {
        if (podcasts is { SelectedEpisode: { } item } m && (sender as MenuItem)?.Tag is string tag && int.TryParse(tag, out int rating))
            await RunUserActionAsync(() => m.SetRatingAsync(item, rating));
    }

    // Library: Audio CD, Devices and Audiobooks.

    private async void DiscRead(object? sender, RoutedEventArgs args) { if (disc is { } m) await RunUserActionAsync(m.RefreshAsync); }
    private async void DiscLookUp(object? sender, RoutedEventArgs args) { if (disc is { } m) await RunUserActionAsync(m.LookUpAsync); }
    private async void DiscArtist(object? sender, RoutedEventArgs args) { if (disc is { } m) await RunUserActionAsync(m.ShowArtistAsync); }
    private async void DiscPlay(object? sender, RoutedEventArgs args) { if (disc is { } m) await RunUserActionAsync(() => m.PlayAsync(m.SelectedRow)); }
    private void DiscCancel(object? sender, RoutedEventArgs args) => disc?.CancelOperation();

    private async void DevicesRefresh(object? sender, RoutedEventArgs args) { if (deviceSync is { } m) await RunUserActionAsync(m.RefreshVolumesAsync); }
    private async void DevicesPreview(object? sender, RoutedEventArgs args) { if (deviceSync is { } m) await RunUserActionAsync(m.PreviewAsync); }
    private async void DevicesStart(object? sender, RoutedEventArgs args) { if (deviceSync is { } m) await RunUserActionAsync(m.StartAsync); }
    private void DevicesCancel(object? sender, RoutedEventArgs args) => deviceSync?.CancelOperation();

    private async void AudiobookAdd(object? sender, RoutedEventArgs args) { if (audiobooks is not null) await RunUserActionAsync(AudiobooksPane.AddAsync); }
    private async void AudiobookPlay(object? sender, RoutedEventArgs args) { if (audiobooks is { } m) await RunUserActionAsync(() => m.PlayAsync(m.SelectedItem, resume: false)); }
    private async void AudiobookResume(object? sender, RoutedEventArgs args) { if (audiobooks is { } m) await RunUserActionAsync(() => m.PlayAsync(m.SelectedItem, resume: true)); }
    private async void AudiobookChapters(object? sender, RoutedEventArgs args) { if (audiobooks is { } m) await RunUserActionAsync(() => m.ShowChaptersAsync(m.SelectedItem)); }
    private async void AudiobookPlayChapter(object? sender, RoutedEventArgs args) { if (audiobooks is { } m) await RunUserActionAsync(m.PlayChapterAsync); }
    private async void AudiobookSavePosition(object? sender, RoutedEventArgs args) { if (audiobooks is { } m) await RunUserActionAsync(m.SaveCurrentAsync); }
    private async void AudiobookRemove(object? sender, RoutedEventArgs args) { if (audiobooks is not null) await RunUserActionAsync(AudiobooksPane.RemoveSelectedAsync); }
    private void AudiobookCancel(object? sender, RoutedEventArgs args) => audiobooks?.CancelOperation();

    // Help.

    private async void OpenProjectWebsite(object? sender, RoutedEventArgs args) =>
        await RunUserActionAsync(async () => { await Launcher.LaunchUriAsync(AboutInfo.ProjectUri); });

    private async void ShowAbout(object? sender, RoutedEventArgs args)
    {
        if (!CanRunUserAction || aboutOpen) return;
        aboutOpen = true;
        try { await new AboutWindow(AboutInfo.Collect(viewModel, mediaSession is not null)).ShowDialog<bool>(this); }
        finally { aboutOpen = false; }
    }

    private async void ShowShortcuts(object? sender, RoutedEventArgs args)
    {
        if (!CanRunUserAction || shortcutsOpen) return;
        shortcutsOpen = true;
        try { await new ShortcutsWindow(ShortcutCatalog.Create()).ShowDialog<bool>(this); }
        finally { shortcutsOpen = false; }
    }

    private bool shortcutsOpen;
    private bool aboutOpen;
}
