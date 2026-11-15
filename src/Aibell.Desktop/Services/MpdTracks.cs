using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aibell.Domain.Media;
using Aibell.Mpd.Client;

namespace Aibell.Desktop.Services;

/// <summary>
/// How an MPD song travels through Aibell's own queue and player: as a <see cref="Track"/> whose location is
/// <c>mpd:///path/relative/to/the/music/directory</c>. The player recognises that scheme and plays it on the server instead.
/// </summary>
public static class MpdTracks
{
    public const string Scheme = "mpd";

    /// <summary>The location of a song, escaping each part of its path.</summary>
    public static Uri Location(string songUri)
    {
        ArgumentNullException.ThrowIfNull(songUri);
        return new Uri($"{Scheme}:///{string.Join('/', songUri.Split('/').Select(Uri.EscapeDataString))}");
    }

    /// <summary>True for a track location made by <see cref="Location"/>; <paramref name="songUri"/> is then the path MPD knows.</summary>
    public static bool TryGetSongUri(Uri? location, out string songUri)
    {
        songUri = string.Empty;
        if (location is null || !location.IsAbsoluteUri || !location.Scheme.Equals(Scheme, StringComparison.Ordinal)) return false;
        songUri = Uri.UnescapeDataString(location.AbsolutePath.TrimStart('/'));
        return songUri.Length > 0;
    }

    /// <summary>The track for a song. The id comes from the path, so the same song is always the same track.</summary>
    public static Track FromSong(MpdSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(song.Uri));
        return new Track(
            new Guid(hash.AsSpan(0, 16)),
            Location(song.Uri),
            song.DisplayTitle,
            song.Artist ?? string.Empty,
            song.Album ?? string.Empty,
            song.Duration ?? TimeSpan.Zero,
            song.Tag("Genre") ?? string.Empty,
            song.AlbumArtist ?? string.Empty,
            LeadingNumber(song.Tag("Track")),
            LeadingNumber(song.Tag("Date")),
            DiscNumber: LeadingNumber(song.Tag("Disc")),
            Source: "MPD");
    }

    /// <summary>The number at the start of a tag such as <c>3/12</c> or <c>1999-05-01</c>; 0 when there is none.</summary>
    private static int LeadingNumber(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        return end > 0 && int.TryParse(text.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }
}
