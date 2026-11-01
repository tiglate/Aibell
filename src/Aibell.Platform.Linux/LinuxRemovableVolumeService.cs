using System.Text;
using Aibell.Application.Devices;

namespace Aibell.Platform.Linux;

/// <summary>
/// Removable volumes on Linux, read from <c>/proc/self/mountinfo</c> without any desktop service: the mounts the desktop
/// (udisks) creates under <c>/media/&lt;user&gt;/&lt;label&gt;</c> or <c>/run/media/&lt;user&gt;/&lt;label&gt;</c> with a file system that
/// music players use. System and network mounts never appear. Mounting and unmounting stay the desktop's job.
/// </summary>
public sealed class LinuxRemovableVolumeService : IRemovableVolumeService
{
    private const string MountInfoPath = "/proc/self/mountinfo";
    private static readonly string[] MountParents = ["/media/", "/run/media/"];
    private static readonly HashSet<string> FileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "vfat", "msdos", "exfat", "ntfs", "ntfs3", "fuseblk", "ext2", "ext3", "ext4", "f2fs", "udf", "hfsplus"
    };

    private readonly Func<string?> readMountInfo;
    private readonly Func<string, (long Total, long Free)?> readSpace;

    public LinuxRemovableVolumeService() : this(ReadMountInfo, ReadSpace) { }

    internal LinuxRemovableVolumeService(Func<string?> readMountInfo, Func<string, (long Total, long Free)?> readSpace)
    {
        this.readMountInfo = readMountInfo;
        this.readSpace = readSpace;
    }

    public IReadOnlyList<RemovableVolume> List()
    {
        List<RemovableVolume> volumes = [];
        foreach ((string mountPoint, string fileSystem) in Parse(readMountInfo()))
        {
            if (readSpace(mountPoint) is not { } space) continue;
            volumes.Add(new RemovableVolume(mountPoint, Path.GetFileName(mountPoint.TrimEnd('/')), mountPoint, space.Total, space.Free, fileSystem));
        }
        return volumes;
    }

    internal static IEnumerable<(string MountPoint, string FileSystem)> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] halves = line.Split(" - ", 2, StringSplitOptions.None);
            if (halves.Length != 2) continue;
            string[] before = halves[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] after = halves[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (before.Length < 5 || after.Length < 1) continue;
            string mountPoint = Unescape(before[4]);
            if (!MountParents.Any(parent => mountPoint.StartsWith(parent, StringComparison.Ordinal)) || mountPoint.Contains("..", StringComparison.Ordinal)) continue;
            // Under /media/<user>/ and /run/media/<user>/ the mount itself is one level deeper than the user's folder.
            string remainder = mountPoint[(mountPoint.StartsWith("/run/media/", StringComparison.Ordinal) ? "/run/media/".Length : "/media/".Length)..].Trim('/');
            if (!remainder.Contains('/') || !FileSystems.Contains(after[0])) continue;
            if (seen.Add(mountPoint)) yield return (mountPoint, after[0]);
        }
    }

    /// <summary>mountinfo writes spaces, tabs, newlines and backslashes as three-digit octal escapes (<c>\040</c>).</summary>
    internal static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal)) return value;
        List<byte> bytes = [];
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && IsOctal(value, index + 1))
            {
                bytes.Add(Convert.ToByte(value.Substring(index + 1, 3), 8));
                index += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[index].ToString()));
            }
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static bool IsOctal(string value, int start) =>
        start + 3 <= value.Length && value.AsSpan(start, 3).IndexOfAnyExceptInRange('0', '7') < 0;

    private static string? ReadMountInfo()
    {
        try { return File.ReadAllText(MountInfoPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static (long Total, long Free)? ReadSpace(string mountPoint)
    {
        try
        {
            DriveInfo drive = new(mountPoint);
            return drive.IsReady ? (drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
