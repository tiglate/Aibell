using System.Security.Cryptography;
using System.Text;
using Aibell.Domain.Media;

namespace Aibell.Application.Discs;

/// <summary>Builds the transient tracks shown for a disc. They are never written to the library.</summary>
public static class DiscTracks
{
    public static IReadOnlyList<Track> Build(OpticalDrive drive, DiscToc toc, string album = "Audio CD", string artist = "", string genre = "", int year = 0,
        IReadOnlyList<string>? titles = null, IReadOnlyList<string>? artists = null)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(toc);
        List<Track> tracks = new(toc.TrackCount);
        for (int index = 0; index < toc.TrackCount; index++)
        {
            int number = toc.FirstTrackNumber + index;
            Uri location = DiscLocation.Create(drive.Id, number);
            string title = titles is not null && index < titles.Count && !string.IsNullOrWhiteSpace(titles[index]) ? titles[index] : $"Track {number}";
            string trackArtist = artists is not null && index < artists.Count && !string.IsNullOrWhiteSpace(artists[index]) ? artists[index] : artist;
            tracks.Add(new Track(StableId(location), location, title, trackArtist, album, toc.DurationOf(index), genre, artist,
                TrackNumber: number, Year: year, Source: "Audio CD"));
        }
        return tracks;
    }

    private static Guid StableId(Uri location) => new(SHA256.HashData(Encoding.UTF8.GetBytes(location.AbsoluteUri)).AsSpan(0, 16));
}
