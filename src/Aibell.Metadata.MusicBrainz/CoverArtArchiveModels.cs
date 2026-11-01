using System.Text.Json.Serialization;

namespace Aibell.Metadata.MusicBrainz;

internal sealed record CoverArtArchiveResponse(
    [property: JsonPropertyName("images")] List<CoverArtImage>? Images);

internal sealed record CoverArtImage(
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("front")] bool Front,
    [property: JsonPropertyName("back")] bool Back,
    [property: JsonPropertyName("types")] List<string>? Types,
    [property: JsonPropertyName("thumbnails")] CoverArtThumbnails? Thumbnails);

internal sealed record CoverArtThumbnails(
    [property: JsonPropertyName("250")] string? Small,
    [property: JsonPropertyName("500")] string? Large,
    [property: JsonPropertyName("1200")] string? ExtraLarge);
