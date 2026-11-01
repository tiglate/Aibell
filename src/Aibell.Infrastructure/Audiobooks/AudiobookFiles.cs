using Aibell.Application.Audiobooks;
using Aibell.Domain.Audiobooks;

namespace Aibell.Infrastructure.Audiobooks;

/// <summary>
/// Read-only file-system adapter. Canonicalization accepts only local audio file URIs, then resolves the file and every
/// ancestor symbolic link component by component, preserving path casing. The answer is point-in-time, not race-proof,
/// and case-only aliases on case-insensitive volumes (or hard links) are not merged. It never writes.
/// </summary>
public sealed class AudiobookFiles : IAudiobookFiles
{
    private const int MaximumLinkHops = 40;

    public Uri Canonicalize(Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!AudiobookLocation.IsSupported(location))
            throw new ArgumentException("Choose a local audio file (MP3, M4A/M4B, AAC, Ogg, Opus, FLAC or WAV).", nameof(location));
        return new Uri(Resolve(Path.GetFullPath(location.LocalPath), 0), UriKind.Absolute);
    }

    public bool Exists(Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.IsAbsoluteUri && location.IsFile && File.Exists(location.LocalPath);
    }

    private static string Resolve(string path, int hops)
    {
        try
        {
            string root = Path.GetPathRoot(path) ?? "";
            string[] parts = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            string current = root;
            for (int index = 0; index < parts.Length; index++)
            {
                current = Path.Combine(current, parts[index]);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is null) continue;
                if (hops >= MaximumLinkHops) throw new IOException("Too many symbolic links in the audio file path: " + path);
                string target = (info.ResolveLinkTarget(returnFinalTarget: true) ?? info).FullName;
                // Restart from the resolved target followed by the not-yet-visited remainder.
                return Resolve(index + 1 < parts.Length ? Path.Combine(target, Path.Combine(parts[(index + 1)..])) : target, hops + 1);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("The audio file location could not be resolved because access was denied: " + path, exception);
        }
        return path;
    }
}
