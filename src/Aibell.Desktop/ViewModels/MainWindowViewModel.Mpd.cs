using System.ComponentModel;
using Avalonia.Threading;
using Aibell.Application.Library;
using Aibell.Desktop.Localization;
using Aibell.Desktop.Services;
using Aibell.Domain.Media;

namespace Aibell.Desktop.ViewModels;

/// <summary>
/// MPD as a source for the Music view. While it is selected the genre, artist and album lists and the track list show the server's
/// library (and, when one is chosen in the Playlists section, a stored playlist of the server); the transport bar, play queue and
/// everything else are the ordinary ones, because MPD tracks play through <see cref="MpdPlaybackEngine"/>.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private IMpdLibrarySource? mpdSource;
    private MpdLibraryBrowser? mpdBrowser;
    private bool isMpdSource;
    private string? mpdPlaylist;

    /// <summary>True while MPD is the selected library source.</summary>
    public bool IsMpdView => isMpdSource;

    /// <summary>The Music view showing the local library (not MPD's).</summary>
    public bool IsLocalMusicView => IsMusicView && !isMpdSource;

    /// <summary>The server playlist whose songs are listed, or <c>null</c> for the whole server library.</summary>
    public string? SelectedMpdPlaylistName => mpdPlaylist;

    public bool CanUseMpd => mpdBrowser is not null;

    private ILibraryBrowser ActiveBrowser => isMpdSource && mpdBrowser is not null ? mpdBrowser : libraryBrowser;

    /// <summary>Tells the view model where MPD's songs come from. Without it MPD is unavailable.</summary>
    public void ConfigureMpd(IMpdLibrarySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        mpdSource = source;
        mpdBrowser = new MpdLibraryBrowser(source);
    }

    /// <summary>True while the volume bar and mute button act on the MPD server (MPD selected and connected, or an MPD song loaded).</summary>
    private bool ServerVolumeActive => playbackEngine is MpdPlaybackRouter { ControlsServerVolume: true };

    private void NotifyMpdChanged()
    {
        if (playbackEngine is MpdPlaybackRouter router) router.ServerSelected = isMpdSource;
        OnPropertyChanged(nameof(IsMpdView));
        OnPropertyChanged(nameof(IsLocalMusicView));
        OnPropertyChanged(nameof(SelectedMpdPlaylistName));
        OnPropertyChanged(nameof(IsColumnBrowserVisible));
        OnPropertyChanged(nameof(IsMusicView));
        OnPropertyChanged(nameof(CanRemoveFromPlaylist));
    }

    /// <summary>Selects MPD in the Library list: the Music view then shows the server's library (or <paramref name="playlist"/>).</summary>
    public Task EnterMpdAsync(string? playlist = null, CancellationToken cancellationToken = default)
    {
        if (mpdBrowser is null) return Task.CompletedTask;
        return RunLibraryOperation(() => EnterMpdCoreAsync(playlist, cancellationToken));
    }

    private async Task EnterMpdCoreAsync(string? playlist, CancellationToken cancellationToken)
    {
        try
        {
            await LoadMpdLibraryAsync(playlist, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ReportMpdFailure(exception);
        }
    }

    /// <summary>
    /// A problem while reading the server's library: the status line gets the friendly sentence and the error window offers the
    /// technical details (exception, stack trace, environment) behind its Details button.
    /// </summary>
    private void ReportMpdFailure(Exception exception, string? friendly = null)
    {
        friendly ??= Loc.Get("Mpd.Error.Library");
        StatusMessage = friendly;
        errorReporter?.Report(exception, "mpd", DescribeEnvironment(), friendly);
    }

    private async Task LoadMpdLibraryAsync(string? playlist, CancellationToken cancellationToken)
    {
        bool entering = !isMpdSource;
        isMpdSource = true;
        mpdPlaylist = playlist;
        SelectedPlaylist = Playlists.FirstOrDefault();
        SetActiveView(playlist is null ? LibraryViewKind.AllMusic : LibraryViewKind.Playlist);
        NotifyMpdChanged();
        if (entering || playlist is null) mpdBrowser!.Invalidate();
        if (entering) await RefreshLibraryFacetsAsync(cancellationToken);
        else if (playlist is null) await ResetBrowserFiltersAsync(cancellationToken);
        SetSort(playlist is null ? TrackSortField.Default : TrackSortField.PlaylistOrder);
        IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
        ReplaceTracks(tracks, autoFitColumns: true);
        StatusMessage = mpdSource is { IsConnected: false } ? Loc.Get("Mpd.NotConnected") : FormatResultCount(tracks.Count);
    }

    /// <summary>Shows a stored playlist of the server in the track list; <c>null</c> goes back to the server's whole library.</summary>
    public Task SelectMpdPlaylistAsync(string? name, CancellationToken cancellationToken = default)
    {
        if (!isMpdSource || string.Equals(mpdPlaylist, name, StringComparison.Ordinal)) return Task.CompletedTask;
        return RunLibraryOperation(() => EnterMpdCoreAsync(name, cancellationToken));
    }

    /// <summary>The server's library or playlists changed (connected, database updated, disconnected): fetch them again.</summary>
    public Task RefreshMpdLibraryAsync(CancellationToken cancellationToken = default)
    {
        if (!isMpdSource || mpdBrowser is null) return Task.CompletedTask;
        return RunLibraryOperation(async () =>
        {
            try
            {
                mpdBrowser.Invalidate();
                if (mpdPlaylist is null) await RefreshLibraryFacetsAsync(cancellationToken);
                IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
                ReplaceTracks(tracks, autoFitColumns: true);
                StatusMessage = mpdSource is { IsConnected: false } ? Loc.Get("Mpd.NotConnected") : FormatResultCount(tracks.Count);
            }
            catch (InvalidOperationException exception)
            {
                ReportMpdFailure(exception);
            }
        });
    }

    /// <summary>Selecting something else in the Library list: stop showing MPD's data. Call <see cref="ReloadLocalLibraryAsync"/> afterwards.</summary>
    public void LeaveMpdState()
    {
        if (!isMpdSource) return;
        isMpdSource = false;
        mpdPlaylist = null;
        NotifyMpdChanged();
    }

    /// <summary>Puts the local library back into the genre, artist, album and track lists.</summary>
    public Task ReloadLocalLibraryAsync(CancellationToken cancellationToken = default) => RunLibraryOperation(async () =>
    {
        if (isMpdSource) return;
        await RefreshLibraryFacetsAsync(cancellationToken);
        if (activeView is LibraryViewKind.AllMusic or LibraryViewKind.Playlist)
        {
            SelectedPlaylist = Playlists.FirstOrDefault();
            SetActiveView(LibraryViewKind.AllMusic);
            SetSort(TrackSortField.Default);
            IReadOnlyList<Track> tracks = await QueryVisibleTracksAsync(cancellationToken);
            ReplaceTracks(tracks, autoFitColumns: true);
            StatusMessage = FormatResultCount(tracks.Count);
        }
    });

    private Task<IReadOnlyList<Track>>? QueryMpdPlaylistTracks(CancellationToken cancellationToken) =>
        isMpdSource && mpdPlaylist is { } name && mpdBrowser is { } browser ? browser.GetPlaylistTracksAsync(name, SearchQuery, cancellationToken) : null;

    /// <summary>Takes the selected song out of the server playlist that is showing (one entry of it, if it is there more than once).</summary>
    private async Task RemoveSelectedMpdTrackAsync(CancellationToken cancellationToken)
    {
        if (mpdSource is null || mpdPlaylist is not { } name || SelectedTrack is not { } selected ||
            !MpdTracks.TryGetSongUri(selected.Source.Location, out string songUri)) return;
        try
        {
            await mpdSource.RemoveFromPlaylistAsync(name, songUri, cancellationToken);
            await RefreshMpdLibraryAsync(cancellationToken);
            StatusMessage = Loc.Format("Playlist.RemovedFrom", selected.Title, name);
        }
        catch (InvalidOperationException exception)
        {
            ReportMpdFailure(exception, exception.Message);
        }
    }

    /// <summary>Plays a stored playlist of the server from its first song, or adds it to the play queue.</summary>
    public async Task PlayMpdPlaylistAsync(string name, bool queueOnly, CancellationToken cancellationToken = default)
    {
        if (mpdBrowser is null) return;
        IReadOnlyList<Track> tracks = await mpdBrowser.GetPlaylistTracksAsync(name, "", cancellationToken);
        if (tracks.Count == 0) return;
        if (queueOnly)
        {
            await Dispatcher.UIThread.InvokeAsync(() => QueueTracks(tracks));
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() => RunPlaybackOperation(async () =>
        {
            long stopGeneration = playbackStopGeneration;
            ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            if (changingTrack) throw new InvalidOperationException("Wait for the current playback transition to finish.");
            QueueTracks(tracks);
            await PlayQueueEntryCoreAsync(playQueue.Items[^tracks.Count].Id, cancellationToken, propagateFailure: true);
        }));
    }
}
