using System.Globalization;
using System.Text;

namespace Aibell.Mpd.Config;

/// <summary>Where MPD looks for its configuration, and reading and writing the file safely.</summary>
public static class MpdConfigFile
{
    /// <summary>
    /// The places MPD reads <c>mpd.conf</c> from when no path is given, in the order it tries them.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string? configHome, string home)
    {
        List<string> paths = [];
        if (!string.IsNullOrEmpty(configHome)) paths.Add(Path.Combine(configHome, "mpd", "mpd.conf"));
        else if (!string.IsNullOrEmpty(home)) paths.Add(Path.Combine(home, ".config", "mpd", "mpd.conf"));
        if (!string.IsNullOrEmpty(home))
        {
            paths.Add(Path.Combine(home, ".mpdconf"));
            paths.Add(Path.Combine(home, ".mpd", "mpd.conf"));
        }
        paths.Add("/etc/mpd.conf");
        return paths;
    }

    /// <summary>The first candidate that exists, or <c>null</c>.</summary>
    public static string? FindExisting(string? configHome, string home) =>
        Candidates(configHome, home).FirstOrDefault(File.Exists);

    /// <summary>The path to offer for a new file: the per-user one.</summary>
    public static string DefaultNewPath(string? configHome, string home) => Candidates(configHome, home)[0];

    public static MpdConfigDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return MpdConfigDocument.Parse(File.ReadAllText(path, new UTF8Encoding(false)));
    }

    /// <summary>True when the file (or, for a file that does not exist yet, its folder) can be written by this user.</summary>
    public static bool CanWrite(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            if (File.Exists(path))
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                return CanCreateIn(Path.GetDirectoryName(Path.GetFullPath(path)));
            }
            return CanCreateIn(Path.GetDirectoryName(Path.GetFullPath(path)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanCreateIn(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return false;
        string probe = directory;
        while (!Directory.Exists(probe))
        {
            string? parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent)) return false;
            probe = parent;
        }
        string file = Path.Combine(probe, $".aibell-write-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the document. An existing file is first copied to <c>name.bak-yyyyMMdd-HHmmss</c> next to it, the new text goes to a temporary
    /// file in the same folder and replaces the original in one step, and the original's permissions are kept.
    /// </summary>
    /// <returns>The path of the backup, or <c>null</c> when there was no file to back up.</returns>
    /// <exception cref="InvalidOperationException">A block is missing its closing brace, which would corrupt the rest of the file.</exception>
    public static string? Save(MpdConfigDocument document, string path, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (document.Blocks.Any(block => !block.IsClosed))
            throw new InvalidOperationException("A block has no closing brace; fix the text before saving.");
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        string? backup = null;
        UnixFileMode? mode = null;
        if (File.Exists(full))
        {
            backup = $"{full}.bak-{(now ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
            for (int attempt = 2; File.Exists(backup); attempt++)
                backup = $"{full}.bak-{(now ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{attempt}";
            File.Copy(full, backup);
            if (!OperatingSystem.IsWindows()) mode = File.GetUnixFileMode(full);
        }
        string temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, document.Serialize(), new UTF8Encoding(false));
            if (mode is { } keep && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, keep);
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return backup;
    }
}
