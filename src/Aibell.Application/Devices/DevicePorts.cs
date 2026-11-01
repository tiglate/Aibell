using Aibell.Domain.Media;

namespace Aibell.Application.Devices;

/// <summary>A mounted removable volume (USB stick, SD card, mass-storage player) that music can be copied to.</summary>
public sealed record RemovableVolume(string Id, string Name, string RootPath, long TotalBytes, long FreeBytes, string FileSystem);

/// <summary>Lists the removable volumes that are mounted right now. Implementations never throw for a volume that disappears.</summary>
public interface IRemovableVolumeService
{
    IReadOnlyList<RemovableVolume> List();
}

public enum SyncAction
{
    /// <summary>The file is not on the device yet.</summary>
    Copy,
    /// <summary>The device already has an identical file (same size and modification time).</summary>
    UpToDate,
    /// <summary>The device has a different file at the destination; it is replaced only when the options allow.</summary>
    Conflict
}

public sealed record SyncItem(Track Track, string SourcePath, string RelativePath, long Length, SyncAction Action);

public sealed record SyncExclusion(Track Track, string Reason);

public sealed record DeviceSyncOptions(string MusicFolder = "Music", bool WritePlaylist = true, string PlaylistName = "Aibell", bool ReplaceDifferent = false);

public sealed record DeviceSyncPlan(RemovableVolume Volume, string MusicRoot, DeviceSyncOptions Options, IReadOnlyList<SyncItem> Items, IReadOnlyList<SyncExclusion> Excluded)
{
    public int CopyCount => Items.Count(item => item.Action == SyncAction.Copy);

    public int UpToDateCount => Items.Count(item => item.Action == SyncAction.UpToDate);

    public int ConflictCount => Items.Count(item => item.Action == SyncAction.Conflict);

    /// <summary>Bytes that will actually be written: new files, plus conflicting ones when they are to be replaced.</summary>
    public long BytesToCopy => Items.Where(item => item.Action == SyncAction.Copy || (item.Action == SyncAction.Conflict && Options.ReplaceDifferent)).Sum(item => item.Length);

    public bool Fits => BytesToCopy <= Volume.FreeBytes;
}

public sealed record DeviceSyncProgress(int Completed, int Total, long BytesCopied, long BytesTotal, string? Current);

public sealed record DeviceSyncResult(int Copied, int Replaced, int UpToDate, int Conflicts, IReadOnlyList<string> Failures, string? PlaylistPath, bool Cancelled);

/// <summary>
/// Copies a list of library tracks onto a removable volume. It only ever adds files (and replaces a conflicting file when the user
/// asked for that): nothing on the device is deleted, and nothing is changed in the library.
/// </summary>
public interface IDeviceSyncService
{
    /// <summary>Works out what a sync would do without touching the device.</summary>
    DeviceSyncPlan Plan(RemovableVolume volume, IReadOnlyList<Track> tracks, DeviceSyncOptions options);

    Task<DeviceSyncResult> ExecuteAsync(DeviceSyncPlan plan, IProgress<DeviceSyncProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Where a device sync takes its tracks from: what the user has selected, what the library view shows, or everything.</summary>
public interface IDeviceTrackSource
{
    IReadOnlyList<Track> SelectedTracks();

    IReadOnlyList<Track> VisibleTracks();

    Task<IReadOnlyList<Track>> AllTracksAsync(CancellationToken cancellationToken = default);
}
