namespace Aibell.Domain.Podcasts;

public sealed record PodcastFeed(Uri Source, string Title, string Description, IReadOnlyList<PodcastEpisode> Episodes);

public sealed record PodcastEpisode(string Id, string Title, Uri Enclosure, string? MediaType = null,
    long? Length = null, DateTimeOffset? Published = null, TimeSpan? Duration = null, string Description = "",
    Uri? Image = null);

public static class PodcastLocation
{
    public static bool IsSupported(Uri? location) => location is { IsAbsoluteUri: true }
        && location.Scheme is "http" or "https" && location.OriginalString.Length <= 4096
        && location.IsWellFormedOriginalString() && !location.OriginalString.Any(char.IsWhiteSpace)
        && location.UserInfo.Length == 0 && location.Fragment.Length == 0 && !string.IsNullOrWhiteSpace(location.Host);
}
