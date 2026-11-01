namespace Aibell.Application.Library;

public interface IMediaFileDiscovery
{
    IAsyncEnumerable<Uri> DiscoverAsync(
        IReadOnlyCollection<string> roots,
        CancellationToken cancellationToken = default);
}
