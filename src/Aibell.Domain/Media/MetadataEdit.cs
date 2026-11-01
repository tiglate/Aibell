namespace Aibell.Domain.Media;

// Null leaves a field unchanged; an empty string explicitly clears it.
public sealed record MetadataEdit(
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? Genre = null,
    int? Year = null,
    int? TrackNumber = null,
    string? Comment = null,
    string? AlbumArtist = null,
    string? Composer = null,
    string? Conductor = null,
    string? Grouping = null,
    string? Copyright = null,
    string? SortTitle = null,
    string? SortArtist = null,
    string? SortAlbumArtist = null,
    string? SortAlbum = null,
    int? TrackTotal = null,
    int? DiscNumber = null,
    int? DiscTotal = null,
    float? BeatsPerMinute = null,
    ArtworkChange? Artwork = null,
    string? Description = null,
    string? Keywords = null,
    string? LicenseUri = null,
    string? ShowName = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null)
{
    public void Validate()
    {
        Artwork?.Validate();
        if (SeasonNumber is < 0) throw new ArgumentOutOfRangeException(nameof(SeasonNumber));
        if (EpisodeNumber is < 0) throw new ArgumentOutOfRangeException(nameof(EpisodeNumber));
        if (this == new MetadataEdit()) throw new ArgumentException("Choose at least one metadata field to edit.");
        if (Year is < 0 or > 9999) throw new ArgumentOutOfRangeException(nameof(Year));
        if (TrackNumber is < 0) throw new ArgumentOutOfRangeException(nameof(TrackNumber));
        if (TrackTotal is < 0) throw new ArgumentOutOfRangeException(nameof(TrackTotal));
        if (DiscNumber is < 0) throw new ArgumentOutOfRangeException(nameof(DiscNumber));
        if (DiscTotal is < 0) throw new ArgumentOutOfRangeException(nameof(DiscTotal));
        if (BeatsPerMinute is { } bpm && (!float.IsFinite(bpm) || bpm < 0))
            throw new ArgumentOutOfRangeException(nameof(BeatsPerMinute));
        if (TrackTotal is > 0 && TrackNumber > TrackTotal || DiscTotal is > 0 && DiscNumber > DiscTotal)
            throw new ArgumentException("A track or disc number cannot exceed its total.");
        foreach (string? value in new[] { Title, Artist, Album, Genre, Comment, Description, Keywords, LicenseUri, ShowName, AlbumArtist, Composer,
            Conductor, Grouping, Copyright, SortTitle, SortArtist, SortAlbumArtist, SortAlbum })
            if (value?.Contains('\0', StringComparison.Ordinal) == true)
                throw new ArgumentException("Metadata text cannot contain a null character.");
    }
}
