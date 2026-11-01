namespace Aibell.Domain.Radio;

public sealed record RadioStation(Guid Id, string Name, Uri Stream, string Genre = "", string Creator = "",
    string Description = "", int Rating = 0, int PlayCount = 0, DateTimeOffset? LastPlayed = null,
    string? Country = null, string? CountryCode = null, Uri? LogoUri = null, string? CatalogId = null)
{
    /// <summary>The identifier a station keeps when it was copied from the curated catalog: lowercase letters, digits and hyphens.</summary>
    public static bool IsValidCatalogId(string? value) =>
        value is { Length: > 0 and <= 100 } && value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') && value[0] != '-';

    /// <summary>An ISO 3166-1 alpha-2 style code: exactly two uppercase ASCII letters.</summary>
    public static bool IsValidCountryCode(string? value) =>
        value is { Length: 2 } && value.All(character => character is >= 'A' and <= 'Z');

    /// <summary>A logo must be a plain https address: it is fetched from the station's own host, so it never travels in clear text.</summary>
    public static bool IsValidLogo(Uri? value) =>
        value is { IsAbsoluteUri: true, Scheme: "https" } && value.OriginalString.Length <= 2048 && value.IsWellFormedOriginalString() &&
        !value.OriginalString.Any(char.IsWhiteSpace) && value.UserInfo.Length == 0 && value.Fragment.Length == 0 && !string.IsNullOrWhiteSpace(value.Host);

    private static readonly HashSet<string> PlaylistExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".m3u", ".pls", ".xspf" };

    /// <summary>True for an HLS (HTTP Live Streaming) address: an http(s) URL whose path ends in <c>.m3u8</c>.</summary>
    public bool IsHls => IsHlsStream(Stream);

    /// <summary>The one place that decides whether a stream address is an HLS manifest; engines and importers call this.</summary>
    public static bool IsHlsStream(Uri? stream) =>
        stream is { IsAbsoluteUri: true, Scheme: "http" or "https" } &&
        string.Equals(Path.GetExtension(stream.AbsolutePath), ".m3u8", StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        if (Id == Guid.Empty) throw new ArgumentException("A station needs an identifier.");
        Text(Name, 200, "Station name", required: true);
        Text(Genre, 100, "Genre"); Text(Creator, 200, "Creator"); Text(Description, 2000, "Description");
        if (Country is not null) Text(Country, 100, "Country");
        if (CountryCode is not null && !IsValidCountryCode(CountryCode)) throw new ArgumentException("The country code must be two capital letters.");
        if (CatalogId is not null && !IsValidCatalogId(CatalogId)) throw new ArgumentException("The catalog identifier is invalid.");
        if (LogoUri is not null && !IsValidLogo(LogoUri)) throw new ArgumentException("The logo must be a complete HTTPS address without credentials or a fragment.");
        if (Rating is < 0 or > 5 || PlayCount < 0) throw new ArgumentException("Station rating or play count is invalid.");
        if (Stream is null || !Stream.IsAbsoluteUri || Stream.Scheme is not ("http" or "https") ||
            Stream.OriginalString.Length > 4096 || !Stream.IsWellFormedOriginalString() ||
            Stream.OriginalString.Any(char.IsWhiteSpace) || Stream.UserInfo.Length > 0 || Stream.Fragment.Length > 0 ||
            string.IsNullOrWhiteSpace(Stream.Host))
            throw new ArgumentException("Use a complete HTTP or HTTPS stream URL without embedded credentials or a fragment.");
        if (PlaylistExtensions.Contains(Path.GetExtension(Stream.AbsolutePath)))
            throw new ArgumentException("Use the station's direct audio stream URL or an HLS (.m3u8) live-stream URL. M3U, PLS and XSPF stream playlists are not supported.");
    }

    private static void Text(string value, int maximum, string field, bool required = false)
    {
        if (value is null || value.Length > maximum || value.Any(char.IsControl) || (required && string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException($"{field} must contain {(required ? "1–" : "at most ")}{maximum} characters without control characters.");
    }
}
