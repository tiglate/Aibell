namespace Aibell.Application.Library;

public interface ILibrarySourceRepository
{
    Task AddAsync(string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    Task<int> RemoveAsync(string path, CancellationToken cancellationToken = default);
}
