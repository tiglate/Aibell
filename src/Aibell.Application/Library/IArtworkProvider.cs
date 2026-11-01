namespace Aibell.Application.Library;

public interface IArtworkProvider
{
    Task<Uri?> GetAsync(Uri mediaLocation, CancellationToken cancellationToken = default);
}
