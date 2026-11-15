using System.Collections.Specialized;
using Aibell.Desktop.Localization;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private readonly Func<MpdViewModel>? mpdViewModelFactory;
    private MpdViewModel? mpd;
    private MpdConfigWindow? mpdConfigWindow;
    private bool syncingMpdPlaylists;

    private async void OpenMpd(object? sender, RoutedEventArgs args)
    {
        if (closeInProgress || disposed || mpdViewModelFactory is null) return;
        // Created on first use and then owned by the window until it closes: selecting something else only hides its panels. Selecting
        // MPD does not connect to anything; the user presses Connect.
        if (mpd is null)
        {
            mpd = mpdViewModelFactory();
            viewModel.ConfigureMpd(mpd);
            MpdPane.DataContext = mpd;
            MpdPane.OpenSettings = OpenMpdConfigWindow;
            mpd.Playlists.CollectionChanged += (_, _) => SyncMpdPlaylists();
            mpd.PlaylistsChanged += (_, _) => SyncMpdPlaylists();
            mpd.LibraryChanged += async (_, _) => await viewModel.RefreshMpdLibraryAsync();
        }
        mpd.SelectPlaylist(null);
        await viewModel.EnterMpdAsync();
        SyncMpdPlaylists();
    }

    /// <summary>Shows the server's stored playlists in the sidebar's Playlists section while MPD is selected.</summary>
    private void SyncMpdPlaylists()
    {
        if (mpd is not { } model || syncingMpdPlaylists) return;
        syncingMpdPlaylists = true;
        try
        {
            PlaylistFilterViewModel[] items = [.. model.Playlists.Select(row => new PlaylistFilterViewModel(null, row.Name, null))];
            MpdPlaylistList.ItemsSource = items;
            MpdPlaylistList.SelectedItem = items.FirstOrDefault(item => item.Name == model.SelectedPlaylist?.Name);
        }
        finally
        {
            syncingMpdPlaylists = false;
        }
    }

    private async void MpdPlaylistChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (syncingMpdPlaylists || mpd is not { } model) return;
        string? name = (MpdPlaylistList.SelectedItem as PlaylistFilterViewModel)?.Name;
        model.SelectPlaylist(name);
        await viewModel.SelectMpdPlaylistAsync(name);
    }

    private void MpdPlaylistItemPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (sender is Control { DataContext: PlaylistFilterViewModel item }) MpdPlaylistList.SelectedItem = item;
    }

    private async void MpdPlaylistPlay(object? sender, RoutedEventArgs args)
    {
        if (mpd is { SelectedPlaylist: { } playlist }) await RunUserActionAsync(() => viewModel.PlayMpdPlaylistAsync(playlist.Name, queueOnly: false));
    }

    private async void MpdPlaylistQueue(object? sender, RoutedEventArgs args)
    {
        if (mpd is { SelectedPlaylist: { } playlist }) await RunUserActionAsync(() => viewModel.PlayMpdPlaylistAsync(playlist.Name, queueOnly: true));
    }

    private async void MpdPlaylistRename(object? sender, RoutedEventArgs args)
    {
        if (mpd is not { SelectedPlaylist: { } playlist } model) return;
        string? name = await ShowTextPromptAsync(Loc.Get("Dialog.RenamePlaylist"), Loc.Get("Dialog.PlaylistName"), playlist.Name, Loc.Get("Dialog.Rename"));
        if (!string.IsNullOrWhiteSpace(name)) await RunUserActionAsync(() => model.RenamePlaylistAsync(playlist, name));
    }

    private async void MpdPlaylistDelete(object? sender, RoutedEventArgs args)
    {
        if (mpd is not { SelectedPlaylist: { } playlist } model) return;
        if (!await DialogChrome.ConfirmAsync(this, Loc.Get("Dialog.DeletePlaylist"), Loc.Format("Mpd.DeletePlaylistPrompt", playlist.Name),
                Loc.Get("Dialog.Delete"), "ConfirmDeleteMpdPlaylist")) return;
        await RunUserActionAsync(() => model.DeletePlaylistAsync(playlist));
    }

    private static string? SongPath(TrackRowViewModel? track) => track is not null && MpdTracks.TryGetSongUri(track.Source.Location, out string path) ? path : null;

    /// <summary>The Playlists section's + button while MPD is selected: a new server playlist holding the selected song.</summary>
    private async Task CreateMpdPlaylistFromSelectionAsync()
    {
        if (mpd is null || SongPath(viewModel.SelectedTrack) is not { } path) return;
        string? name = await ShowTextPromptAsync(Loc.Get("Dialog.CreatePlaylist"), Loc.Get("Dialog.PlaylistName"), string.Empty, Loc.Get("Dialog.Save"));
        if (!string.IsNullOrWhiteSpace(name)) await RunUserActionAsync(() => mpd.AddToPlaylistAsync(name, [path]));
    }

    /// <summary>The "Add to Playlist" menu of the track list while MPD is selected: the server's playlists instead of the local ones.</summary>
    private List<Control> BuildMpdAddToPlaylistItems(TrackRowViewModel? track)
    {
        List<Control> items = [];
        string? path = SongPath(track);
        foreach (MpdPlaylistRow playlist in mpd?.Playlists ?? [])
        {
            MenuItem item = new() { Header = playlist.Name, Tag = playlist.Name, IsEnabled = path is not null };
            item.Click += async (_, _) =>
            {
                if (mpd is not null && path is not null) await RunUserActionAsync(() => mpd.AddToPlaylistAsync(playlist.Name, [path]));
            };
            items.Add(item);
        }
        if (items.Count == 0) items.Add(new MenuItem { Header = Loc.Get("Menu.NoPlaylists"), IsEnabled = false });
        items.Add(new Separator());
        MenuItem create = new() { Header = Loc.Get("Menu.NewPlaylist"), IsEnabled = path is not null };
        create.Click += async (_, _) => await CreateMpdPlaylistFromSelectionAsync();
        items.Add(create);
        return items;
    }

    private void OpenMpdConfigWindow()
    {
        if (closeInProgress || disposed) return;
        if (mpdConfigWindow is { } existing) { existing.Activate(); return; }
        MpdConfigWindow opened = new(filePicker);
        mpdConfigWindow = opened;
        opened.Closed += (_, _) => { if (ReferenceEquals(mpdConfigWindow, opened)) mpdConfigWindow = null; };
        opened.Show(this);
    }

    private async Task ShutdownMpdAsync()
    {
        if (mpdConfigWindow is { } config) config.Close();
        if (mpd is not { } model) return;
        await model.ShutdownAsync();
        model.Dispose();
    }

    private void RefreshMpdMenu()
    {
        MpdViewModel? model = viewModel.IsMpdView ? mpd : null;
        MenuMpd.IsEnabled = model is not null || mpdViewModelFactory is not null;
        MenuMpdConnect.IsEnabled = model is { CanConnect: true };
        MenuMpdDisconnect.IsEnabled = model is { CanDisconnect: true };
        MenuMpdRefresh.IsEnabled = model is { CanControl: true };
        MenuMpdUpdate.IsEnabled = model is { CanControl: true, IsUpdatingDatabase: false };
        MenuMpdSettings.IsEnabled = true;
    }

    private async void MpdConnect(object? sender, RoutedEventArgs args) { if (mpd is { } m) await RunUserActionAsync(m.ConnectAsync); }
    private async void MpdDisconnect(object? sender, RoutedEventArgs args) { if (mpd is { } m) await RunUserActionAsync(m.DisconnectAsync); }
    private async void MpdRefresh(object? sender, RoutedEventArgs args) { if (mpd is { } m) await RunUserActionAsync(async () => { await m.RefreshAsync(); await viewModel.RefreshMpdLibraryAsync(); }); }
    private async void MpdUpdateDatabase(object? sender, RoutedEventArgs args) { if (mpd is { } m) await RunUserActionAsync(m.UpdateDatabaseAsync); }
    private void OpenMpdSettings(object? sender, RoutedEventArgs args) => OpenMpdConfigWindow();
}
