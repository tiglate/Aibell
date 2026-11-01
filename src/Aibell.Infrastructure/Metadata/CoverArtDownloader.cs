using System.Net;
using Aibell.Application.Library;
using Aibell.Domain.Media;

namespace Aibell.Infrastructure.Metadata;

/// <summary>
/// Downloads one cover image: https only, at most <see cref="ArtworkChange.MaximumImageBytes"/>, within <see cref="Timeout"/> per
/// attempt, and only if the bytes really are a PNG or JPEG. The Cover Art Archive still lists some older images with
/// <c>http://</c> addresses; those are upgraded to https rather than refused, and an https-to-http redirect is never followed.
/// The archive's storage servers answer 5xx now and then, so a busy answer is retried twice before giving up.
/// </summary>
public sealed class CoverArtDownloader(HttpClient http, TimeSpan? retryDelay = null) : ICoverArtDownloader
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int Attempts = 3;

    public async Task<byte[]> DownloadAsync(Uri source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source is not { IsAbsoluteUri: true, Scheme: "http" or "https" }) throw new ArgumentException("Cover art must come from a web (https) address.", nameof(source));
        if (source.Scheme == Uri.UriSchemeHttp) source = new UriBuilder(source) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await DownloadOnceAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (TransientDownloadException exception) when (attempt < Attempts)
            {
                _ = exception;
                await Task.Delay((retryDelay ?? TimeSpan.FromSeconds(1)) * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (TransientDownloadException exception)
            {
                throw new IOException(exception.Message + " The Cover Art Archive may be busy; try again in a moment.", exception);
            }
        }
    }

    private async Task<byte[]> DownloadOnceAsync(Uri source, CancellationToken cancellationToken)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, source);
            request.Headers.UserAgent.ParseAdd("Aibell/0.1 ( https://github.com/tiglate/Aibell )");
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string message = $"The cover art server answered {(int)response.StatusCode}.";
                throw (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests
                    ? new TransientDownloadException(message) : new IOException(message);
            }
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" }) throw new IOException("The cover art server redirected to an insecure address.");
            if (response.Content.Headers.ContentLength > ArtworkChange.MaximumImageBytes) throw new InvalidDataException("The cover image is too large.");

            using MemoryStream bytes = new();
            await using Stream stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > ArtworkChange.MaximumImageBytes) throw new InvalidDataException("The cover image is too large.");
                bytes.Write(buffer, 0, read);
            }
            byte[] image = bytes.ToArray();
            if (!ArtworkChange.IsSupportedImage(image)) throw new InvalidDataException("The download is not a PNG or JPEG image.");
            return image;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientDownloadException("The cover art download timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new TransientDownloadException("The cover art could not be downloaded: " + exception.Message);
        }
    }

    private sealed class TransientDownloadException(string message) : Exception(message);
}
