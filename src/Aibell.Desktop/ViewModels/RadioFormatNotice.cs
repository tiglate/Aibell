using Aibell.Domain.Radio;

namespace Aibell.Desktop.ViewModels;

/// <summary>
/// The warning shown on a radio card whose audio the active playback engine cannot play. It exists only for the built-in player: with VLC
/// selected nothing is shown, and on Windows HE-AAC streams (not HLS, which never goes through Media Foundation) do play, so they are not flagged.
/// </summary>
public sealed record RadioFormatNotice(string Format, bool WindowsAlternative)
{
    public static RadioFormatNotice? For(string? unsupportedFormat, Uri stream, bool vlcInUse, bool windows)
    {
        if (string.IsNullOrWhiteSpace(unsupportedFormat) || vlcInUse) return null;
        bool windowsPlaysIt = unsupportedFormat == "HE-AAC" && !RadioStation.IsHlsStream(stream);
        if (windows && windowsPlaysIt) return null;
        return new RadioFormatNotice(unsupportedFormat, windowsPlaysIt);
    }
}
