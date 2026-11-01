namespace Aibell.Audio.Cdda;

/// <summary>Opens the audio reader for a drive id: a device path such as <c>/dev/sr0</c> on Linux, a drive letter such as <c>E:</c> on Windows.</summary>
public static class CdAudioReader
{
    /// <exception cref="IOException">The drive cannot be opened.</exception>
    /// <exception cref="PlatformNotSupportedException">The system is neither Linux nor Windows.</exception>
    public static ICdAudioReader Open(string driveId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveId);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (driveId.Length != 2 || !char.IsAsciiLetter(driveId[0]) || driveId[1] != ':') throw new IOException("Not a drive letter: " + driveId);
                return new WindowsCdAudioReader(driveId);
            }
            if (OperatingSystem.IsLinux())
            {
                if (!driveId.StartsWith("/dev/", StringComparison.Ordinal)) throw new IOException("Not a device path: " + driveId);
                return new LinuxCdAudioReader(driveId);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("The drive cannot be read (is the user in the cdrom group?).", exception);
        }
        throw new PlatformNotSupportedException("Audio CDs can be read on Linux and Windows only.");
    }
}
