using System.Reflection;
using System.Runtime.Loader;
using Aibell.Application.Plugins;

namespace Aibell.Infrastructure.Plugins;

public sealed record LoadedPlugin(string Id, string DisplayName, PluginContributions Contributions, string Folder = "");

public sealed record PluginLoadFailure(string Name, string Reason, bool IsUserInstalled = false);

/// <summary>A plugin folder whose entry point was found and constructed. <see cref="Started"/> is false when the user switched it off.</summary>
public sealed record PluginFound(string Folder, string Id, string Name, string Description, string Version, bool IsUserInstalled, bool Started);

/// <summary>A folder that holds plugins: the one shipped with the application, or the per-user folder that Install copies into.</summary>
public sealed record PluginSource(string? Root, bool IsUserInstalled);

public sealed record PluginLoadReport(IReadOnlyList<LoadedPlugin> Plugins, IReadOnlyList<PluginLoadFailure> Failures, IReadOnlyList<PluginFound>? Found = null)
{
    public static PluginLoadReport Empty { get; } = new([], [], []);

    public IReadOnlyList<PluginFound> Discovered { get; } = Found ?? [];
}

/// <summary>
/// Loads plugins from <c>&lt;root&gt;/&lt;name&gt;/&lt;name&gt;.dll</c>. Each plugin gets its own load context, but any assembly the
/// application already has (the contracts, the framework) resolves from the default context so the host and the plugin share one
/// <see cref="IAibellPlugin"/> type. A missing folder means no plugins; a broken, incompatible or throwing plugin is reported and
/// skipped without affecting the others or the application. A plugin the user switched off is constructed so it can be described,
/// but never started. When two roots hold the same folder name the earlier root wins.
/// </summary>
public static class PluginLoader
{
    public static PluginLoadReport LoadAll(string? root, PluginContext context) =>
        LoadAll([new PluginSource(root, IsUserInstalled: false)], context, new HashSet<string>());

    public static PluginLoadReport LoadAll(IReadOnlyList<PluginSource> sources, PluginContext context, IReadOnlySet<string> disabled)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(disabled);

        List<LoadedPlugin> plugins = [];
        List<PluginLoadFailure> failures = [];
        List<PluginFound> found = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (PluginSource source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Root) || !Directory.Exists(source.Root)) continue;
            foreach (string folder in Directory.EnumerateDirectories(source.Root).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(folder);
                // Folders starting with a dot are install staging areas, not plugins.
                if (name.StartsWith('.')) continue;
                string assemblyPath = Path.Combine(folder, name + ".dll");
                if (!File.Exists(assemblyPath)) continue;
                if (!seen.Add(name))
                {
                    failures.Add(new PluginLoadFailure(name, "Another plugin with this folder name is already installed.", source.IsUserInstalled));
                    continue;
                }
                try
                {
                    (IAibellPlugin plugin, string version) = Instantiate(assemblyPath);
                    bool start = !disabled.Contains(name);
                    if (start) plugins.Add(Start(plugin, context, name));
                    found.Add(new PluginFound(name, plugin.Id, plugin.DisplayName, plugin.Description ?? "", version, source.IsUserInstalled, start));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    failures.Add(new PluginLoadFailure(name,
                        exception is TargetInvocationException { InnerException: { } inner } ? inner.Message : exception.Message, source.IsUserInstalled));
                }
            }
        }
        return new PluginLoadReport(plugins, failures, found);
    }

    private static (IAibellPlugin Plugin, string Version) Instantiate(string assemblyPath)
    {
        PluginLoadContext loadContext = new(assemblyPath);
        Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        Type[] candidates = [.. assembly.GetExportedTypes().Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IAibellPlugin).IsAssignableFrom(type))];
        if (candidates.Length != 1) throw new InvalidOperationException($"Expected exactly one plugin entry point, found {candidates.Length}.");
        IAibellPlugin plugin = (IAibellPlugin)(Activator.CreateInstance(candidates[0]) ?? throw new InvalidOperationException("The plugin could not be created."));
        if (plugin.ApiVersion != PluginApi.Version)
            throw new InvalidOperationException($"The plugin targets plugin API {plugin.ApiVersion}; this application provides {PluginApi.Version}.");
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "";
        int build = version.IndexOf('+', StringComparison.Ordinal);
        return (plugin, build > 0 ? version[..build] : version);
    }

    private static LoadedPlugin Start(IAibellPlugin plugin, PluginContext context, string folder)
    {
        PluginContributions contributions = plugin.Start(context) ?? throw new InvalidOperationException("The plugin returned no contributions.");
        return new LoadedPlugin(plugin.Id, plugin.DisplayName, contributions, folder);
    }

    private sealed class PluginLoadContext(string assemblyPath) : AssemblyLoadContext("plugin:" + Path.GetFileNameWithoutExtension(assemblyPath))
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException)
            {
                string? path = resolver.ResolveAssemblyToPath(assemblyName);
                return path is null ? null : LoadFromAssemblyPath(path);
            }
        }
    }
}
