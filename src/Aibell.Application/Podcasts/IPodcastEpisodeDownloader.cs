namespace Aibell.Application.Podcasts;

public sealed record PodcastDownloadRequest(Uri Source, Uri Destination, long MaximumBytes = 1024L * 1024 * 1024,
    long? ExpectedLength = null, IProgress<PodcastDownloadProgress>? Progress = null);

/// <summary>Bytes of the current attempt written so far, and the total when the server announced one.</summary>
public sealed record PodcastDownloadProgress(long Received, long? Total);
public sealed record PodcastDownloadResult(Uri Destination, long BytesWritten);

public interface IPodcastEpisodeDownloader
{
    Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default);
}
