using Aibell.Domain.Media;

namespace Aibell.Application.Library;

public interface IMediaMetadataReader
{
    Task<MediaMetadata> ReadAsync(
        Uri location,
        CancellationToken cancellationToken = default);
}
