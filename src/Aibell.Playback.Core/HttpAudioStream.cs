using System.Net;
using System.Net.Http.Headers;

namespace Aibell.Playback.Core;

/// <summary>
/// A readable, seekable view of an http(s) audio resource for the decoders. Servers that honour byte ranges are fully seekable
/// (a seek simply reopens the request at the new offset). Live streams, which cannot be rewound, keep their first
/// <see cref="HeadLimit"/> bytes so a decoder can probe several formats before settling on one; seeks beyond that window fail.
/// Reads block, so this stream must only be used from a decoding thread, never from the audio callback.
/// </summary>
internal sealed class HttpAudioStream : Stream
{
    internal const int HeadLimit = 1 << 20;
    private const long SkipForwardLimit = 64 * 1024;

    private readonly HttpClient client;
    private readonly Uri uri;
    private readonly bool rangeCapable;
    private readonly long length;
    private readonly byte[] head = new byte[HeadLimit];
    private int headLength;
    private HttpResponseMessage? response;
    private Stream? network;
    private long networkPosition;
    private long position;
    private bool usedFirstResponse;

    private HttpAudioStream(HttpClient client, Uri uri, HttpResponseMessage first, bool rangeCapable, long length)
    {
        this.client = client;
        this.uri = uri;
        this.rangeCapable = rangeCapable;
        this.length = length;
        response = first;
    }

    public static async Task<HttpAudioStream> OpenAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        HttpResponseMessage? response = await SendAsync(client, uri, 0, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response!.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Some servers refuse a range request outright; ask again for the whole resource.
                response.Dispose();
                response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            long length = -1;
            bool ranges = false;
            if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is { Length: { } total })
            {
                ranges = true;
                length = total;
            }
            else if (response.Content.Headers.ContentLength is { } contentLength &&
                response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase))
            {
                ranges = true;
                length = contentLength;
            }
            HttpAudioStream stream = new(client, uri, response, ranges, length);
            response = null;
            return stream;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Uri uri, long from, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(from, null);
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>True when the server honours byte ranges and told the file's length, so any position can be reached without reading what is before it.</summary>
    public bool CanRangeSeek => rangeCapable && length >= 0;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length >= 0 ? length : throw new NotSupportedException("The stream has no known length.");

    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            _ => length >= 0 ? length + offset : throw new NotSupportedException("The stream has no known length.")
        };
        if (target < 0) throw new IOException("Cannot seek before the start of the stream.");
        // A live stream can only go back into the bytes it kept, or forward into bytes that are still arriving.
        if (!rangeCapable && target > networkPosition && target - networkPosition > SkipForwardLimit)
            throw new IOException("A live stream cannot be seeked.");
        if (!rangeCapable && target < networkPosition && target >= headLength) throw new IOException("A live stream cannot be rewound that far.");
        position = target;
        return position;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        if (rangeCapable)
        {
            if (length >= 0 && position >= length) return 0;
            EnsureNetworkAt(position);
        }
        else if (position < headLength)
        {
            int copied = Math.Min(buffer.Length, headLength - (int)position);
            head.AsSpan((int)position, copied).CopyTo(buffer);
            position += copied;
            return copied;
        }
        else
        {
            // Live stream: skip any gap between the logical position and what has arrived.
            Stream live = network ??= OpenLive();
            byte[] scratch = new byte[4096];
            while (networkPosition < position)
            {
                int skipped = ReadNetwork(live, scratch.AsSpan(0, (int)Math.Min(scratch.Length, position - networkPosition)));
                if (skipped == 0) return 0;
            }
        }
        int read = ReadNetwork(network!, buffer);
        position += read;
        return read;
    }

    // The response owns the content stream, so it stays alive for as long as the stream is being read.
    private Stream OpenLive() => response!.Content.ReadAsStream();

    private int ReadNetwork(Stream source, Span<byte> buffer)
    {
        int read = source.Read(buffer);
        if (!rangeCapable && networkPosition < HeadLimit && read > 0)
        {
            int keep = Math.Min(read, HeadLimit - (int)networkPosition);
            buffer[..keep].CopyTo(head.AsSpan((int)networkPosition));
            headLength = (int)networkPosition + keep;
        }
        networkPosition += read;
        return read;
    }

    private void EnsureNetworkAt(long target)
    {
        if (network is not null && networkPosition == target) return;
        if (network is not null && target > networkPosition && target - networkPosition <= SkipForwardLimit)
        {
            byte[] scratch = new byte[4096];
            while (networkPosition < target)
            {
                int skipped = network.Read(scratch.AsSpan(0, (int)Math.Min(scratch.Length, target - networkPosition)));
                if (skipped == 0) return;
                networkPosition += skipped;
            }
            return;
        }
        network?.Dispose();
        network = null;
        if (response is null || target != 0 || networkPosition != 0 || usedFirstResponse)
        {
            response?.Dispose();
            response = SendAsync(client, uri, target, CancellationToken.None).GetAwaiter().GetResult();
            if (response.StatusCode != HttpStatusCode.PartialContent && target > 0)
            {
                response.Dispose();
                response = null;
                throw new IOException("The server stopped honouring byte ranges.");
            }
        }
        usedFirstResponse = true;
        network = response.Content.ReadAsStream();
        networkPosition = target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            network?.Dispose();
            response?.Dispose();
            network = null;
            response = null;
        }
        base.Dispose(disposing);
    }
}
