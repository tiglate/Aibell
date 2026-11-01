using Aibell.Application.Devices;

namespace Aibell.Platform.Windows;

/// <summary>Removable volumes on Windows: the ready drives that Windows reports as <see cref="DriveType.Removable"/>.</summary>
public sealed class WindowsRemovableVolumeService : IRemovableVolumeService
{
    private readonly Func<IReadOnlyList<RemovableVolume>> read;

    public WindowsRemovableVolumeService() : this(ReadDrives) { }

    internal WindowsRemovableVolumeService(Func<IReadOnlyList<RemovableVolume>> read) => this.read = read;

    public IReadOnlyList<RemovableVolume> List() => read();

    internal static RemovableVolume Describe(string driveName, string label, string rootPath, long total, long free, string fileSystem) =>
        new(driveName, string.IsNullOrWhiteSpace(label) ? driveName.TrimEnd('\\') : $"{label} ({driveName.TrimEnd('\\')})", rootPath, total, free, fileSystem);

    private static IReadOnlyList<RemovableVolume> ReadDrives()
    {
        List<RemovableVolume> volumes = [];
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;
                volumes.Add(Describe(drive.Name, drive.VolumeLabel, drive.RootDirectory.FullName, drive.TotalSize, drive.AvailableFreeSpace, drive.DriveFormat));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Removed while listing. */ }
        }
        return volumes;
    }
}
