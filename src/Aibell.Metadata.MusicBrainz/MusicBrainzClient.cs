using System.Net.Http.Json;
using System.Text.Json;

namespace Aibell.Metadata.MusicBrainz;

/// <summary>
/// The one place that talks HTTP to MusicBrainz and the Cover Art Archive: generic User-Agent with contact details, the
/// one-request-per-interval limiter, headers-only completion and a response size cap. Expected failures become null.
/// </summary>
internal sealed class MusicBrainzClient(
    HttpClient httpClient,
    IMusicBrainzRateLimiter rateLimiter,
    Uri musicBrainzBaseUri,
    Uri coverArtArchiveBaseUri)
{
    // MusicBrainz throttles anonymous or generic clients harder; see
    // https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting.
    internal const string UserAgent = "Aibell/0.1 ( https://github.com/tiglate/Aibell )";
    internal const long MaxResponseBytes = 2 * 1024 * 1024;

    public async Task<T?> GetAsync<T>(string relativeUri, CancellationToken cancellationToken) where T : class
    {
        await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await SendAsync<T>(new Uri(musicBrainzBaseUri, relativeUri), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Front cover location for a release, or null when the archive has none. Cover Art Archive is a separate, unthrottled service.</summary>
    public async Task<Uri?> GetFrontCoverAsync(string releaseId, CancellationToken requestToken, CancellationToken callerToken)
    {
        try
        {
            CoverArtArchiveResponse? coverArt = await SendAsync<CoverArtArchiveResponse>(
                new Uri(coverArtArchiveBaseUri, $"release/{Uri.EscapeDataString(releaseId)}/"), requestToken).ConfigureAwait(false);
            CoverArtImage? image = coverArt?.Images?.OfType<CoverArtImage>().FirstOrDefault(candidate => candidate.Front)
                ?? coverArt?.Images?.OfType<CoverArtImage>().FirstOrDefault();
            string? imageUri = image?.Thumbnails?.Large ?? image?.Image;
            return Uri.TryCreate(imageUri, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? uri : null;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private async Task<T?> SendAsync<T>(Uri requestUri, CancellationToken cancellationToken) where T : class
    {
        using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // A 404 is an expected "nothing there" answer (unknown disc, no cover), not a failure.
        if (!response.IsSuccessStatusCode) return null;
        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
