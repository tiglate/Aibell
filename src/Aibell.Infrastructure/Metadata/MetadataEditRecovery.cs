using System.Text.Json;
using Aibell.Application.Library;

namespace Aibell.Infrastructure.Metadata;

public sealed class MetadataEditRecovery : IMetadataEditRecovery
{
    public Task<MetadataEditRecoveryScan> ScanAsync(IReadOnlyCollection<string> directories,
        CancellationToken cancellationToken = default) => ScanAsync(directories, false, cancellationToken);

    public Task<MetadataEditRecoveryScan> ScanRecursivelyAsync(IReadOnlyCollection<string> directories,
        CancellationToken cancellationToken = default) => ScanAsync(directories, true, cancellationToken);

    private static Task<MetadataEditRecoveryScan> ScanAsync(IReadOnlyCollection<string> directories,
        bool recursive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directories);
        string[] roots = directories.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToArray();
        return Task.Run(() => ScanCoreAsync(roots, recursive, cancellationToken), cancellationToken);
    }

    private static async Task<MetadataEditRecoveryScan> ScanCoreAsync(string[] roots, bool recursive,
        CancellationToken cancellationToken)
    {
        List<PendingMetadataEdit> pending = [];
        List<MetadataEditRecoveryIssue> issues = [];
        HashSet<string> visited = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Queue<string> directories = new(roots);
        while (directories.TryDequeue(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(directory)) continue;
            string[] journals;
            try
            {
                RejectLinkedDirectory(directory);
                if (!Directory.Exists(directory))
                {
                    issues.Add(new(new Uri(directory), "This folder is unavailable."));
                    continue;
                }
                journals = Directory.GetFiles(directory, MetadataEditJournal.Prefix + "*.json");
                if (recursive)
                    foreach (string child in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
                        directories.Enqueue(child);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                issues.Add(new(new Uri(directory), exception.Message));
                continue;
            }
            foreach (string path in journals.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string stagePath = MetadataEditJournal.StagePath(path);
                    // An exclusive lease is held for the entire lifetime of a
                    // live stage. Do not inspect or discard another owner's edit.
                    FileStream lease;
                    try { lease = MetadataEditJournal.AcquireLease(stagePath); }
                    catch (IOException) { continue; }
                    using (lease)
                    {
                        MetadataEditJournal journal = await MetadataEditJournal.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                        string source = Path.Combine(directory, journal.SourceName);
                        if (await MetadataEditJournal.HashAsync(source, cancellationToken).ConfigureAwait(false) != journal.SourceHash)
                            throw new InvalidDataException("The original file changed; the preview was preserved for review.");
                        string? stageHash = File.Exists(stagePath)
                            ? await MetadataEditJournal.HashAsync(stagePath, cancellationToken).ConfigureAwait(false) : null;
                        if (journal.Ready && stageHash != journal.StageHash)
                            throw new InvalidDataException("The validated preview changed or is missing; recovery files were preserved.");
                        pending.Add(new(new Uri(source), new Uri(stagePath), new Uri(path), journal.SourceHash,
                            stageHash, await MetadataEditJournal.HashAsync(path, cancellationToken).ConfigureAwait(false),
                            journal.Ready ? PendingMetadataEditState.Validated : PendingMetadataEditState.Incomplete));
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    issues.Add(new(new Uri(path), exception.Message));
                }
            }
        }
        return new(pending, issues);
    }

    private static void RejectLinkedDirectory(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null || directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked folders are excluded from metadata recovery scans.");
    }

    public async Task DiscardAsync(PendingMetadataEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!edit.Journal.IsAbsoluteUri || !edit.Journal.IsFile)
            throw new ArgumentException("Recovery requires a local journal.", nameof(edit));
        string path = edit.Journal.LocalPath;
        RejectLinkedDirectory(Path.GetDirectoryName(path)!);
        string stagePath = MetadataEditJournal.StagePath(path);
        using (FileStream lease = MetadataEditJournal.AcquireLease(stagePath))
        {
            MetadataEditJournal journal = await MetadataEditJournal.LoadAsync(path, cancellationToken).ConfigureAwait(false);
            string source = Path.Combine(Path.GetDirectoryName(path)!, journal.SourceName);
            if (new Uri(source) != edit.Source || new Uri(stagePath) != edit.StagedFile ||
                await MetadataEditJournal.HashAsync(path, cancellationToken).ConfigureAwait(false) != edit.JournalHash ||
                await MetadataEditJournal.HashAsync(source, cancellationToken).ConfigureAwait(false) != edit.SourceHash ||
                edit.SourceHash != journal.SourceHash)
                throw new IOException("Recovery inputs changed; scan again before discarding.");
            string? stageHash = File.Exists(stagePath)
                ? await MetadataEditJournal.HashAsync(stagePath, cancellationToken).ConfigureAwait(false) : null;
            if (stageHash != edit.ObservedStageHash || (journal.Ready && stageHash != journal.StageHash))
                throw new IOException("The preview changed; scan again before discarding.");
            cancellationToken.ThrowIfCancellationRequested();
            // Cancellation is honored before deletion, never between deleting
            // the preview and its journal. The original is never deleted.
            File.Delete(stagePath);
            File.Delete(path);
        }
        File.Delete(MetadataEditJournal.LeasePath(stagePath));
    }
}
