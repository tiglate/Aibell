using Aibell.Domain.Media;

namespace Aibell.Application.Library;

public sealed record TrackFileState(long Size, DateTimeOffset ModifiedAt);

public interface ITrackMetadataRepository
{
    Task<Track> SaveMetadataAsync(Guid trackId, TrackMetadata metadata, CancellationToken cancellationToken = default);
    Task<Track> SaveMetadataAsync(Guid trackId, TrackMetadata metadata, TrackFileState? fileState, CancellationToken cancellationToken = default);
}

public sealed record EmbeddedArtworkInfo(int Index, string Hash, bool IsFront, string Kind, long Size);

public sealed record TrackFileDetails(TrackMetadata Metadata, int BitsPerSample, string Codec,
    IReadOnlyList<string> TagFormats, IReadOnlyDictionary<string, string> AdditionalFields, string? SourceHash = null, string? FileWriteRestriction = null, IReadOnlyList<EmbeddedArtworkInfo>? EmbeddedArtwork = null);

public interface ITrackFileDetailsReader
{
    Task<TrackFileDetails> ReadAsync(Uri location, CancellationToken cancellationToken = default);
}
