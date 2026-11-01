using Aibell.Application.Discs;

namespace Aibell.Application.Enrichment;

public enum MetadataLookupStatus
{
    /// <summary>The user has not allowed online lookups.</summary>
    Disabled,
    /// <summary>Lookups are allowed but no plugin provides them.</summary>
    NoProvider,
    Found,
    NotFound,
    Failed
}

public sealed record MetadataLookupResult<T>(MetadataLookupStatus Status, IReadOnlyList<T> Items, string? Detail = null);

public interface IMetadataLookupService
{
    /// <summary>True when the user allows online lookups and at least one plugin can answer.</summary>
    bool IsAvailable { get; }

    Task<MetadataLookupResult<ReleaseCandidate>> FindDiscAsync(DiscToc toc, CancellationToken cancellationToken = default);

    Task<MetadataLookupResult<ReleaseCandidate>> FindAlbumAsync(string album, string? artist, bool includeTracks = true, CancellationToken cancellationToken = default);

    Task<MetadataLookupResult<ArtistInfo>> FindArtistAsync(string artistName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks the loaded plugins for album and artist details. Nothing leaves the machine unless <c>isAllowed</c> says the user turned
/// lookups on; the answer is read on every call so the preference takes effect immediately. A plugin that is slow or throws is
/// reported as <see cref="MetadataLookupStatus.Failed"/>; only the caller's own cancellation propagates.
/// </summary>
public sealed class MetadataLookupService(
    IEnumerable<IReleaseLookupProvider> releaseProviders,
    IEnumerable<IArtistInfoProvider> artistProviders,
    Func<bool> isAllowed,
    TimeSpan? timeout = null) : IMetadataLookupService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private readonly IReleaseLookupProvider[] releases = [.. releaseProviders.Where(provider => provider.Descriptor.IsEnabled)];
    private readonly IArtistInfoProvider[] artists = [.. artistProviders.Where(provider => provider.Descriptor.IsEnabled)];

    public bool IsAvailable => isAllowed() && (releases.Length > 0 || artists.Length > 0);

    public Task<MetadataLookupResult<ReleaseCandidate>> FindDiscAsync(DiscToc toc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toc);
        return RunAsync(releases, (provider, token) => provider.LookupDiscAsync(toc, token), cancellationToken);
    }

    public Task<MetadataLookupResult<ReleaseCandidate>> FindAlbumAsync(string album, string? artist, bool includeTracks = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(album);
        return RunAsync(releases, (provider, token) => provider.LookupAlbumAsync(album, artist, includeTracks, token), cancellationToken);
    }

    public async Task<MetadataLookupResult<ArtistInfo>> FindArtistAsync(string artistName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artistName);
        return await RunAsync<IArtistInfoProvider, ArtistInfo>(artists, async (provider, token) =>
            await provider.LookupArtistAsync(artistName, token).ConfigureAwait(false) is { } info ? [info] : [],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<MetadataLookupResult<T>> RunAsync<TProvider, T>(
        TProvider[] providers,
        Func<TProvider, CancellationToken, Task<IReadOnlyList<T>>> lookup,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isAllowed()) return new MetadataLookupResult<T>(MetadataLookupStatus.Disabled, []);
        if (providers.Length == 0) return new MetadataLookupResult<T>(MetadataLookupStatus.NoProvider, []);
        string? failure = null;
        foreach (TProvider provider in providers)
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                IReadOnlyList<T> found = await lookup(provider, limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
                if (found.Count > 0) return new MetadataLookupResult<T>(MetadataLookupStatus.Found, found);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                failure = "The lookup timed out.";
            }
            catch (Exception exception)
            {
                failure = exception.Message;
            }
        }
        return failure is null
            ? new MetadataLookupResult<T>(MetadataLookupStatus.NotFound, [])
            : new MetadataLookupResult<T>(MetadataLookupStatus.Failed, [], failure);
    }
}
