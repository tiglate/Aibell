using Aibell.Domain.Media;

namespace Aibell.Application.Library;

public static class TrackFileEdit
{
    // Ratings and playback history remain library values, regardless of destination.
    public static MetadataEdit? Between(TrackMetadata original, TrackMetadata edited)
    {
        edited.Validate();
        if (edited.BeatsPerMinute > float.MaxValue)
            throw new NotSupportedException("This BPM value cannot be stored exactly in file tags. Choose Library or enter a representable value.");
        MetadataEdit patch = new(
            ShowName: Changed(original.ShowName, edited.ShowName), SeasonNumber: Changed(original.SeasonNumber, edited.SeasonNumber),
            EpisodeNumber: Changed(original.EpisodeNumber, edited.EpisodeNumber),
            Keywords: Changed(original.Keywords, edited.Keywords), LicenseUri: Changed(original.LicenseUri, edited.LicenseUri),
            Title: Changed(original.Title, edited.Title), Artist: Changed(original.Artist, edited.Artist),
            Album: Changed(original.Album, edited.Album), Genre: Changed(original.Genre, edited.Genre),
            Year: Changed(original.Year, edited.Year), TrackNumber: Changed(original.TrackNumber, edited.TrackNumber),
            Comment: Changed(original.Comment, edited.Comment), Description: Changed(original.Description, edited.Description), AlbumArtist: Changed(original.AlbumArtist, edited.AlbumArtist),
            Composer: Changed(original.Composer, edited.Composer), Conductor: Changed(original.Conductor, edited.Conductor),
            Grouping: Changed(original.Grouping, edited.Grouping), Copyright: Changed(original.Copyright, edited.Copyright),
            SortTitle: Changed(original.SortTitle, edited.SortTitle), SortArtist: Changed(original.SortArtist, edited.SortArtist),
            SortAlbumArtist: Changed(original.SortAlbumArtist, edited.SortAlbumArtist), SortAlbum: Changed(original.SortAlbum, edited.SortAlbum),
            TrackTotal: Changed(original.TrackTotal, edited.TrackTotal), DiscNumber: Changed(original.DiscNumber, edited.DiscNumber),
            DiscTotal: Changed(original.DiscTotal, edited.DiscTotal),
            BeatsPerMinute: original.BeatsPerMinute == edited.BeatsPerMinute ? null : (float)edited.BeatsPerMinute);
        return patch == new MetadataEdit() ? null : patch;
    }

    public static void ValidateFormat(MetadataEdit edit, string extension)
    {
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            MetadataEdit basic = new(Title: edit.Title, Artist: edit.Artist, Album: edit.Album,
                Genre: edit.Genre, Year: edit.Year, TrackNumber: edit.TrackNumber, Comment: edit.Comment);
            if (edit != basic)
                throw new NotSupportedException("WAV file edits support title, artist, album, genre, year, track number and comment only. Choose Library for other changes.");
        }
        if (!extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase) &&
            (edit.Grouping is not null || edit.SortTitle is not null || edit.SortArtist is not null ||
             edit.SortAlbumArtist is not null || edit.SortAlbum is not null))
            throw new NotSupportedException("Grouping and sort names can be written to M4A files only. Choose Library for these changes.");
    }

    public static TrackMetadata ReconcileRestored(TrackMetadata library, TrackMetadata restored, string extension)
    {
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return library with
            {
                Title = restored.Title,
                Artist = restored.Artist,
                Album = restored.Album,
                Genre = restored.Genre,
                Year = restored.Year,
                TrackNumber = restored.TrackNumber,
                Comment = restored.Comment
            };
        TrackMetadata result = restored with { Rating = library.Rating };
        return extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ? result : result with
        {
            Grouping = library.Grouping,
            SortTitle = library.SortTitle,
            SortArtist = library.SortArtist,
            SortAlbumArtist = library.SortAlbumArtist,
            SortAlbum = library.SortAlbum
        };
    }

    private static string? Changed(string before, string after) => before == after ? null : after;
    private static int? Changed(int before, int after) => before == after ? null : after;
}
