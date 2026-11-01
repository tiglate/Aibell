using System.Text;
using Aibell.Domain.Media;

namespace Aibell.Application.Devices;

/// <summary>
/// Chooses where a track goes on a device: <c>Music/Artist/Album/NN - Title.ext</c>. Names are made safe for FAT, exFAT, NTFS and
/// ext4 alike, because the device's file system is not known in advance: forbidden characters become <c>_</c>, trailing dots and
/// spaces are removed, Windows device names are escaped and every segment is limited in length.
/// </summary>
public static class DevicePathBuilder
{
    public const int MaximumSegmentLength = 80;
    public const int MaximumRelativeLength = 200;
    private const string UnknownArtist = "Unknown Artist";
    private const string UnknownAlbum = "Unknown Album";
    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>The relative path with forward slashes, for example <c>Music/Black Sabbath/Heaven and Hell/01 - Neon Knights.mp3</c>.</summary>
    public static string Relative(Track track, string musicFolder, string extension)
    {
        ArgumentNullException.ThrowIfNull(track);
        string artist = Segment(FirstNonBlank(track.AlbumArtist, track.Artist), UnknownArtist);
        string album = Segment(track.Album, UnknownAlbum);
        string number = track.TrackNumber > 0 ? (track.DiscNumber > 1 ? $"{track.DiscNumber}-{track.TrackNumber:00}" : $"{track.TrackNumber:00}") + " - " : "";
        string ext = SafeExtension(extension);
        string title = Segment(track.Title, Path.GetFileNameWithoutExtension(track.Location.IsFile ? track.Location.LocalPath : "track"));
        int room = Math.Max(8, MaximumRelativeLength - (Segment(musicFolder, "Music").Length + artist.Length + album.Length + 3) - ext.Length - number.Length);
        if (title.Length > room) title = title[..room].TrimEnd(' ', '.');
        return $"{Segment(musicFolder, "Music")}/{artist}/{album}/{number}{title}{ext}";
    }

    /// <summary>Turns free text into one safe path segment (never empty, never a reserved name).</summary>
    public static string Segment(string? text, string fallback)
    {
        string source = string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
        StringBuilder builder = new(source.Length);
        foreach (char character in source)
            builder.Append(char.IsControl(character) || "<>:\"/\\|?*".Contains(character, StringComparison.Ordinal) ? '_' : character);
        string cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length > MaximumSegmentLength) cleaned = cleaned[..MaximumSegmentLength].TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(character => character is '_' or '.')) cleaned = Segment(fallback, "Unknown");
        string stem = cleaned.Split('.')[0];
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) cleaned = "_" + cleaned;
        return cleaned;
    }

    private static string SafeExtension(string extension)
    {
        string trimmed = extension.Trim().TrimStart('.').ToLowerInvariant();
        return trimmed.Length is 0 or > 8 || !trimmed.All(char.IsAsciiLetterOrDigit) ? "" : "." + trimmed;
    }

    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
