namespace Aibell.Desktop.Services;

/// <summary>
/// Decides whether the main window keeps its title bar. A tiling compositor or bare window manager (niri, sway, Hyprland, i3...)
/// draws no title bars of its own, so Avalonia draws a light one inside the window that ignores the dark theme, and that bar
/// is of no use in such a session. Full desktop environments keep the title bar. <c>AIBELL_TITLE_BAR</c> (<c>on</c> or <c>off</c>)
/// overrides the detection.
/// </summary>
public static class TitleBarPolicy
{
    private static readonly string[] TilingDesktops =
    [
        "niri", "sway", "scroll", "hyprland", "river", "i3", "bspwm", "awesome", "qtile", "dwl", "dwm", "herbstluftwm", "xmonad",
        "spectrwm", "leftwm", "miracle", "mangowc", "wayfire-tiling"
    ];

    private static readonly string[] DesktopNames = ["XDG_CURRENT_DESKTOP", "XDG_SESSION_DESKTOP", "DESKTOP_SESSION"];
    private static readonly char[] Separators = [':', ';'];

    private static readonly string[] TilingMarkers = ["NIRI_SOCKET", "SWAYSOCK", "HYPRLAND_INSTANCE_SIGNATURE", "I3SOCK"];

    public static bool ShouldHideTitleBar(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!OperatingSystem.IsLinux()) return false;
        switch (environment("AIBELL_TITLE_BAR")?.Trim().ToLowerInvariant())
        {
            case "off" or "0" or "false" or "no": return true;
            case "on" or "1" or "true" or "yes": return false;
        }
        if (TilingMarkers.Any(name => !string.IsNullOrEmpty(environment(name)))) return true;
        return DesktopNames
            .SelectMany(name => (environment(name) ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(desktop => TilingDesktops.Contains(desktop, StringComparer.OrdinalIgnoreCase));
    }
}
