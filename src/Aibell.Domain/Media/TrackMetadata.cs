namespace Aibell.Domain.Media;

public sealed record TrackMetadata(
    string Title = "", string Artist = "", string Album = "", string AlbumArtist = "",
    string Genre = "", int Year = 0, int TrackNumber = 0, int TrackTotal = 0,
    int DiscNumber = 0, int DiscTotal = 0, int Rating = 0, double BeatsPerMinute = 0,
    string Composer = "", string Conductor = "", string Grouping = "", string Comment = "",
    string Copyright = "", string LicenseUri = "", string SortTitle = "", string SortArtist = "",
    string SortAlbumArtist = "", string SortAlbum = "", string Description = "", string Keywords = "",
    string ShowName = "", int SeasonNumber = 0, int EpisodeNumber = 0)
{
    public static TrackMetadata FromTrack(Track track) => (track.Metadata ?? new()) with
    {
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        AlbumArtist = track.AlbumArtist,
        Genre = track.Genre,
        Year = track.Year,
        TrackNumber = track.TrackNumber,
        DiscNumber = track.DiscNumber,
        Rating = track.Rating,
        BeatsPerMinute = track.BeatsPerMinute,
        Composer = track.Composer,
        Comment = track.Comment,
        Description = track.Description,
        Keywords = track.Keywords,
        ShowName = track.ShowName,
        SeasonNumber = track.SeasonNumber,
        EpisodeNumber = track.EpisodeNumber
    };

    public void Validate()
    {
        if (Year is < 0 or > 9999 || Rating is < 0 or > 5 ||
            TrackNumber < 0 || TrackTotal < 0 || DiscNumber < 0 || DiscTotal < 0 || SeasonNumber < 0 || EpisodeNumber < 0 ||
            !double.IsFinite(BeatsPerMinute) || BeatsPerMinute < 0)
            throw new ArgumentException("Enter valid non-negative numbers; years range from 0 to 9999 and ratings from 0 to 5.");
        if (TrackTotal > 0 && TrackNumber > TrackTotal || DiscTotal > 0 && DiscNumber > DiscTotal)
            throw new ArgumentException("A track or disc number cannot exceed its total.");
        foreach (string? value in new[] { Title, Artist, Album, AlbumArtist, Genre, Composer, Conductor, Grouping,
            Comment, Copyright, LicenseUri, SortTitle, SortArtist, SortAlbumArtist, SortAlbum, Description, Keywords, ShowName })
            if (value is null || value.Contains('\0', StringComparison.Ordinal))
                throw new ArgumentException("Metadata text cannot be null or contain a null character.");
    }
}
