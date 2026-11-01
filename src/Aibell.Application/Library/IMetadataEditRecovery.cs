namespace Aibell.Application.Library;

public enum PendingMetadataEditState { Incomplete, Validated }

public sealed record PendingMetadataEdit(
    Uri Source, Uri StagedFile, Uri Journal, string SourceHash,
    string? ObservedStageHash, string JournalHash, PendingMetadataEditState State);

public sealed record MetadataEditRecoveryIssue(Uri Journal, string Message);

public sealed record MetadataEditRecoveryScan(
    IReadOnlyList<PendingMetadataEdit> Pending, IReadOnlyList<MetadataEditRecoveryIssue> Issues);

public interface IMetadataEditRecovery
{
    // Inspect only explicitly supplied directories; no automatic deletion or
    // source-file recovery is performed. Live leased previews are skipped.
    Task<MetadataEditRecoveryScan> ScanAsync(IReadOnlyCollection<string> directories, CancellationToken cancellationToken = default);
    // Explicit recursive inspection skips linked folders and reports inaccessible locations.
    Task<MetadataEditRecoveryScan> ScanRecursivelyAsync(IReadOnlyCollection<string> directories, CancellationToken cancellationToken = default);
    // Recheck the inspected snapshot before discarding its preview and journal.
    // The source file is never modified or deleted.
    Task DiscardAsync(PendingMetadataEdit edit, CancellationToken cancellationToken = default);
}
