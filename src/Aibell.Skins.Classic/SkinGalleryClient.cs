using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Aibell.Skins.Classic;

/// <summary>A skin listed by the online skin museum.</summary>
public sealed record GallerySkin(string Md5, string FileName, Uri ScreenshotUrl, Uri DownloadUrl)
{
    /// <summary>The file name without its extension, which is what the museum shows as the skin's name.</summary>
    public string Name => Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>
/// Reads the public catalogue behind skins.webamp.org: the museum's own order when browsing, its search otherwise. Skins the
/// museum flags as not safe for work are never returned, and only https downloads from the museum's own storage are accepted.
/// Nothing about the user is sent: only the search words and the page wanted.
/// </summary>
public sealed class SkinGalleryClient(HttpClient http)
{
    public static Uri Endpoint { get; } = new("https://api.webamp.org/graphql");

    public static Uri Website { get; } = new("https://skins.webamp.org/");

    private const string SkinFields = "md5 filename screenshot_url download_url nsfw";

    private static readonly string[] TrustedHosts = ["r2.webampskins.org", "cdn.webampskins.org"];

    /// <summary>One page of skins: the museum's order when <paramref name="query"/> is blank, search results otherwise.</summary>
    public async Task<IReadOnlyList<GallerySkin>> GetPageAsync(string? query, int offset, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        bool searching = !string.IsNullOrWhiteSpace(query);
        object request = searching
            ? new
            {
                query = $"query($q:String!,$n:Int,$o:Int){{search_classic_skins(query:$q,first:$n,offset:$o,nsfw:false){{{SkinFields}}}}}",
                variables = new { q = query!.Trim(), n = count, o = offset },
            }
            : new
            {
                query = $"query($n:Int,$o:Int){{skins(first:$n,offset:$o,sort:MUSEUM){{nodes{{... on ClassicSkin{{{SkinFields}}}}}}}}}",
                variables = new { n = count, o = offset },
            };
        using HttpResponseMessage response = await http.PostAsJsonAsync(Endpoint, request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        GraphResponse? body = await response.Content.ReadFromJsonAsync<GraphResponse>(cancellationToken).ConfigureAwait(false);
        if (body?.Errors is { Length: > 0 } errors) throw new HttpRequestException(errors[0].Message ?? "The skin museum returned an error.");
        IEnumerable<GraphSkin?> skins = searching ? body?.Data?.Search ?? [] : body?.Data?.Skins?.Nodes ?? [];
        List<GallerySkin> result = [];
        foreach (GraphSkin? skin in skins)
        {
            if (skin is null || skin.Nsfw == true || string.IsNullOrEmpty(skin.Md5) || string.IsNullOrEmpty(skin.FileName)) continue;
            if (!TryTrusted(skin.ScreenshotUrl, out Uri? screenshot) || !TryTrusted(skin.DownloadUrl, out Uri? download)) continue;
            result.Add(new GallerySkin(skin.Md5, skin.FileName, screenshot, download));
        }
        return result;
    }

    /// <summary>Downloads a skin archive, refusing anything larger than a skin can be.</summary>
    public Task<byte[]> DownloadAsync(GallerySkin skin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skin);
        return ReadLimitedAsync(skin.DownloadUrl, SkinArchive.MaximumArchiveBytes, cancellationToken);
    }

    /// <summary>Downloads a skin's preview picture.</summary>
    public Task<byte[]> DownloadScreenshotAsync(GallerySkin skin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skin);
        return ReadLimitedAsync(skin.ScreenshotUrl, 2 * 1024 * 1024, cancellationToken);
    }

    private async Task<byte[]> ReadLimitedAsync(Uri url, long limit, CancellationToken cancellationToken)
    {
        if (!TryTrusted(url.AbsoluteUri, out _)) throw new HttpRequestException("Refusing a download from outside the skin museum.");
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidSkinException(InvalidSkinReason.TooLarge);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidSkinException(InvalidSkinReason.TooLarge);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool TryTrusted(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? url) =>
        Uri.TryCreate(text, UriKind.Absolute, out url) && url.Scheme == Uri.UriSchemeHttps &&
        TrustedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);

    private sealed class GraphResponse
    {
        [JsonPropertyName("data")] public GraphData? Data { get; set; }
        [JsonPropertyName("errors")] public GraphError[]? Errors { get; set; }
    }

    private sealed class GraphError
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class GraphData
    {
        [JsonPropertyName("search_classic_skins")] public GraphSkin?[]? Search { get; set; }
        [JsonPropertyName("skins")] public GraphConnection? Skins { get; set; }
    }

    private sealed class GraphConnection
    {
        [JsonPropertyName("nodes")] public GraphSkin?[]? Nodes { get; set; }
    }

    private sealed class GraphSkin
    {
        [JsonPropertyName("md5")] public string? Md5 { get; set; }
        [JsonPropertyName("filename")] public string? FileName { get; set; }
        [JsonPropertyName("screenshot_url")] public string? ScreenshotUrl { get; set; }
        [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("nsfw")] public bool? Nsfw { get; set; }
    }
}
