using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aibell.Application.Lyrics;

namespace Aibell.Lyrics.Lrclib;

/// <summary>
/// Talks to the public LRCLIB API (<c>/api/get</c> for the best match, <c>/api/search</c> for manual search). Every expected
/// failure (offline, timeout, 404, 429, bad JSON) is "no result"; only the caller's cancellation propagates.
/// </summary>
internal sealed class LrclibLyricsProvider(HttpClient httpClient, Uri? baseUri = null) : ILyricsProvider
{
    internal const string ProviderId = "lrclib";
    internal const string UserAgent = "Aibell/1.1 (https://github.com/tiglate/Aibell)";
    internal const long MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private readonly Uri root = baseUri ?? new Uri("https://lrclib.net/api/");

    public LyricsProviderDescriptor Descriptor { get; } = new(ProviderId, "LRCLIB");

    public async Task<LyricsCandidate?> FindAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsSearchable) return null;
        List<string> parts = [$"track_name={Escape(query.Title)}", $"artist_name={Escape(query.Artist)}"];
        if (!string.IsNullOrWhiteSpace(query.Album)) parts.Add($"album_name={Escape(query.Album)}");
        int seconds = (int)Math.Round(query.Duration.TotalSeconds);
        if (seconds is >= 1 and <= 3600) parts.Add($"duration={seconds.ToString(CultureInfo.InvariantCulture)}");
        Record? record = await GetAsync<Record>("get?" + string.Join('&', parts), cancellationToken).ConfigureAwait(false);
        return record?.ToCandidate();
    }

    public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Title) && string.IsNullOrWhiteSpace(query.Artist)) return [];
        string request = string.IsNullOrWhiteSpace(query.Title)
            ? $"search?q={Escape(query.Artist)}"
            : $"search?track_name={Escape(query.Title)}" + (string.IsNullOrWhiteSpace(query.Artist) ? "" : $"&artist_name={Escape(query.Artist)}");
        Record[]? records = await GetAsync<Record[]>(request, cancellationToken).ConfigureAwait(false);
        return records is null ? [] : [.. records.Where(record => record is not null).Select(record => record.ToCandidate())];
    }

    private static string Escape(string value) => Uri.EscapeDataString(value.Trim());

    private async Task<T?> GetAsync<T>(string relative, CancellationToken cancellationToken) where T : class
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(RequestTimeout);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root, relative));
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentType?.MediaType is not "application/json") return null;
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, limit.Token).ConfigureAwait(false);
            await using Stream body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(body, cancellationToken: limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class Record
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("albumName")] public string? AlbumName { get; set; }
        [JsonPropertyName("duration")] public double Duration { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }

        public LyricsCandidate ToCandidate() => new(
            ProviderId, Id.ToString(CultureInfo.InvariantCulture), TrackName ?? "", ArtistName ?? "", AlbumName ?? "",
            TimeSpan.FromSeconds(Math.Clamp(double.IsFinite(Duration) ? Duration : 0, 0, 86_400)), SyncedLyrics, PlainLyrics, Instrumental);
    }
}
