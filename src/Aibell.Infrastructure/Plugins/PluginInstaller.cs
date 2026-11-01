using System.IO.Compression;

namespace Aibell.Infrastructure.Plugins;

/// <summary>
/// Copies a plugin into the per-user plugins folder. The source is either a folder <c>Name/</c> that holds <c>Name.dll</c>, or a
/// <c>.zip</c> whose only top-level entry is such a folder. Nothing is executed here: the copy is staged beside the destination,
/// checked, and moved into place in one step, so a failed install leaves no partial plugin behind. Archive entries that would
/// escape the plugin folder, links, and oversized packages are rejected.
/// </summary>
public static class PluginInstaller
{
    public const long MaximumBytes = 200L * 1024 * 1024;
    public const int MaximumFiles = 2000;

    public static string Install(string source, string userRoot, IEnumerable<string> existingRoots,
        IReadOnlySet<string>? pendingRemoval = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(userRoot);
        string staging = Path.Combine(userRoot, ".installing-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            string name = Directory.Exists(source) ? StageFolder(source, staging, cancellationToken)
                : source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(source) ? StageZip(source, staging, cancellationToken)
                : throw new ArgumentException("Choose a plugin folder or a .zip file.");
            if (!File.Exists(Path.Combine(staging, name, name + ".dll")))
                throw new InvalidDataException($"A plugin must contain {name}.dll next to its folder name.");
            if (pendingRemoval?.Contains(name) == true)
                throw new InvalidOperationException($"{name} is waiting to be removed; restart the application first.");
            foreach (string root in existingRoots.Append(userRoot))
                if (Directory.Exists(Path.Combine(root, name)))
                    throw new InvalidOperationException($"A plugin named {name} is already installed.");
            Directory.Move(Path.Combine(staging, name), Path.Combine(userRoot, name));
            return name;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* A dot-folder is ignored by the loader. */ }
        }
    }

    private static string StageFolder(string source, string staging, CancellationToken cancellationToken)
    {
        DirectoryInfo root = new(Path.GetFullPath(source));
        string name = root.Name;
        RequireName(name);
        string target = Directory.CreateDirectory(Path.Combine(staging, name)).FullName;
        long total = 0;
        int files = 0;
        Copy(root, target);
        return name;

        void Copy(DirectoryInfo from, string to)
        {
            foreach (FileSystemInfo entry in from.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
                    throw new InvalidDataException("Plugins may not contain links: " + entry.Name);
                if (entry is DirectoryInfo directory)
                {
                    Copy(directory, Directory.CreateDirectory(Path.Combine(to, directory.Name)).FullName);
                    continue;
                }
                FileInfo file = (FileInfo)entry;
                if (++files > MaximumFiles || (total += file.Length) > MaximumBytes) throw new InvalidDataException("The plugin is too large.");
                file.CopyTo(Path.Combine(to, file.Name));
            }
        }
    }

    private static string StageZip(string source, string staging, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumFiles) throw new InvalidDataException("The archive is empty or too large.");
        string? name = null;
        long total = 0;
        string stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] parts = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || entry.FullName.StartsWith('/') || entry.FullName.Contains('\\') ||
                parts.Any(part => part is "." or ".." || part.Contains(':')))
                throw new InvalidDataException("The archive contains an unsafe path: " + entry.FullName);
            name ??= parts[0];
            if (!string.Equals(parts[0], name, StringComparison.Ordinal))
                throw new InvalidDataException("The archive must contain exactly one plugin folder.");
            RequireName(name);
            string destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
            if (!destination.StartsWith(stagingRoot, StringComparison.Ordinal))
                throw new InvalidDataException("The archive contains an unsafe path: " + entry.FullName);
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            if ((total += entry.Length) > MaximumBytes) throw new InvalidDataException("The plugin is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream input = entry.Open();
            using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write);
            // The declared size can lie, so the copy itself is bounded too.
            byte[] buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > entry.Length || written > MaximumBytes) throw new InvalidDataException("The archive entry is larger than declared: " + entry.FullName);
                output.Write(buffer, 0, read);
            }
        }
        return name ?? throw new InvalidDataException("The archive is empty.");
    }

    private static void RequireName(string name)
    {
        if (!PluginNames.IsValid(name)) throw new InvalidDataException($"“{name}” is not a valid plugin folder name (letters, digits, '.', '-' and '_').");
    }
}
