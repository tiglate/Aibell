using Aibell.Application.Enrichment;

namespace Aibell.Application.Plugins;

/// <summary>What the host tells a plugin when it starts it.</summary>
public sealed record PluginContext(string DataDirectory);

/// <summary>Everything a plugin adds to the application. Unused lists stay empty.</summary>
public sealed record PluginContributions
{
    public IReadOnlyList<IEnrichmentProvider> EnrichmentProviders { get; init; } = [];

    public IReadOnlyList<IReleaseLookupProvider> ReleaseProviders { get; init; } = [];

    public IReadOnlyList<IArtistInfoProvider> ArtistProviders { get; init; } = [];
}

/// <summary>
/// Entry point of a plugin assembly. A plugin lives in <c>plugins/&lt;name&gt;/&lt;name&gt;.dll</c> next to the application, has a
/// public parameterless constructor and keeps its own network access, credentials and caching. The core never references a plugin.
/// </summary>
public interface IAibellPlugin
{
    /// <summary>Must equal <see cref="PluginApi.Version"/>; a plugin built against another version is rejected, not loaded.</summary>
    int ApiVersion { get; }

    string Id { get; }

    string DisplayName { get; }

    /// <summary>One or two sentences for the plugin manager: what the plugin adds and what it sends over the network.</summary>
    string Description => "";

    PluginContributions Start(PluginContext context);
}

public static class PluginApi
{
    public const int Version = 1;
}
