namespace Aibell.Playback.LibVLC;

internal static class LibVlcRuntime
{
    public static string? ResolveDirectory(bool windows, string? configuredPath, string programFiles)
    {
        // LibVLCSharp's custom-directory parameter is unsupported on Linux.
        if (!windows) return null;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!Path.IsPathFullyQualified(configuredPath) || !HasLibraries(configuredPath))
                throw new DllNotFoundException("AIBELL_LIBVLC_PATH must name an absolute folder containing libvlc.dll and libvlccore.dll.");
            return Path.GetFullPath(configuredPath);
        }
        string installed = Path.Combine(programFiles, "VideoLAN", "VLC");
        return !string.IsNullOrWhiteSpace(programFiles) && HasLibraries(installed) ? installed : null;
    }

    private static bool HasLibraries(string directory) => File.Exists(Path.Combine(directory, "libvlc.dll")) &&
        File.Exists(Path.Combine(directory, "libvlccore.dll"));
}
