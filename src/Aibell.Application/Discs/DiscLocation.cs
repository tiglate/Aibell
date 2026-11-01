using System.Globalization;
using System.Text.RegularExpressions;

namespace Aibell.Application.Discs;

/// <summary>The <c>cdda:</c> locations used for tracks that live on a disc rather than in the library.</summary>
public static partial class DiscLocation
{
    public const string Scheme = "cdda";

    public static Uri Create(string driveId, int trackNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveId);
        ArgumentOutOfRangeException.ThrowIfLessThan(trackNumber, 1);
        string path = DriveLetter().IsMatch(driveId) ? "/" + driveId.TrimEnd('\\', '/') : driveId;
        string escaped = string.Join('/', path.Split('/').Select(segment => DriveLetterSegment().IsMatch(segment) ? segment : Uri.EscapeDataString(segment)));
        return new Uri($"{Scheme}://{escaped}?track={trackNumber.ToString(CultureInfo.InvariantCulture)}");
    }

    public static bool TryParse(Uri? location, out string driveId, out int trackNumber)
    {
        driveId = "";
        trackNumber = 0;
        if (location is not { IsAbsoluteUri: true } || !string.Equals(location.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        string path = Uri.UnescapeDataString(location.AbsolutePath);
        if (path.Length < 2) return false;
        Match query = TrackQuery().Match(location.Query);
        if (!query.Success || !int.TryParse(query.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out trackNumber) || trackNumber < 1) return false;
        driveId = DriveLetterPath().IsMatch(path) ? path[1..] : path;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/]?$")]
    private static partial Regex DriveLetter();

    [GeneratedRegex(@"^[A-Za-z]:$")]
    private static partial Regex DriveLetterSegment();

    [GeneratedRegex(@"^/[A-Za-z]:$")]
    private static partial Regex DriveLetterPath();

    [GeneratedRegex(@"^\?track=(\d{1,2})$")]
    private static partial Regex TrackQuery();
}
