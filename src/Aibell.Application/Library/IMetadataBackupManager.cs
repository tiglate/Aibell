namespace Aibell.Application.Library;

public sealed record MetadataBackupInfo(MetadataFileBackup Backup, long Size, bool CanRestore, string JournalHash)
{
    public string DisplayName => $"{Backup.CreatedAt.ToLocalTime():g} — {Size / 1048576d:N2} MiB — {(CanRestore ? "Matches current file" : "Earlier file version")}";
}

public sealed record MetadataBackupIssue(Uri Journal, string Message);
public sealed record MetadataBackupScan(IReadOnlyList<MetadataBackupInfo> Backups, IReadOnlyList<MetadataBackupIssue> Issues);

public interface IMetadataBackupManager
{
    Task<MetadataBackupScan> InspectBackupsAsync(Uri location, CancellationToken cancellationToken = default);
    // Deletes only the verified backup and its journal, never the source file.
    Task DeleteBackupAsync(MetadataBackupInfo backup, CancellationToken cancellationToken = default);
}
