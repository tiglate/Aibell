using Aibell.Application.Plugins;

namespace Aibell.Metadata.MusicBrainz;

/// <summary>Plugin entry point. The application discovers it in <c>plugins/Aibell.Metadata.MusicBrainz/</c>; it is never referenced directly.</summary>
public sealed class MusicBrainzPlugin : IAibellPlugin
{
    public int ApiVersion => PluginApi.Version;

    public string Id => "musicbrainz";

    public string DisplayName => "MusicBrainz";

    public string Description =>
        "Finds album details, track names, genres, cover art and basic artist facts on MusicBrainz and the Cover Art Archive. " +
        "Sends a disc's table of contents or an album or artist name only when you press Look up or Get Artwork.";

    public PluginContributions Start(PluginContext context)
    {
        HttpClient http = new();
        // One limiter for every MusicBrainz request this plugin makes, whichever provider issues it.
        TokenBucketRateLimiter limiter = new(TimeSpan.FromSeconds(1));
        MusicBrainzClient client = new(http, limiter,
            new Uri("https://musicbrainz.org/ws/2/"), new Uri("https://coverartarchive.org/"));
        MusicBrainzLookupProvider lookup = new(client);
        return new PluginContributions
        {
            EnrichmentProviders = [new MusicBrainzEnrichmentProvider(http, limiter)],
            ReleaseProviders = [lookup],
            ArtistProviders = [lookup]
        };
    }
}
