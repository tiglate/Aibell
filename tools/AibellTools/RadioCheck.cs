using System.Net;
using System.Text.Json;
using Aibell.Audio.Aac;

namespace AibellTools;

/// <summary>
/// Checks that every station in the curated radio catalog still delivers audio. This is deliberately a live, network-dependent
/// command and not part of the test suite: stations move or go offline for reasons that are not regressions in Aibell.
/// </summary>
internal static class RadioCheck
{
    private const int MinimumAudioBytes = 4096;
    private const int DirectProbeBytes = 16 * 1024;
    private const int SegmentProbeBytes = 48 * 1024;
    private const long MaximumPlaylistBytes = 512 * 1024;
    private const string CatalogPath = "src/Aibell.Infrastructure/Radio/curated-stations.json";

    internal sealed record Station(string Id, string Name, string CountryCode, string Stream, bool Hls, string? Logo);

    /// <summary>Whether the built-in (miniaudio + managed AAC) engine can play a stream, as far as its first bytes show.</summary>
    internal enum Native
    {
        Unknown,
        Yes,
        No
    }

    /// <summary>The outcome for one station. <paramref name="Format"/> names what was detected (container and codec); <paramref name="Support"/> says if the built-in engine plays it.</summary>
    internal sealed record Result(Station Station, bool Ok, string Detail, string Format = "", Native Support = Native.Unknown);

    public static int Run(string[] args)
    {
        Options options = new(args, new HashSet<string> { "--logos" }, new HashSet<string> { "--timeout", "--concurrency", "--only", "--catalog" });
        string catalog = options.Value("--catalog") ?? Path.Combine(RepositoryRoot.Find(), CatalogPath);
        int timeout = options.Value("--timeout") is { } t ? ParsePositive(t, "--timeout") : 15;
        int concurrency = options.Value("--concurrency") is { } c ? ParsePositive(c, "--concurrency") : 8;
        string? only = options.Value("--only");

        List<Station> stations = Load(catalog);
        if (only is not null)
            stations = [.. stations.Where(s => s.Id.Equals(only, StringComparison.OrdinalIgnoreCase) ||
                s.CountryCode.Equals(only, StringComparison.OrdinalIgnoreCase))];
        if (stations.Count == 0) throw new ToolException("No stations match.");

        using HttpClient http = CreateClient();
        Console.WriteLine($"Checking {stations.Count} stations (timeout {timeout}s, {concurrency} at a time)...");
        List<Result> results = CheckAllAsync(http, stations, concurrency, options.Has("--logos"), timeout).GetAwaiter().GetResult();
        return Report(results, Console.Out);
    }

    internal static List<Station> Load(string path)
    {
        if (!File.Exists(path)) throw new ToolException($"Catalog not found: {path}");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("stations", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            throw new ToolException("The catalog has no stations array.");
        List<Station> stations = [];
        foreach (JsonElement item in array.EnumerateArray())
        {
            string? id = Text(item, "id"), name = Text(item, "name"), stream = Text(item, "stream");
            if (id is null || name is null || stream is null) throw new ToolException($"A catalog entry is missing id, name or stream: {item.GetRawText()}");
            bool hls = item.TryGetProperty("hls", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
            stations.Add(new Station(id, name, Text(item, "countryCode") ?? "", stream, hls, Text(item, "logo")));
        }
        return stations;
    }

    internal static HttpClient CreateClient()
    {
        // No automatic decompression: gzip over an endless stream buffers instead of delivering audio. Redirects are followed by hand
        // (see GetAsync) because stations often hop from https to http, which HttpClient refuses to follow.
        // Some stations answer 403 to a browser agent, so identify as a plain player.
        HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback = ConnectIpv4FirstAsync })
        { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AibellRadioCheck/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Icy-MetaData", "0");
        return client;
    }

    internal static async Task<List<Result>> CheckAllAsync(HttpClient http, IReadOnlyList<Station> stations, int concurrency, bool logos, int timeoutSeconds = 20)
    {
        using SemaphoreSlim gate = new(concurrency);
        Result[] results = new Result[stations.Count];
        await Task.WhenAll(stations.Select(async (station, index) =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Result result = await CheckAsync(http, station, TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
                // One retry: a single dropped connection is not an outage.
                if (!result.Ok) result = await CheckAsync(http, station, TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
                if (result.Ok && logos && station.Logo is { Length: > 0 } logo && await CheckLogoAsync(http, logo).ConfigureAwait(false) is { } problem)
                    result = result with { Detail = result.Detail + $" (logo: {problem})" };
                results[index] = result;
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return [.. results];
    }

    internal static async Task<Result> CheckAsync(HttpClient http, Station station, TimeSpan? timeout = null)
    {
        using CancellationTokenSource limit = new(timeout ?? TimeSpan.FromSeconds(20));
        try
        {
            return station.Hls || Path.GetExtension(new Uri(station.Stream).AbsolutePath).Equals(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? await CheckHlsAsync(http, station, limit.Token).ConfigureAwait(false)
                : await CheckDirectAsync(http, station, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new Result(station, false, "timed out"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or UriFormatException)
        {
            return new Result(station, false, exception.InnerException?.Message ?? exception.Message);
        }
    }

    private static async Task<Result> CheckDirectAsync(HttpClient http, Station station, CancellationToken token)
    {
        using HttpResponseMessage response = await GetAsync(http, new Uri(station.Stream), token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new Result(station, false, $"HTTP {(int)response.StatusCode}");
        string type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!LooksLikeAudio(type)) return new Result(station, false, $"unexpected content type '{type}'");
        byte[] head = await ReadHeadAsync(response, DirectProbeBytes, token).ConfigureAwait(false);
        if (head.Length < MinimumAudioBytes) return new Result(station, false, $"stream ended after {head.Length} bytes");
        (string format, Native support) = Classify(head, type);
        return new Result(station, true, type, format, support);
    }

    private static async Task<Result> CheckHlsAsync(HttpClient http, Station station, CancellationToken token)
    {
        Uri playlistUri = new(station.Stream);
        for (int depth = 0; depth < 3; depth++)
        {
            (string text, Uri final) = await FetchTextAsync(http, playlistUri, token).ConfigureAwait(false);
            if (!text.TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal))
                return new Result(station, false, "not an HLS playlist");
            string[] lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool master = lines.Any(line => line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal));
            string? target = master ? ChooseVariant(lines) : lines.FirstOrDefault(line => !line.StartsWith('#'));
            if (target is null) return new Result(station, false, "playlist lists no segments");
            Uri next = new(final, target);
            if (master) { playlistUri = next; continue; }
            string? limitation = lines.Select(HlsLimitation).FirstOrDefault(reason => reason is not null);
            using HttpResponseMessage segment = await GetAsync(http, next, token).ConfigureAwait(false);
            if (!segment.IsSuccessStatusCode) return new Result(station, false, $"segment HTTP {(int)segment.StatusCode}");
            byte[] head = await ReadHeadAsync(segment, SegmentProbeBytes, token).ConfigureAwait(false);
            if (head.Length < MinimumAudioBytes) return new Result(station, false, $"segment of only {head.Length} bytes");
            (string format, Native support) = Classify(head, segment.Content.Headers.ContentType?.MediaType ?? "");
            if (limitation is not null) (format, support) = (format.Contains(limitation, StringComparison.Ordinal) ? format : $"{format}, {limitation}", Native.No);
            return new Result(station, true, "HLS", "HLS " + format, support);
        }
        return new Result(station, false, "playlist nesting too deep");
    }

    // The tags the built-in player refuses even when it could decode the audio.
    private static string? HlsLimitation(string line) =>
        line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal) && !line.Contains("METHOD=NONE", StringComparison.Ordinal) ? "encrypted" :
        line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal) ? "fragmented MP4" :
        line.StartsWith("#EXT-X-BYTERANGE", StringComparison.Ordinal) ? "byte ranges" : null;

    /// <summary>Picks the variant the built-in player would: audio-only first, then MP3, AAC-LC, anything else, HE-AAC last, then the lowest bandwidth.</summary>
    internal static string? ChooseVariant(string[] lines)
    {
        string? best = null;
        (int Video, int Rank, long Bandwidth) bestKey = (int.MaxValue, int.MaxValue, long.MaxValue);
        for (int i = 0; i + 1 < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) continue;
            string attributes = lines[i];
            string? uri = lines.Skip(i + 1).FirstOrDefault(line => !line.StartsWith('#'));
            if (uri is null) continue;
            string codecs = Attribute(attributes, "CODECS") ?? "";
            bool video = attributes.Contains("RESOLUTION=", StringComparison.Ordinal) || codecs.Contains("avc", StringComparison.OrdinalIgnoreCase) ||
                codecs.Contains("hvc", StringComparison.OrdinalIgnoreCase) || codecs.Contains("hev", StringComparison.OrdinalIgnoreCase);
            int rank = codecs.Length == 0 ? 2 : codecs.Contains("mp4a.40.34", StringComparison.OrdinalIgnoreCase) ? 0 :
                codecs.Contains("mp4a.40.2", StringComparison.OrdinalIgnoreCase) ? 1 :
                codecs.Contains("mp4a.40.5", StringComparison.OrdinalIgnoreCase) || codecs.Contains("mp4a.40.29", StringComparison.OrdinalIgnoreCase) ? 4 : 3;
            long bandwidth = long.TryParse(Attribute(attributes, "BANDWIDTH"), out long declared) ? declared : 0;
            (int, int, long) key = (video ? 1 : 0, rank, bandwidth == 0 ? long.MaxValue : bandwidth);
            if (key.CompareTo(bestKey) < 0)
            {
                best = uri;
                bestKey = key;
            }
        }
        return best;
    }

    private static string? Attribute(string line, string name)
    {
        int at = line.IndexOf(name + "=", StringComparison.Ordinal);
        if (at < 0) return null;
        at += name.Length + 1;
        if (at < line.Length && line[at] == '"')
        {
            int close = line.IndexOf('"', at + 1);
            return close < 0 ? null : line[(at + 1)..close];
        }
        int end = line.IndexOf(',', at);
        return end < 0 ? line[at..] : line[at..end];
    }

    /// <summary>Names the container and codec in the first bytes of a stream and says whether the built-in engine plays it.</summary>
    internal static (string Format, Native Support) Classify(ReadOnlySpan<byte> head, string contentType)
    {
        AacStreamInfo info = AacStreamInspector.Inspect(head);
        if (info.Container != AacContainer.Unknown)
        {
            if (info.Codec is "MP3") return ("MPEG-TS MP3", Native.No);
            if (info.Codec == "AC-3") return ("MPEG-TS AC-3", Native.No);
            if (info.Container == AacContainer.Mp4) return ("fragmented MP4", Native.No);
            return (info.ToString(), info.IsDecodable ? Native.Yes : Native.No);
        }
        int skip = Id3.TagLength(head);
        ReadOnlySpan<byte> audio = skip > 0 && skip < head.Length ? head[skip..] : head;
        if (FindMpegAudio(audio)) return ("MP3", Native.Yes);
        if (audio.StartsWith("OggS"u8))
            return head.IndexOf("OpusHead"u8) >= 0 ? ("Ogg Opus", Native.No) : head.IndexOf("vorbis"u8) >= 0 ? ("Ogg Vorbis", Native.Yes) : ("Ogg", Native.Unknown);
        if (audio.StartsWith("fLaC"u8)) return ("FLAC", Native.Yes);
        if (audio.StartsWith("RIFF"u8)) return ("WAV", Native.Yes);
        return ($"unrecognized ({(contentType.Length == 0 ? "no content type" : contentType)})", Native.Unknown);
    }

    // A host with a dead IPv6 route would otherwise report every dual-stack station as down while IPv4 works fine.
    private static async ValueTask<Stream> ConnectIpv4FirstAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        string host = context.DnsEndPoint.Host;
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
        Exception? last = null;
        foreach (IPAddress address in addresses.OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1))
        {
            System.Net.Sockets.Socket socket = new(address.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), attempt.Token).ConfigureAwait(false);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                socket.Dispose();
                last = exception;
            }
        }
        throw new HttpRequestException($"could not connect to {host}", last);
    }

    /// <summary>GET that follows up to five redirects, including https to http, and reports the final address in RequestMessage.</summary>
    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, Uri uri, CancellationToken token, bool gzip = false)
    {
        for (int hop = 0; ; hop++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            if (gzip) request.Headers.AcceptEncoding.ParseAdd("gzip");
            HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) || response.Headers.Location is not { } location)
                return response;
            response.Dispose();
            if (hop >= 5) throw new HttpRequestException("too many redirects");
            uri = new Uri(uri, location);
        }
    }

    private static async Task<(string Text, Uri Final)> FetchTextAsync(HttpClient http, Uri uri, CancellationToken token)
    {
        using HttpResponseMessage response = await GetAsync(http, uri, token, gzip: true).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // Some playlist servers (RTP's HLS, for one) compress the playlist whether or not it was asked for.
        await using Stream raw = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using Stream stream = response.Content.Headers.ContentEncoding.Contains("gzip") ? new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress) : raw;
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, count);
            if (buffer.Length > MaximumPlaylistBytes) throw new InvalidDataException("playlist too large");
        }
        return (System.Text.Encoding.UTF8.GetString(buffer.ToArray()), response.RequestMessage?.RequestUri ?? uri);
    }

    private static async Task<string?> CheckLogoAsync(HttpClient http, string logo)
    {
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(15));
        try
        {
            using HttpResponseMessage response = await GetAsync(http, new Uri(logo), limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return $"HTTP {(int)response.StatusCode}";
            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            return type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? null : $"content type '{type}'";
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or UriFormatException) { return "unreachable"; }
    }

    private static bool LooksLikeAudio(string type) =>
        type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("application/ogg", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("video/mp2t", StringComparison.OrdinalIgnoreCase) || type.Length == 0;

    /// <summary>Reads up to <paramref name="wanted"/> bytes of the body; what arrived before the time ran out or the stream ended is returned.</summary>
    private static async Task<byte[]> ReadHeadAsync(HttpResponseMessage response, int wanted, CancellationToken token)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] buffer = new byte[wanted];
        int total = 0;
        try
        {
            while (total < wanted)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(total, wanted - total), token).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
            }
        }
        catch (OperationCanceledException) when (total > 0) { }
        return buffer[..total];
    }

    internal static int Report(IReadOnlyList<Result> results, TextWriter output)
    {
        foreach (Result result in results.OrderBy(r => r.Station.CountryCode).ThenBy(r => r.Station.Name, StringComparer.OrdinalIgnoreCase))
            output.WriteLine($"{(result.Ok ? "ok  " : "FAIL")} {result.Station.CountryCode,-3} {result.Station.Name,-42} {Describe(result)}");
        List<Result> failed = [.. results.Where(r => !r.Ok)];
        output.WriteLine();
        output.WriteLine($"{results.Count - failed.Count} of {results.Count} stations available.");
        List<Result> available = [.. results.Where(r => r.Ok)];
        output.WriteLine($"Built-in engine: {available.Count(r => r.Support == Native.Yes)} play natively, {available.Count(r => r.Support == Native.No)} need VLC, " +
            $"{available.Count(r => r.Support == Native.Unknown)} not identified.");
        foreach (IGrouping<string, Result> group in available.Where(r => r.Support == Native.No).GroupBy(r => r.Format).OrderByDescending(g => g.Count()))
            output.WriteLine($"  needs VLC: {group.Key} x{group.Count()}: {string.Join(", ", group.Select(r => r.Station.Name).Order(StringComparer.OrdinalIgnoreCase))}");
        if (failed.Count == 0) return 0;
        output.WriteLine("Unavailable (id, stream):");
        foreach (Result result in failed) output.WriteLine($"  {result.Station.Id}  {result.Station.Stream}  -> {result.Detail}");
        return 1;
    }

    private static readonly int[] Mpeg1Layer3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg1Layer2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] Mpeg2Layers = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[][] MpegSampleRates = [[11025, 12000, 8000], [], [22050, 24000, 16000], [44100, 48000, 32000]];

    /// <summary>The length of the MPEG audio (layer II or III) frame whose header starts <paramref name="header"/>, or 0 when it is not a valid header.</summary>
    private static int MpegFrameLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4 || header[0] != 0xff || (header[1] & 0xe0) != 0xe0) return 0;
        int version = header[1] >> 3 & 3, layer = header[1] >> 1 & 3, bitrateIndex = header[2] >> 4, rateIndex = header[2] >> 2 & 3;
        if (version == 1 || layer is 0 or 3 || bitrateIndex is 0 or 15 || rateIndex == 3) return 0;
        int kilobits = version == 3 ? layer == 1 ? Mpeg1Layer3[bitrateIndex] : Mpeg1Layer2[bitrateIndex] : Mpeg2Layers[bitrateIndex];
        int rate = MpegSampleRates[version][rateIndex];
        int coefficient = version != 3 && layer == 1 ? 72 : 144;
        return coefficient * kilobits * 1000 / rate + (header[2] >> 1 & 1);
    }

    /// <summary>True when three MPEG audio frames follow each other somewhere in the data (a live stream may begin in the middle of a frame).</summary>
    private static bool FindMpegAudio(ReadOnlySpan<byte> data)
    {
        for (int start = 0; start + 4 <= data.Length && start < 4096; start++)
        {
            int at = start, chain = 0;
            while (MpegFrameLength(at + 4 <= data.Length ? data[at..] : []) is int length and > 0)
            {
                chain++;
                at += length;
                if (chain == 3) return true;
            }
        }
        return false;
    }

    private static string Describe(Result result)
    {
        if (!result.Ok || result.Format.Length == 0) return result.Detail;
        string verdict = result.Support switch { Native.Yes => "native", Native.No => "needs VLC", _ => "?" };
        return $"{result.Format} [{verdict}] ({result.Detail})";
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static int ParsePositive(string value, string option) =>
        int.TryParse(value, out int number) && number > 0 ? number : throw new ToolException($"{option} must be a positive number.");
}
