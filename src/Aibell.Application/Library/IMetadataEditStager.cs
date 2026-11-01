using Aibell.Domain.Media;

namespace Aibell.Application.Library;

public interface IMetadataEditStager
{
    Task<IStagedMetadataEdit> StageAsync(Uri location, MetadataEdit edit, CancellationToken cancellationToken = default);
}

public interface IStagedMetadataEdit : IAsyncDisposable
{
    Uri Source { get; }
    Uri StagedFile { get; }
    string SourceHash { get; }
    string StagedHash { get; }
    MetadataEdit Edit { get; }
    Task VerifyUnchangedAsync(CancellationToken cancellationToken = default);
}
