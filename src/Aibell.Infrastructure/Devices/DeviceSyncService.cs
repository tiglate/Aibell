using System.Text;
using Aibell.Application.Devices;
using Aibell.Domain.Media;

namespace Aibell.Infrastructure.Devices;

/// <summary>
/// Copies library files onto a removable volume under <c>Music/Artist/Album/NN - Title.ext</c> (see <see cref="DevicePathBuilder"/>).
/// It only adds: a file that is already there with the same size and modification time (two seconds of slack for FAT) is left
/// alone, a different file at the destination is reported as a conflict and replaced only when asked, and nothing is ever deleted.
/// Each file goes to a temporary name first and is moved into place, so a pulled cable never leaves a half-written song under
/// its real name. One failing file does not stop the others; cancellation stops after the current buffer and removes the partial file.
/// </summary>
public sealed class DeviceSyncService : IDeviceSyncService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wav"
    };
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);
    private const string PartialSuffix = ".aibell-part";
    private const int BufferSize = 1024 * 1024;

    private readonly Func<string, long?> freeSpace;

    public DeviceSyncService() : this(ReadFreeSpace) { }

    /// <summary>Takes the free-space probe so tests can simulate a full device.</summary>
    internal DeviceSyncService(Func<string, long?> freeSpace) => this.freeSpace = freeSpace;

    public DeviceSyncPlan Plan(RemovableVolume volume, IReadOnlyList<Track> tracks, DeviceSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(options);
        string root = Path.GetFullPath(volume.RootPath);
        string folder = DevicePathBuilder.Segment(options.MusicFolder, "Music");
        string musicRoot = Path.GetFullPath(Path.Combine(root, folder));
        DeviceSyncOptions safe = options with { MusicFolder = folder, PlaylistName = DevicePathBuilder.Segment(options.PlaylistName, "Aibell") };
        List<SyncItem> items = [];
        List<SyncExclusion> excluded = [];
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        foreach (Track track in tracks)
        {
            if (!track.Location.IsAbsoluteUri || !track.Location.IsFile) { excluded.Add(new(track, "NotAFile")); continue; }
            string source = track.Location.LocalPath;
            string extension = Path.GetExtension(source);
            if (!File.Exists(source)) { excluded.Add(new(track, "Missing")); continue; }
            if (!SupportedExtensions.Contains(extension)) { excluded.Add(new(track, "Unsupported")); continue; }
            string relative = Unique(DevicePathBuilder.Relative(track, folder, extension), used);
            string destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(musicRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { excluded.Add(new(track, "Unsafe")); continue; }
            FileInfo sourceInfo = new(source);
            SyncAction action = SyncAction.Copy;
            if (File.Exists(destination))
            {
                FileInfo existing = new(destination);
                action = existing.Length == sourceInfo.Length && (existing.LastWriteTimeUtc - sourceInfo.LastWriteTimeUtc).Duration() <= TimeTolerance
                    ? SyncAction.UpToDate : SyncAction.Conflict;
            }
            items.Add(new SyncItem(track, source, relative, sourceInfo.Length, action));
        }
        return new DeviceSyncPlan(volume, musicRoot, safe, items, excluded);
    }

    public async Task<DeviceSyncResult> ExecuteAsync(DeviceSyncPlan plan, IProgress<DeviceSyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string root = Path.GetFullPath(plan.Volume.RootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The device is no longer available.");
        if (freeSpace(root) is { } free && plan.BytesToCopy > free)
            throw new IOException($"The device has {free / 1048576} MiB free but {plan.BytesToCopy / 1048576} MiB are needed.");

        List<SyncItem> work = [.. plan.Items.Where(item => item.Action == SyncAction.Copy || (item.Action == SyncAction.Conflict && plan.Options.ReplaceDifferent))];
        long total = work.Sum(item => item.Length);
        long copiedBytes = 0;
        int copied = 0, replaced = 0, completed = 0;
        List<string> failures = [];
        HashSet<string> onDevice = new(StringComparer.OrdinalIgnoreCase);
        foreach (SyncItem item in plan.Items.Where(item => item.Action == SyncAction.UpToDate)) onDevice.Add(item.RelativePath);
        bool cancelled = false;
        foreach (SyncItem item in work)
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            progress?.Report(new DeviceSyncProgress(completed, work.Count, copiedBytes, total, item.RelativePath));
            string destination = Path.GetFullPath(Path.Combine(root, item.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            string partial = destination + PartialSuffix;
            try
            {
                if (!destination.StartsWith(plan.MusicRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new IOException("The destination is outside the music folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                long before = copiedBytes;
                await CopyFileAsync(item.SourcePath, partial, delta =>
                {
                    copiedBytes = before + delta;
                    progress?.Report(new DeviceSyncProgress(completed, work.Count, copiedBytes, total, item.RelativePath));
                }, cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(item.SourcePath));
                File.Move(partial, destination, overwrite: item.Action == SyncAction.Conflict);
                if (item.Action == SyncAction.Conflict) replaced++; else copied++;
                onDevice.Add(item.RelativePath);
                copiedBytes = before + item.Length;
            }
            catch (OperationCanceledException)
            {
                TryDelete(partial);
                cancelled = true;
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
            {
                TryDelete(partial);
                failures.Add($"{item.RelativePath}: {exception.Message}");
            }
            completed++;
        }
        progress?.Report(new DeviceSyncProgress(completed, work.Count, copiedBytes, total, null));
        string? playlist = cancelled ? null : WritePlaylist(plan, onDevice, failures);
        int conflicts = plan.Options.ReplaceDifferent ? 0 : plan.ConflictCount;
        return new DeviceSyncResult(copied, replaced, plan.UpToDateCount, conflicts, failures, playlist, cancelled);
    }

    private static string? WritePlaylist(DeviceSyncPlan plan, HashSet<string> onDevice, List<string> failures)
    {
        if (!plan.Options.WritePlaylist) return null;
        List<SyncItem> entries = [.. plan.Items.Where(item => onDevice.Contains(item.RelativePath))];
        if (entries.Count == 0) return null;
        StringBuilder text = new("#EXTM3U\n");
        string prefix = plan.Options.MusicFolder + "/";
        foreach (SyncItem item in entries)
        {
            double seconds = Math.Max(0, Math.Round(item.Track.Duration.TotalSeconds));
            text.Append("#EXTINF:").Append(seconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(item.Track.Artist).Append(" - ").Append(item.Track.Title).Append('\n')
                .Append(item.RelativePath.StartsWith(prefix, StringComparison.Ordinal) ? item.RelativePath[prefix.Length..] : item.RelativePath).Append('\n');
        }
        string path = Path.Combine(plan.MusicRoot, plan.Options.PlaylistName + ".m3u8");
        string temporary = path + PartialSuffix;
        try
        {
            Directory.CreateDirectory(plan.MusicRoot);
            File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            failures.Add($"{plan.Options.PlaylistName}.m3u8: {exception.Message}");
            return null;
        }
    }

    private static async Task CopyFileAsync(string source, string destination, Action<long> progress, CancellationToken cancellationToken)
    {
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        byte[] buffer = new byte[BufferSize];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            progress(written);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Unique(string relative, HashSet<string> used)
    {
        if (used.Add(relative)) return relative;
        string extension = Path.GetExtension(relative);
        string stem = relative[..^extension.Length];
        for (int number = 2; ; number++)
        {
            string candidate = $"{stem} ({number}){extension}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Leave the partial file; its name marks it as unfinished. */ }
    }

    private static long? ReadFreeSpace(string root)
    {
        try { return new DriveInfo(root).AvailableFreeSpace; }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
