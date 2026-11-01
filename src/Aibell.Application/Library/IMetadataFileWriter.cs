using Aibell.Domain.Media;

namespace Aibell.Application.Library;

public sealed record MetadataFileBackup(Uri Source, Uri Backup, Uri Journal, string OriginalHash,
    string WrittenHash, DateTimeOffset CreatedAt);

public sealed record MetadataFileWriteResult(MetadataFileBackup Backup, string? Warning = null);

public interface IMetadataFileWriter
{
    Task<MetadataFileWriteResult> WriteAsync(Uri location, MetadataEdit edit, string? expectedSourceHash = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MetadataFileBackup>> GetBackupsAsync(Uri location, CancellationToken cancellationToken = default);
    Task<MetadataFileWriteResult> RestoreAsync(MetadataFileBackup backup, CancellationToken cancellationToken = default);
}
