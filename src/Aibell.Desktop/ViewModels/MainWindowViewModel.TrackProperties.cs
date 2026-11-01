using Aibell.Desktop.Localization;
using Aibell.Application.Library;
using Aibell.Domain.Media;

namespace Aibell.Desktop.ViewModels;

public sealed record TrackPropertiesData(Track Track, TrackMetadata Metadata, TrackFileDetails? File,
    Uri? Artwork, string? Notice = null, bool CanWriteFile = false, MetadataFileBackup? FileBackup = null, bool CanManageBackups = false);

public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<Guid, TrackMetadata> editedMetadata = [];
    private readonly Dictionary<Guid, Track> refreshedTracks = [];
    private Task pendingMetadataOperation = Task.CompletedTask;

    private Task BeginMetadataOperation(Func<Task> operation)
    {
        if (IsBusy) return Task.FromException(new InvalidOperationException("Wait for the current library operation to finish."));
        pendingMetadataOperation = operation();
        return pendingMetadataOperation;
    }

    private Task<T> BeginMetadataOperation<T>(Func<Task<T>> operation)
    {
        if (IsBusy) return Task.FromException<T>(new InvalidOperationException("Wait for the current library operation to finish."));
        Task<T> task = operation();
        pendingMetadataOperation = task;
        return task;
    }

    public async Task WaitForMetadataOperationsAsync()
    {
        try { await pendingMetadataOperation; }
        catch (Exception exception) { StatusMessage = Loc.Format("TrackProps.OperationError", exception.Message); }
    }


    public async Task<TrackPropertiesData> GetTrackPropertiesAsync(Track track, CancellationToken cancellationToken = default)
    {
        TrackFileDetails? file = null;
        Uri? artwork = null;
        string? notice = null;
        if (trackFileDetailsReader is not null)
        {
            try { file = await trackFileDetailsReader.ReadAsync(track.Location, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { notice = Loc.Format("TrackProps.DetailsUnavailable", exception.Message); }
        }
        if (artworkProvider is not null)
        {
            try { artwork = await artworkProvider.GetAsync(track.Location, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException) { /* Keep the cover placeholder. */ }
        }
        TrackMetadata metadata = TrackMetadata.FromTrack(track);
        if (track.Metadata is null && file is not null)
            metadata = metadata with
            {
                TrackTotal = file.Metadata.TrackTotal,
                DiscTotal = file.Metadata.DiscTotal,
                Conductor = file.Metadata.Conductor,
                Grouping = file.Metadata.Grouping,
                Copyright = file.Metadata.Copyright,
                LicenseUri = file.Metadata.LicenseUri,
                SortTitle = file.Metadata.SortTitle,
                SortArtist = file.Metadata.SortArtist,
                SortAlbumArtist = file.Metadata.SortAlbumArtist,
                SortAlbum = file.Metadata.SortAlbum
            };
        MetadataFileBackup? backup = null;
        bool canWrite = metadataFileWriter is not null && file?.SourceHash is not null && file.FileWriteRestriction is null && track.Location.IsFile &&
            new[] { ".flac", ".m4a", ".ogg", ".opus", ".mp3", ".wav" }.Contains(Path.GetExtension(track.Location.LocalPath), StringComparer.OrdinalIgnoreCase);
        if (canWrite)
        {
            try
            {
                IReadOnlyList<MetadataFileBackup> backups = await metadataFileWriter!.GetBackupsAsync(track.Location, cancellationToken);
                backup = backups.Count > 0 ? backups[0] : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            { notice = Loc.Format("TrackProps.BackupsUnavailable", exception.Message); }
        }
        notice ??= file?.FileWriteRestriction;
        return new(track, metadata, file, artwork, notice, canWrite, backup,
            metadataFileWriter is IMetadataBackupManager && file?.SourceHash is not null && track.Location.IsFile);
    }

    public Task SaveTrackMetadataAsync(Guid id, TrackMetadata metadata, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(() => SaveTrackMetadataCoreAsync(id, metadata, cancellationToken));

    private async Task SaveTrackMetadataCoreAsync(Guid id, TrackMetadata metadata, CancellationToken cancellationToken)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current library operation to finish.");
        IsBusy = true;
        try
        {
            await SaveAndRefreshMetadataAsync(id, metadata, cancellationToken);
            StatusMessage = Loc.Get("TrackProps.SavedLibrary");
        }
        finally { IsBusy = false; }
    }

    public Task SaveTrackMetadataToFileAsync(TrackPropertiesData data, TrackMetadata metadata, CancellationToken cancellationToken = default) =>
        SaveTrackMetadataToFileAsync(data, metadata, null, cancellationToken);

    public Task SaveTrackMetadataToFileAsync(TrackPropertiesData data, TrackMetadata metadata, ArtworkChange? artwork, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(() => SaveTrackMetadataToFileCoreAsync(data, metadata, artwork, cancellationToken));

    private async Task SaveTrackMetadataToFileCoreAsync(TrackPropertiesData data, TrackMetadata metadata, ArtworkChange? artwork, CancellationToken cancellationToken)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current library operation to finish.");
        if (repository is not ITrackMetadataRepository) throw new NotSupportedException("The library does not support metadata editing.");
        if (!data.CanWriteFile || metadataFileWriter is null || data.File?.SourceHash is null)
            throw new NotSupportedException("File writing is available for validated FLAC, M4A, MP3, Ogg, Opus and classic PCM WAV files.");
        if (changingTrack) throw new InvalidOperationException("Wait for playback to finish changing tracks.");
        EnsureFileIsNotPlaying(data.Track);
        MetadataEdit? patch = TrackFileEdit.Between(data.Metadata, metadata);
        if (artwork is not null) patch = (patch ?? new MetadataEdit()) with { Artwork = artwork };
        patch?.Validate();
        if (patch is not null) TrackFileEdit.ValidateFormat(patch, Path.GetExtension(data.Track.Location.LocalPath));
        IsBusy = true; changingTrack = true;
        MetadataFileWriteResult? result = null;
        try
        {
            if (patch is not null)
                result = await metadataFileWriter.WriteAsync(data.Track.Location, patch, data.File.SourceHash, cancellationToken);
            // Once replacement succeeds, finish catalog reconciliation even if the window is closing.
            await SaveAndRefreshMetadataAsync(data.Track.Id, metadata, result is null ? cancellationToken : CancellationToken.None,
                result is null ? null : FileState(data.Track.Location));
            StatusMessage = result is null ? Loc.Get("TrackProps.SavedNoTags") :
                Loc.Format("TrackProps.SavedFile", result.Backup.Backup.LocalPath) +
                (result.Warning is null ? "" : " — " + result.Warning);
        }
        catch (Exception exception) when (result is not null)
        {
            throw new IOException(Loc.Format("TrackProps.SavedNoRefresh", result.Backup.Backup.LocalPath), exception);
        }
        finally
        {
            IsBusy = false;
            CompletePlaybackTransition();
        }
    }

    public Task RestoreTrackFileAsync(TrackPropertiesData data, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(() => RestoreTrackFileCoreAsync(data, cancellationToken));

    private async Task RestoreTrackFileCoreAsync(TrackPropertiesData data, CancellationToken cancellationToken)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current library operation to finish.");
        if (repository is not ITrackMetadataRepository) throw new NotSupportedException("The library does not support metadata editing.");
        if (metadataFileWriter is null || data.FileBackup is null || trackFileDetailsReader is null)
            throw new NotSupportedException("No verified file backup is available.");
        if (changingTrack) throw new InvalidOperationException("Wait for playback to finish changing tracks.");
        EnsureFileIsNotPlaying(data.Track);
        IsBusy = true; changingTrack = true;
        MetadataFileWriteResult? result = null;
        try
        {
            result = await metadataFileWriter.RestoreAsync(data.FileBackup, cancellationToken);
            TrackFileDetails restored = await trackFileDetailsReader.ReadAsync(data.Track.Location, CancellationToken.None);
            TrackMetadata metadata = TrackFileEdit.ReconcileRestored(data.Metadata, restored.Metadata,
                Path.GetExtension(data.Track.Location.LocalPath));
            await SaveAndRefreshMetadataAsync(data.Track.Id, metadata, CancellationToken.None, FileState(data.Track.Location));
            StatusMessage = Loc.Get("TrackProps.Restored") +
                (result.Warning is null ? "" : " " + result.Warning);
        }
        catch (Exception exception) when (result is not null)
        { throw new IOException(Loc.Format("TrackProps.RestoredNoRefresh", exception.Message), exception); }
        finally
        {
            IsBusy = false;
            CompletePlaybackTransition();
        }
    }

    public Task<MetadataBackupScan> GetTrackBackupsAsync(Track track, CancellationToken cancellationToken = default) =>
        metadataFileWriter is IMetadataBackupManager manager ? manager.InspectBackupsAsync(track.Location, cancellationToken)
            : Task.FromException<MetadataBackupScan>(new NotSupportedException("Backup management is unavailable."));

    public Task DeleteTrackBackupAsync(MetadataBackupInfo backup, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(async () =>
        {
            if (metadataFileWriter is not IMetadataBackupManager manager) throw new NotSupportedException("Backup management is unavailable.");
            IsBusy = true;
            try { await manager.DeleteBackupAsync(backup, cancellationToken); StatusMessage = Loc.Get("TrackProps.BackupDeleted"); }
            finally { IsBusy = false; }
        });

    private void EnsureFileIsNotPlaying(Track track)
    {
        if (playbackEngine.Snapshot is { Track: { } playing, Status: not Aibell.Application.Playback.PlaybackStatus.Stopped } &&
            playing.Location == track.Location)
            throw new InvalidOperationException("Stop playback of this track before writing or restoring its file.");
    }

    private static TrackFileState FileState(Uri location)
    {
        FileInfo file = new(location.LocalPath);
        return new(file.Length, file.LastWriteTimeUtc);
    }

    private async Task SaveAndRefreshMetadataAsync(Guid id, TrackMetadata metadata, CancellationToken cancellationToken, TrackFileState? fileState = null)
    {
        if (repository is not ITrackMetadataRepository writer) throw new NotSupportedException("The library does not support metadata editing.");
        Track updated = await writer.SaveMetadataAsync(id, metadata, fileState, cancellationToken);
        editedMetadata[id] = metadata;
        refreshedTracks[id] = updated;
        await RefreshPlaylistsAsync(cancellationToken, SelectedPlaylist?.Id);
        await RefreshLibraryFacetsAsync(cancellationToken);
        ReplaceTracks(await QueryVisibleTracksAsync(cancellationToken));
        playQueue.UpdateTrack(updated); RefreshPlayQueue();
        ApplyPlaybackSnapshot(playbackEngine.Snapshot);
    }
}
