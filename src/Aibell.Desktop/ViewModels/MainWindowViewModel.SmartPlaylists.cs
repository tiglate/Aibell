using Aibell.Desktop.Localization;
using Aibell.Domain.Playlists;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task CreateSmartPlaylistAsync(string name, SmartPlaylistDefinition definition,
        CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(async () =>
        {
            Playlist playlist = await playlistRepository.CreateSmartAsync(name, definition, cancellationToken);
            await RefreshPlaylistsAsync(cancellationToken, playlist.Id);
            SetActiveView(LibraryViewKind.Playlist);
            SetSort(TrackSortField.Default);
            ReplaceTracks(await QueryVisibleTracksAsync(cancellationToken), autoFitColumns: true);
            StatusMessage = Loc.Format("Smart.Created", playlist.Name, FormatResultCount(Tracks.Count));
        });
    }

    public async Task UpdateSelectedSmartPlaylistAsync(SmartPlaylistDefinition definition,
        CancellationToken cancellationToken = default)
    {
        if (SelectedPlaylist is not { Id: Guid id, IsSmart: true }) return;
        await RunBusyAsync(async () =>
        {
            await playlistRepository.UpdateSmartDefinitionAsync(id, definition, cancellationToken);
            await RefreshPlaylistsAsync(cancellationToken, id);
            ReplaceTracks(await QueryVisibleTracksAsync(cancellationToken), autoFitColumns: true);
            StatusMessage = Loc.Format("Smart.Updated", FormatResultCount(Tracks.Count));
        });
    }

    private async Task RefreshSmartPlaylistsAsync(CancellationToken cancellationToken)
    {
        if (!EditablePlaylists.Any(playlist => playlist.IsSmart)) return;
        await RefreshPlaylistsAsync(cancellationToken);
        if (SelectedPlaylist?.IsSmart == true)
            ReplaceTracks(await QueryVisibleTracksAsync(cancellationToken));
    }
}
