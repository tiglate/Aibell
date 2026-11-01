using Aibell.Desktop.Localization;
using Aibell.Application.Playlists;
using Aibell.Domain.Playlists;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? playlistPreparation;
    public bool CanImportPlaylistFile => playlistFiles is not null && !IsBusy;
    public bool CanExportPlaylistFile => playlistFiles is not null && !IsBusy && SelectedPlaylist?.Id is not null;
    public void CancelPlaylistPreparation() => playlistPreparation?.Cancel();

    public Task<PlaylistImportPreview> PreparePlaylistFileAsync(Uri source, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(async () =>
        {
            if (playlistFiles is null) throw new NotSupportedException("Playlist file import is unavailable.");
            if (IsBusy) throw new InvalidOperationException("Wait for the current library operation.");
            using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            playlistPreparation = operation; IsBusy = true;
            try { return await playlistFiles.PrepareAsync(source, operation.Token); }
            finally { playlistPreparation = null; IsBusy = false; }
        });

    public Task<Playlist> ImportPlaylistFileAsync(string name, PlaylistImportPreview preview, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(async () =>
        {
            if (playlistFiles is null) throw new NotSupportedException("Playlist file import is unavailable.");
            if (IsBusy) throw new InvalidOperationException("Wait for the current library operation.");
            IsBusy = true;
            try
            {
                Playlist imported = await playlistFiles.ImportAsync(name, preview, cancellationToken);
                // Once SQLite commits, cancellation cannot turn success into an unchanged-library report.
                try
                {
                    await RefreshPlaylistsAsync(CancellationToken.None, imported.Id);
                    await RefreshLibraryFacetsAsync(CancellationToken.None);
                    await SelectPlaylistAsync(Playlists.Single(item => item.Id == imported.Id), CancellationToken.None);
                    StatusMessage = Loc.Format("PlaylistFile.Imported", imported.Name, imported.TrackIds.Count, preview.Issues.Count, preview.Duplicates);
                }
                catch (Exception exception)
                { StatusMessage = Loc.Format("PlaylistFile.ImportedNoRefresh", imported.Name, exception.Message); }
                return imported;
            }
            finally { IsBusy = false; }
        });

    public Task ExportPlaylistFileAsync(Guid playlistId, Uri destination, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(async () =>
        {
            if (playlistFiles is null) throw new NotSupportedException("Playlist file export is unavailable.");
            if (IsBusy) throw new InvalidOperationException("Wait for the current library operation.");
            IsBusy = true;
            try
            {
                var tracks = await playlistRepository.GetTracksAsync(playlistId, "", cancellationToken);
                await playlistFiles.ExportAsync(destination, tracks, cancellationToken);
                StatusMessage = Loc.Format("PlaylistFile.Exported", tracks.Count, destination.LocalPath);
            }
            finally { IsBusy = false; }
        });
}
