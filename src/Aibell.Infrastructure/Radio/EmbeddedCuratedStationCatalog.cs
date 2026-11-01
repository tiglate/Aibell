using System.Reflection;
using System.Text.Json;
using Aibell.Application.Radio;
using Aibell.Domain.Radio;

namespace Aibell.Infrastructure.Radio;

/// <summary>
/// Reads the curated station list embedded in this assembly (<c>curated-stations.json</c>). Every entry is validated with the
/// same rules as a station the user types in; an invalid entry is skipped and a broken file yields an empty list, never an exception.
/// </summary>
public sealed class EmbeddedCuratedStationCatalog : ICuratedStationCatalog
{
    internal const string ResourceName = "Aibell.Infrastructure.Radio.curated-stations.json";
    private readonly Lazy<IReadOnlyList<CuratedStation>> stations = new(Load);

    public IReadOnlyList<CuratedStation> Stations => stations.Value;

    private static IReadOnlyList<CuratedStation> Load()
    {
        try
        {
            using Stream? stream = typeof(EmbeddedCuratedStationCatalog).Assembly.GetManifestResourceStream(ResourceName);
            return stream is null ? [] : Parse(stream);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or ArgumentException)
        { return []; }
    }

    internal static IReadOnlyList<CuratedStation> Parse(Stream json)
    {
        using StreamReader reader = new(json, System.Text.Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    internal static IReadOnlyList<CuratedStation> Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Skip });
            return Parse(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        { return []; }
    }

    private static List<CuratedStation> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("stations", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            return [];
        List<CuratedStation> result = [];
        HashSet<string> ids = new(StringComparer.Ordinal), streams = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (TryRead(entry) is not { } station) continue;
            // The first entry wins: catalog order is popularity order, and ids and streams must stay unique.
            if (!ids.Add(station.Id) || !streams.Add(station.Stream.AbsoluteUri)) continue;
            result.Add(station);
        }
        return result;
    }

    private static CuratedStation? TryRead(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        string? id = Text(entry, "id"), name = Text(entry, "name"), code = Text(entry, "countryCode"), country = Text(entry, "country"),
            genre = Text(entry, "genre"), description = Text(entry, "description") ?? "";
        if (id is null || name is null || code is null || country is null || genre is null) return null;
        if (!Uri.TryCreate(Text(entry, "stream"), UriKind.Absolute, out Uri? stream)) return null;
        Uri? logo = Uri.TryCreate(Text(entry, "logo"), UriKind.Absolute, out Uri? parsedLogo) && RadioStation.IsValidLogo(parsedLogo) ? parsedLogo : null;
        Uri? homepage = Uri.TryCreate(Text(entry, "homepage"), UriKind.Absolute, out Uri? parsedHomepage) &&
            parsedHomepage.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(parsedHomepage.Host) ? parsedHomepage : null;
        CuratedStation station = new(id, name.Trim(), code, country.Trim(), genre.Trim(), description.Trim(), stream, logo, homepage, UnsupportedFormatOf(entry));
        try
        {
            if (!RadioStation.IsValidCatalogId(id)) return null;
            station.ToRadioStation().Validate();
            return station;
        }
        catch (ArgumentException) { return null; }
    }

    // A short label such as "HE-AAC"; anything empty, over-long or with control characters is ignored rather than shown.
    private static string? UnsupportedFormatOf(JsonElement entry) =>
        Text(entry, "unsupportedFormat")?.Trim() is { Length: > 0 and <= 24 } format && !format.Any(char.IsControl) ? format : null;

    private static string? Text(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
