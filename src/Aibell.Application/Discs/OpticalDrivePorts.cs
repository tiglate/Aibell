namespace Aibell.Application.Discs;

/// <summary>An optical drive. <see cref="Id"/> is the platform device name: a device path on Linux, a drive letter on Windows.</summary>
public sealed record OpticalDrive(string Id, string DisplayName);

/// <summary>Platform adapter for optical drives. Implementations live in the platform projects.</summary>
public interface IOpticalDriveService
{
    IReadOnlyList<OpticalDrive> ListDrives();

    /// <summary>Returns the audio table of contents, or null when the drive is empty or the disc has no audio tracks.</summary>
    Task<DiscToc?> ReadTocAsync(OpticalDrive drive, CancellationToken cancellationToken = default);
}
