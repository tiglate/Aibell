using System.Net;

namespace Aibell.Playback.Core;

/// <summary>Timing knobs for <see cref="HlsAudioStream"/>; the defaults suit real streams, tests shrink them.</summary>
internal sealed record HlsOptions
{
    /// <summary>Upper bound for one playlist or segment request, headers and body together.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A live stream whose playlist offers nothing new for this long is treated as ended.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>The playlist is reloaded about once per target duration, never faster or slower than these bounds.</summary>
    public TimeSpan MinimumReload { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumReload { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The concatenated bytes of an HLS stream's segments as one readable stream, so the ordinary decoders can play it when the segments
/// are MP3 (or another format they know). Only the current and the next segment are held in memory, plus the first
/// <see cref="HeadLimit"/> bytes so a decoder can probe formats; VOD playlists play through once and cannot be seeked.
/// Reads block, so this stream must only be used from a decoding thread, never from the audio callback.
/// </summary>
internal sealed class HlsAudioStream : Stream
{
    internal const int HeadLimit = 1 << 20;
    internal const int MaximumPlaylistBytes = 1 << 20;
    internal const int MaximumSegmentBytes = 8 << 20;
    private const int LiveStartBehindEdge = 3;
    private const int ReloadFailuresTolerated = 3;
    private const long SkipForwardLimit = 64 * 1024;

    private readonly HttpClient client;
    private readonly HlsOptions options;
    private readonly Uri mediaPlaylistUri;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<HlsSegment> pending = new();
    private readonly byte[] head = new byte[HeadLimit];
    private Uri playlistBase;
    private bool endList;
    private double targetDuration;
    private long nextSequence;
    private DateTime lastReload = DateTime.UtcNow;
    private DateTime lastProgress = DateTime.UtcNow;
    private byte[] current = [];
    private int currentOffset;
    private Task<byte[]>? prefetch;
    private int headLength;
    private long forwardPosition;
    private long position;
    private bool finished;
    private bool disposed;
    private long segmentsStarted;

    private HlsAudioStream(HttpClient client, HlsOptions options, Uri mediaPlaylistUri, Uri playlistBase)
    {
        this.client = client;
        this.options = options;
        this.mediaPlaylistUri = mediaPlaylistUri;
        this.playlistBase = playlistBase;
    }

    /// <summary>True when the playlist has an end (a VOD or finished event) rather than being a live stream.</summary>
    public bool IsLive => !endList;

    /// <summary>What the first segment holds (MPEG-TS, packed AAC, MP3...), so the caller can pick the decoder without reading it.</summary>
    public HlsSegmentKind FirstSegmentKind { get; private set; }

    /// <summary>How many segments have started to be read; a reader compares it before and after a read to notice a segment join.</summary>
    public long SegmentsStarted => Interlocked.Read(ref segmentsStarted);

    public static async Task<HlsAudioStream> OpenAsync(HttpClient client, Uri uri, CancellationToken cancellationToken, HlsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(uri);
        HlsOptions settings = options ?? new HlsOptions();
        Uri requested = HlsPlaylistParser.ValidateAddress(uri, uri);
        (string text, Uri final) = await FetchPlaylistAsync(client, requested, requested, settings, cancellationToken).ConfigureAwait(false);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(text, final);
        if (playlist is HlsMasterPlaylist master)
        {
            Uri variant = HlsPlaylistParser.ChooseVariant(master).Uri;
            HlsPlaylistParser.ValidateAddress(variant, requested);
            requested = variant;
            (text, final) = await FetchPlaylistAsync(client, requested, requested, settings, cancellationToken).ConfigureAwait(false);
            playlist = HlsPlaylistParser.Parse(text, final);
        }
        if (playlist is not HlsMediaPlaylist media) throw new InvalidDataException("The HLS variant playlist is itself a master playlist.");

        HlsAudioStream stream = new(client, settings, requested, final);
        try
        {
            stream.Adopt(media, initial: true);
            using CancellationTokenSource opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stream.lifetime.Token);
            if (!stream.pending.TryDequeue(out HlsSegment? first)) throw new InvalidDataException("The HLS playlist lists no segments.");
            stream.nextSequence = first.Sequence + 1;
            byte[] bytes = await FetchBytesAsync(client, first.Uri, requested, MaximumSegmentBytes, settings, opening.Token).ConfigureAwait(false);
            stream.FirstSegmentKind = Inspect(bytes);
            stream.segmentsStarted = 1;
            stream.current = bytes;
            stream.StartPrefetch();
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Refuses segment formats the built-in player cannot read (fragmented MP4) and reports the kind of the others.</summary>
    private static HlsSegmentKind Inspect(byte[] segment)
    {
        HlsSegmentKind kind = SniffSegment(segment);
        if (kind == HlsSegmentKind.Mp4)
            throw new NotSupportedException(AudioSourceFactory.UnsupportedMessage("The built-in player cannot play this HLS stream: its fragmented MP4 segments are not readable here."));
        return kind;
    }

    internal static HlsSegmentKind SniffSegment(ReadOnlySpan<byte> data)
    {
        // Packed-audio segments start with an ID3v2 tag that carries a timestamp.
        if (data.Length >= 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
        {
            int size = (data[6] & 0x7f) << 21 | (data[7] & 0x7f) << 14 | (data[8] & 0x7f) << 7 | data[9] & 0x7f;
            int footer = (data[5] & 0x10) != 0 ? 10 : 0;
            long end = 10L + size + footer;
            data = end >= data.Length ? [] : data[(int)end..];
        }
        if (data.Length < 4) return HlsSegmentKind.Unknown;
        if (data[0] == 0x47 && (data.Length <= 188 || data[188] == 0x47)) return HlsSegmentKind.MpegTransportStream;
        if (data[4..].StartsWith("ftyp"u8) || data[4..].StartsWith("styp"u8) || data[4..].StartsWith("moof"u8)) return HlsSegmentKind.Mp4;
        if (data[0] == 0xff && (data[1] & 0xf6) == 0xf0) return HlsSegmentKind.Aac;
        if (data[0] == 0xff && (data[1] & 0xe0) == 0xe0 && (data[1] & 0x06) != 0) return HlsSegmentKind.MpegAudio;
        return HlsSegmentKind.Unknown;
    }

    private void Adopt(HlsMediaPlaylist media, bool initial)
    {
        endList = media.EndList;
        targetDuration = media.TargetDuration;
        lastReload = DateTime.UtcNow;
        int added = 0;
        IEnumerable<HlsSegment> fresh = media.Segments;
        if (initial)
        {
            // A live stream starts a little behind its edge, as the specification recommends; a finished one starts at the top.
            if (!media.EndList) fresh = media.Segments.Skip(Math.Max(0, media.Segments.Count - LiveStartBehindEdge));
            nextSequence = fresh.First().Sequence;
        }
        foreach (HlsSegment segment in fresh)
        {
            // Segments are numbered; anything older than what was already taken is a repeat, and a gap means the server moved on.
            if (segment.Sequence < nextSequence || pending.Any(queued => queued.Sequence == segment.Sequence)) continue;
            pending.Enqueue(segment);
            added++;
        }
        if (added > 0) lastProgress = DateTime.UtcNow;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException("The stream has no known length.");

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
            _ => throw new NotSupportedException("The stream has no known length.")
        };
        if (target < 0) throw new IOException("Cannot seek before the start of the stream.");
        // Like any live stream: back into the bytes kept for probing, or forward into bytes that are still arriving.
        if (target > forwardPosition && target - forwardPosition > SkipForwardLimit) throw new IOException("An HLS stream cannot be seeked.");
        if (target < forwardPosition && target >= headLength) throw new IOException("An HLS stream cannot be rewound that far.");
        position = target;
        return position;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty) return 0;
        if (position < forwardPosition)
        {
            int copied = Math.Min(buffer.Length, (int)Math.Min(headLength - position, int.MaxValue));
            head.AsSpan((int)position, copied).CopyTo(buffer);
            position += copied;
            return copied;
        }
        byte[] scratch = [];
        while (forwardPosition < position)
        {
            if (scratch.Length == 0) scratch = new byte[4096];
            int skipped = ReadForward(scratch.AsSpan(0, (int)Math.Min(scratch.Length, position - forwardPosition)));
            if (skipped == 0) return 0;
        }
        int read = ReadForward(buffer);
        position += read;
        return read;
    }

    private int ReadForward(Span<byte> buffer)
    {
        while (currentOffset >= current.Length)
        {
            if (finished || !NextSegment()) return 0;
        }
        int count = Math.Min(buffer.Length, current.Length - currentOffset);
        current.AsSpan(currentOffset, count).CopyTo(buffer);
        currentOffset += count;
        if (forwardPosition < HeadLimit)
        {
            int keep = Math.Min(count, HeadLimit - (int)forwardPosition);
            buffer[..keep].CopyTo(head.AsSpan((int)forwardPosition));
            headLength = (int)forwardPosition + keep;
        }
        forwardPosition += count;
        return count;
    }

    /// <summary>Moves to the next segment, waiting for the playlist to grow on a live stream. False at the end of the stream.</summary>
    private bool NextSegment()
    {
        byte[]? bytes = null;
        if (prefetch is not null)
        {
            Task<byte[]> task = prefetch;
            prefetch = null;
            bytes = Wait(task);
        }
        else
        {
            HlsSegment? segment = WaitForSegment();
            if (segment is null)
            {
                finished = true;
                return false;
            }
            nextSequence = segment.Sequence + 1;
            bytes = Wait(FetchBytesAsync(client, segment.Uri, mediaPlaylistUri, MaximumSegmentBytes, options, lifetime.Token));
        }
        current = bytes;
        currentOffset = 0;
        Interlocked.Increment(ref segmentsStarted);
        StartPrefetch();
        return true;
    }

    private void StartPrefetch()
    {
        if (!pending.TryDequeue(out HlsSegment? next)) return;
        nextSequence = next.Sequence + 1;
        prefetch = FetchBytesAsync(client, next.Uri, mediaPlaylistUri, MaximumSegmentBytes, options, lifetime.Token);
    }

    private HlsSegment? WaitForSegment()
    {
        int failures = 0;
        // Stalling is time spent waiting with an empty queue, not time since a reload: the backlog of a stream with long segments
        // can take longer than the timeout to play.
        DateTime waitStart = DateTime.UtcNow;
        while (true)
        {
            if (pending.TryDequeue(out HlsSegment? segment)) return segment;
            if (endList) return null;
            if (DateTime.UtcNow - (lastProgress > waitStart ? lastProgress : waitStart) > options.StallTimeout) return null;
            TimeSpan interval = TimeSpan.FromSeconds(targetDuration);
            if (interval < options.MinimumReload) interval = options.MinimumReload;
            if (interval > options.MaximumReload) interval = options.MaximumReload;
            TimeSpan wait = lastReload + interval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero && lifetime.Token.WaitHandle.WaitOne(wait)) throw new OperationCanceledException(lifetime.Token);
            try
            {
                (string text, Uri final) = FetchPlaylistAsync(client, mediaPlaylistUri, mediaPlaylistUri, options, lifetime.Token).GetAwaiter().GetResult();
                if (HlsPlaylistParser.Parse(text, final) is not HlsMediaPlaylist media) throw new InvalidDataException("The HLS variant playlist became a master playlist.");
                playlistBase = final;
                failures = 0;
                // A server that restarted its numbering would otherwise look like old segments forever.
                if (media.MediaSequence + media.Segments.Count < nextSequence) nextSequence = media.MediaSequence;
                Adopt(media, initial: false);
            }
            catch (Exception exception) when ((exception is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException) && !lifetime.IsCancellationRequested)
            {
                lastReload = DateTime.UtcNow;
                if (++failures >= ReloadFailuresTolerated) throw new IOException("The HLS playlist could not be refreshed.", exception);
            }
        }
    }

    private byte[] Wait(Task<byte[]> task)
    {
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            throw new IOException("An HLS segment request timed out.");
        }
    }

    private static async Task<(string Text, Uri Final)> FetchPlaylistAsync(HttpClient client, Uri uri, Uri origin, HlsOptions options, CancellationToken cancellationToken)
    {
        (byte[] bytes, Uri final) = await FetchAsync(client, uri, origin, MaximumPlaylistBytes, options, cancellationToken).ConfigureAwait(false);
        // Some servers (RTP's, for one) gzip a playlist whether or not it was asked for, and the player's client does not decompress.
        if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using System.IO.Compression.GZipStream gzip = new(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
            using MemoryStream plain = new();
            byte[] chunk = new byte[16 * 1024];
            int read;
            while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (plain.Length + read > MaximumPlaylistBytes) throw new InvalidDataException("An HLS download is larger than the player allows.");
                plain.Write(chunk, 0, read);
            }
            bytes = plain.ToArray();
        }
        return (System.Text.Encoding.UTF8.GetString(bytes), final);
    }

    private static async Task<byte[]> FetchBytesAsync(HttpClient client, Uri uri, Uri origin, int limit, HlsOptions options, CancellationToken cancellationToken) =>
        (await FetchAsync(client, uri, origin, limit, options, cancellationToken).ConfigureAwait(false)).Bytes;

    private static async Task<(byte[] Bytes, Uri Final)> FetchAsync(HttpClient client, Uri uri, Uri origin, int limit, HlsOptions options, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        // Relative references resolve against where the redirects ended, but a redirect must never leave https.
        Uri final = response.RequestMessage?.RequestUri ?? uri;
        HlsPlaylistParser.ValidateAddress(final, origin);
        if (response.Content.Headers.ContentLength is { } declared && declared > limit)
            throw new InvalidDataException("An HLS download is larger than the player allows.");
        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using MemoryStream collected = new();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (collected.Length + read > limit) throw new InvalidDataException("An HLS download is larger than the player allows.");
            collected.Write(buffer, 0, read);
        }
        return (collected.ToArray(), final);
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            try { lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
            // An abandoned prefetch must not raise an unobserved exception once the request is cancelled.
            prefetch?.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            prefetch = null;
            current = [];
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal enum HlsSegmentKind
{
    Unknown,
    MpegAudio,
    Aac,
    MpegTransportStream,
    Mp4
}
