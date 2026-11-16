using Aibell.Application.Plugins;

namespace Aibell.Lyrics.Lrclib;

/// <summary>Plugin entry point. The application discovers it in <c>plugins/Aibell.Lyrics.Lrclib/</c>; it is never referenced directly.</summary>
public sealed class LrclibPlugin : IAibellPlugin
{
    public int ApiVersion => PluginApi.Version;

    public string Id => "lrclib";

    public string DisplayName => "LRCLIB lyrics";

    public string Description =>
        "Finds plain and time-synced lyrics on lrclib.net. Sends the title, artist, album and length of the track being played " +
        "to lrclib.net when lyrics are not already stored; turn lyrics off in Preferences to stop that.";

    public PluginContributions Start(PluginContext context) =>
        new() { LyricsProviders = [new LrclibLyricsProvider(new HttpClient { Timeout = TimeSpan.FromSeconds(15) })] };
}
