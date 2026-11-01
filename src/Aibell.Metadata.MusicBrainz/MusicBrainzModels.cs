using System.Text.Json.Serialization;

namespace Aibell.Metadata.MusicBrainz;

internal sealed record MusicBrainzSearchResponse(
    [property: JsonPropertyName("recordings")] List<MusicBrainzRecording>? Recordings);

internal sealed record MusicBrainzRecording(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("length")] int? Length,
    [property: JsonPropertyName("first-release-date")] string? FirstReleaseDate,
    [property: JsonPropertyName("artist-credit")] List<MusicBrainzArtistCredit>? ArtistCredit,
    [property: JsonPropertyName("releases")] List<MusicBrainzRelease>? Releases);

internal sealed record MusicBrainzArtistCredit(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("joinphrase")] string? JoinPhrase,
    [property: JsonPropertyName("artist")] MusicBrainzArtist? Artist);

internal sealed record MusicBrainzArtist(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record MusicBrainzRelease(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("release-group")] MusicBrainzReleaseGroup? ReleaseGroup);

internal sealed record MusicBrainzReleaseGroup(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title);

internal sealed record MusicBrainzDiscResponse(
    [property: JsonPropertyName("releases")] List<MusicBrainzDetailedRelease>? Releases);

internal sealed record MusicBrainzReleaseSearchResponse(
    [property: JsonPropertyName("releases")] List<MusicBrainzDetailedRelease>? Releases);

internal sealed record MusicBrainzDetailedRelease(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("artist-credit")] List<MusicBrainzArtistCredit>? ArtistCredit,
    [property: JsonPropertyName("genres")] List<MusicBrainzGenre>? Genres,
    [property: JsonPropertyName("release-group")] MusicBrainzDetailedReleaseGroup? ReleaseGroup,
    [property: JsonPropertyName("media")] List<MusicBrainzMedium>? Media);

internal sealed record MusicBrainzDetailedReleaseGroup(
    [property: JsonPropertyName("genres")] List<MusicBrainzGenre>? Genres);

internal sealed record MusicBrainzGenre(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("count")] int Count);

internal sealed record MusicBrainzMedium(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("discs")] List<MusicBrainzDiscReference>? Discs,
    [property: JsonPropertyName("tracks")] List<MusicBrainzTrack>? Tracks);

internal sealed record MusicBrainzDiscReference(
    [property: JsonPropertyName("id")] string? Id);

internal sealed record MusicBrainzTrack(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("length")] int? Length,
    [property: JsonPropertyName("artist-credit")] List<MusicBrainzArtistCredit>? ArtistCredit);

internal sealed record MusicBrainzArtistSearchResponse(
    [property: JsonPropertyName("artists")] List<MusicBrainzArtistDetail>? Artists);

internal sealed record MusicBrainzArtistDetail(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("disambiguation")] string? Disambiguation,
    [property: JsonPropertyName("area")] MusicBrainzArea? Area,
    [property: JsonPropertyName("life-span")] MusicBrainzLifeSpan? LifeSpan,
    [property: JsonPropertyName("genres")] List<MusicBrainzGenre>? Genres,
    [property: JsonPropertyName("relations")] List<MusicBrainzRelation>? Relations);

internal sealed record MusicBrainzArea(
    [property: JsonPropertyName("name")] string? Name);

internal sealed record MusicBrainzLifeSpan(
    [property: JsonPropertyName("begin")] string? Begin,
    [property: JsonPropertyName("end")] string? End);

internal sealed record MusicBrainzRelation(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("url")] MusicBrainzUrl? Url);

internal sealed record MusicBrainzUrl(
    [property: JsonPropertyName("resource")] string? Resource);
