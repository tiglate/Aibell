using System.Net;
using System.Net.Http.Headers;
using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;

namespace Aibell.Infrastructure.Podcasts;

public sealed class HttpPodcastEpisodeDownloader(HttpClient client, TimeSpan? requestTimeout = null) : IPodcastEpisodeDownloader
{
    private readonly TimeSpan timeout = requestTimeout ?? TimeSpan.FromMinutes(30);

    public async Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!PodcastLocation.IsSupported(request.Source)) throw new ArgumentException("Use a complete HTTP or HTTPS audio URL without credentials or a fragment.", nameof(request));
        Uri destination = request.Destination;
        if (destination is null || !destination.IsAbsoluteUri || !destination.IsFile || destination.IsUnc ||
            destination.Host.Length > 0 && destination.Host != "localhost" || destination.Query.Length > 0 || destination.Fragment.Length > 0)
            throw new ArgumentException("Choose a local download destination.", nameof(request));
        if (request.MaximumBytes <= 0 || request.ExpectedLength < 0 || request.ExpectedLength > request.MaximumBytes)
            throw new ArgumentException("The download size limits are invalid.", nameof(request));
        string path = Path.GetFullPath(destination.LocalPath);
        if (File.Exists(path)) throw new IOException("The download destination already exists.");
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".aibell-podcast-{Guid.NewGuid():N}.partial");
        bool created = false;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            long written;
            await using (FileStream output = new(temporary, options))
            {
                created = true;
                written = await TransferAsync(request, output, deadline.Token).ConfigureAwait(false);
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            deadline.Token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
            return new(destination, written);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The podcast episode download timed out.", exception); }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<long> TransferAsync(PodcastDownloadRequest request, FileStream output, CancellationToken cancellationToken)
    {
        long written = 0;
        long? totalLength = null;
        EntityTagHeaderValue? validator = null;
        Uri? validatedResource = null;
        byte[] buffer = new byte[64 * 1024];
        long lastReport = 0;
        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage message = new(HttpMethod.Get, request.Source);
            message.Headers.UserAgent.ParseAdd("Aibell/0.1 (https://github.com/tiglate/Aibell)");
            message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            bool resuming = written > 0 && validator is not null;
            bool retryAllowed = true;
            TimeSpan retryDelay = TimeSpan.FromMilliseconds(100 * (attempt + 1));
            if (resuming)
            {
                message.Headers.Range = new RangeHeaderValue(written, null);
                message.Headers.IfRange = new RangeConditionHeaderValue(validator!);
            }
            try
            {
                using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && response.Headers.RetryAfter is { } retryAfter)
                {
                    TimeSpan serverDelay = retryAfter.Delta ?? (retryAfter.Date!.Value - DateTimeOffset.UtcNow);
                    // A longer deferral belongs in the future persisted download queue.
                    // Never retry earlier than requested just to satisfy this local retry budget.
                    retryAllowed = serverDelay <= TimeSpan.FromSeconds(5);
                    if (serverDelay > retryDelay) retryDelay = serverDelay;
                }
                response.EnsureSuccessStatusCode();
                Uri effectiveResource = response.RequestMessage?.RequestUri ?? request.Source;
                if (!PodcastLocation.IsSupported(effectiveResource)) throw new InvalidDataException("The episode redirected to an unsupported location.");
                if (!HttpPodcastFeedReader.IsAudioType(response.Content.Headers.ContentType?.MediaType) ||
                    response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The server returned an unsupported or encoded audio response.");

                long? bodyLength = response.Content.Headers.ContentLength;
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    // If-Range permits a server to return a new complete representation.
                    // Discard the old prefix before accepting that response.
                    output.SetLength(0); output.Position = 0; written = 0;
                    totalLength = bodyLength;
                    validator = response.Headers.ETag is { IsWeak: false } tag ? tag : null;
                    validatedResource = effectiveResource;
                }
                else if (response.StatusCode == HttpStatusCode.PartialContent && resuming)
                {
                    ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
                    if (range is not { From: { } from, To: { } to, Length: { } total } ||
                        !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || from != written || to < from || to >= total ||
                        totalLength is { } previous && total != previous ||
                        !string.Equals(response.Headers.ETag?.ToString(), validator!.ToString(), StringComparison.Ordinal) ||
                        !string.Equals(effectiveResource.AbsoluteUri, validatedResource?.AbsoluteUri, StringComparison.Ordinal))
                        throw new InvalidDataException("The resumed episode response does not match the saved byte range and validator.");
                    long rangeLength = to - from + 1;
                    if (bodyLength is { } advertised && advertised != rangeLength)
                        throw new InvalidDataException("The resumed response length does not match its byte range.");
                    bodyLength = rangeLength;
                    totalLength = total;
                }
                else throw new InvalidDataException("A complete audio response or validated resume range is required.");
                if (totalLength > request.MaximumBytes) throw new InvalidDataException("The episode exceeds the download size limit.");

                await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                long received = 0;
                while (true)
                {
                    int count;
                    try { count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
                    catch (IOException exception) { throw new HttpRequestException("The episode transfer was interrupted.", exception); }
                    if (count == 0) break;
                    if (count > request.MaximumBytes - written) throw new InvalidDataException("The episode exceeds the download size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    written += count; received += count;
                    // Several writes per second are plenty for a progress bar; the final value is always reported.
                    if (request.Progress is { } progress && Environment.TickCount64 - lastReport >= 100)
                    { lastReport = Environment.TickCount64; progress.Report(new(written, totalLength ?? request.ExpectedLength)); }
                }
                request.Progress?.Report(new(written, totalLength ?? request.ExpectedLength));
                if (bodyLength is { } length && received != length || totalLength is { } complete && written != complete)
                    throw new HttpRequestException("The episode transfer ended before its expected length.");
                if (written == 0) throw new InvalidDataException("The episode download is empty.");
                if (request.ExpectedLength is { } expected && written != expected)
                    throw new InvalidDataException("The episode download length does not match its expected size.");
                return written;
            }
            catch (HttpRequestException exception) when (attempt < 2 && retryAllowed && IsTransient(exception.StatusCode))
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode? status) => status is null or HttpStatusCode.RequestTimeout
        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
