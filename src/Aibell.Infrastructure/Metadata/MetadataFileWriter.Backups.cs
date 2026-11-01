using System.Text.Json;
using Aibell.Application.Library;

namespace Aibell.Infrastructure.Metadata;

public sealed partial class MetadataFileWriter
{
    public async Task<MetadataBackupScan> InspectBackupsAsync(Uri location, CancellationToken cancellationToken = default)
    {
        string source = SourcePath(location, requireWritable: false);
        using FileStream lease = LockSource(source);
        string directory = Path.GetDirectoryName(source)!;
        string currentHash = await HashAsync(source, cancellationToken).ConfigureAwait(false);
        List<MetadataBackupInfo> backups = [];
        List<MetadataBackupIssue> issues = [];
        foreach (string path in Directory.EnumerateFiles(directory, Prefix + "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string journalHash = await HashAsync(path, cancellationToken).ConfigureAwait(false);
                WriteJournal entry = await ReadJournalAsync(path, cancellationToken).ConfigureAwait(false);
                if (entry.SourceName != Path.GetFileName(source)) continue;
                string backup = Path.Combine(directory, entry.BackupName);
                if (await HashAsync(backup, cancellationToken).ConfigureAwait(false) != entry.OriginalHash)
                    throw new InvalidDataException("Backup content no longer matches the recorded original. Files were retained.");
                if (await HashAsync(path, cancellationToken).ConfigureAwait(false) != journalHash)
                    throw new IOException("The journal changed while it was being inspected.");
                backups.Add(new(ToBackup(source, backup, path, entry), new FileInfo(backup).Length,
                    currentHash == entry.WrittenHash, journalHash));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { issues.Add(new(new Uri(path), exception.Message)); }
        }
        return new(backups.OrderByDescending(backup => backup.Backup.CreatedAt).ToArray(), issues);
    }

    public async Task DeleteBackupAsync(MetadataBackupInfo backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        string source = SourcePath(backup.Backup.Source, requireWritable: false);
        using FileStream lease = LockSource(source);
        string directory = Path.GetDirectoryName(source)!;
        string journal = backup.Backup.Journal.LocalPath;
        if (Path.GetDirectoryName(Path.GetFullPath(journal)) != directory ||
            await HashAsync(journal, cancellationToken).ConfigureAwait(false) != backup.JournalHash)
            throw new IOException("The backup journal changed. Refresh the backup list before deleting.");
        WriteJournal entry = await ReadJournalAsync(journal, cancellationToken).ConfigureAwait(false);
        string path = Path.Combine(directory, entry.BackupName);
        if (entry.SourceName != Path.GetFileName(source) || path != backup.Backup.Backup.LocalPath ||
            entry.OriginalHash != backup.Backup.OriginalHash || entry.WrittenHash != backup.Backup.WrittenHash ||
            await HashAsync(path, cancellationToken).ConfigureAwait(false) != entry.OriginalHash)
            throw new IOException("The backup changed. Refresh the backup list before deleting.");
        cancellationToken.ThrowIfCancellationRequested();
        // No cancellation between deleting this verified backup and its journal.
        // A failed second delete leaves a journal surfaced as an issue on inspection.
        File.Delete(path);
        File.Delete(journal);
    }
}
