using Aibell.Domain.Podcasts;

namespace Aibell.Application.Podcasts;

public sealed record PodcastFeedRequest(Uri Source, string? EntityTag = null, DateTimeOffset? ModifiedSince = null);
public sealed record PodcastFeedIssue(int Entry, string Message);
public sealed record PodcastFeedResult(PodcastFeed? Feed, IReadOnlyList<PodcastFeedIssue> Issues,
    bool NotModified = false, string? EntityTag = null, DateTimeOffset? LastModified = null);

public interface IPodcastFeedReader
{
    Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default);
}
