using Aibell.Application.Podcasts;

namespace Aibell.Infrastructure.Podcasts;

/// <summary>Filesystem adapter confined to <c>root</c>. Symlinks, junctions and other reparse points at or below the root are rejected.</summary>
public sealed class PodcastDownloadStore : IPodcastDownloadStore, IPodcastDownloadDeletionStore, IPodcastDownloadIdentityStore
{
    private PodcastDownloadConfiguration configuration;
    public string Root => Volatile.Read(ref configuration).Root;

    public PodcastDownloadStore(string root, IEnumerable<string>? previousRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        configuration = NormalizeConfiguration(root, previousRoots);
    }

    public Uri PrepareDestination(Guid subscriptionId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['/', '\\', ':', '\0']) >= 0 ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName != Path.GetFileName(fileName) || fileName is "." or "..")
            throw new IOException("The episode file name is not valid.");
        string downloadRoot = Root;
        EnsureSafeAncestors(downloadRoot);
        EnsureDirectory(downloadRoot);
        string folder = Path.Combine(downloadRoot, subscriptionId.ToString("N"));
        EnsureDirectory(folder);
        string destination = Path.Combine(folder, fileName);
        if (File.Exists(destination) || Directory.Exists(destination) || IsLink(destination))
            throw new IOException("A file already exists where this episode would be saved. It was not replaced: " + destination);
        return new Uri(destination);
    }

    public Uri? ResolveExisting(string? path)
    {
        try { return ResolveExistingCore(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    private Uri? ResolveExistingCore(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        string full = Path.GetFullPath(path);
        string? allowedRoot = Volatile.Read(ref configuration).AllowedRoots.FirstOrDefault(candidate => IsInside(candidate, full));
        if (allowedRoot is null) return null;
        string root = allowedRoot;
        EnsureSafeAncestors(root);
        string relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative == ".") return null;
        if (IsLink(root) || !Directory.Exists(root)) return null;
        string current = root;
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        for (int i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (IsLink(current)) return null;
            bool last = i == parts.Length - 1;
            if (last ? !File.Exists(current) : !Directory.Exists(current)) return null;
        }
        return new Uri(full);
    }

    /// <summary>Validates and probes before settings persistence. Applying the returned immutable value performs no I/O.</summary>
    public static PodcastDownloadConfiguration PrepareConfiguration(string root, IEnumerable<string>? previousRoots = null)
    {
        PodcastDownloadConfiguration prepared = NormalizeConfiguration(root, previousRoots);
        EnsureSafeAncestors(prepared.Root);
        EnsureDirectory(prepared.Root);
        string probe = Path.Combine(prepared.Root, $".aibell-write-probe-{Guid.NewGuid():N}");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        return prepared;
    }

    public void ApplyConfiguration(PodcastDownloadConfiguration prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        Volatile.Write(ref configuration, prepared);
    }

    public void ConfigureRoot(string root, IEnumerable<string>? previousRoots = null) =>
        ApplyConfiguration(PrepareConfiguration(root, previousRoots));

    private static PodcastDownloadConfiguration NormalizeConfiguration(string root, IEnumerable<string>? previousRoots)
    {
        static string Normalize(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Choose an absolute podcast download folder.", nameof(root));
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        string normalized = Normalize(root);
        return new PodcastDownloadConfiguration(normalized,
            new[] { normalized }.Concat(previousRoots ?? []).Select(Normalize)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray());
    }

    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != "." && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public bool IsSameFile(string path, Uri file) => file.IsAbsoluteUri && file.IsFile &&
        Path.GetFullPath(path).Equals(Path.GetFullPath(file.LocalPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public bool OwnsDownload(Guid subscriptionId, string episodeId, string path)
    {
        try { return ResolveExisting(path) is not null && HasManagedShape(subscriptionId, episodeId, Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private bool HasManagedShape(Guid subscriptionId, string episodeId, string full)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(episodeId)))[..16].ToLowerInvariant();
        string[] extensions = [".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wav"];
        // Roots can overlap after a parent folder is selected; match ownership, not merely the first containing prefix.
        return Volatile.Read(ref configuration).AllowedRoots.Any(root =>
        {
            if (!IsInside(root, full)) return false;
            string[] parts = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return parts.Length == 2 && parts[0].Equals(subscriptionId.ToString("N"), comparison) &&
                Path.GetFileNameWithoutExtension(parts[1]).Equals(hash, comparison) &&
                extensions.Contains(Path.GetExtension(parts[1]), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        });
    }

    public IPodcastDownloadDeletion StageDelete(Guid subscriptionId, string episodeId, string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("The downloaded file path is unsafe.");
        string full = Path.GetFullPath(path);
        if (!HasManagedShape(subscriptionId, episodeId, full))
            throw new IOException("The file is not an application-owned episode download. The file was kept.");
        EnsureSafeAncestors(full);
        if (Directory.Exists(full)) throw new IOException("A directory replaced the downloaded file. It was kept.");
        if (!File.Exists(full)) return new StagedDeletion(full, null);
        string staged = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.deleting");
        File.Move(full, staged);
        return new StagedDeletion(full, staged);
    }

    private sealed class StagedDeletion(string original, string? staged) : IPodcastDownloadDeletion
    {
        private bool complete;
        public void Complete()
        {
            complete = true; // SQL has committed: never restore after this point.
            if (staged is null) return;
            try { File.Delete(staged); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new IOException($"The download record was cleared, but the staged file could not be removed. Remove it manually at {staged}.", exception); }
        }

        public void Dispose()
        {
            if (complete || staged is null) return;
            complete = true;
            try { File.Move(staged, original); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new IOException($"The download could not be restored. Its contents were preserved at {staged}.", exception); }
        }
    }

    private static void EnsureSafeAncestors(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if (IsLink(current)) throw new IOException("The podcast path contains a link and will not be used: " + current);
            current = Path.GetDirectoryName(current);
        }
    }

    private static void EnsureDirectory(string path)
    {
        if (IsLink(path)) throw new IOException("The podcast folder is a link and will not be used: " + path);
        if (File.Exists(path)) throw new IOException("A file blocks the podcast folder: " + path);
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        // Re-check after creation: a substituted link must not be written through.
        if (IsLink(path)) throw new IOException("The podcast folder is a link and will not be used: " + path);
    }

    private static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists ? info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint) : IsDanglingLink(info);
    }

    private static bool IsDanglingLink(FileSystemInfo info)
    {
        try { return info.LinkTarget is not null; } catch (IOException) { return false; }
    }
}

/// <summary>Only created by the store after validation; immutable, safe to apply after committing preferences.</summary>
public sealed class PodcastDownloadConfiguration
{
    internal PodcastDownloadConfiguration(string root, string[] allowedRoots)
    { Root = root; AllowedRoots = Array.AsReadOnly(allowedRoots); }
    public string Root { get; }
    public IReadOnlyList<string> AllowedRoots { get; }
}
