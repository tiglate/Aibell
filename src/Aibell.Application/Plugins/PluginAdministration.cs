namespace Aibell.Application.Plugins;

/// <summary>
/// One plugin folder as the plugin manager shows it. <paramref name="IsRunning"/> describes this session; <paramref name="EnabledNext"/>
/// and <paramref name="RemovalPending"/> describe what the next start will do, because plugins are never loaded or unloaded while
/// the application runs.
/// </summary>
public sealed record PluginEntry(
    string Folder,
    string Name,
    string Description,
    string Version,
    bool IsUserInstalled,
    bool IsRunning,
    string? Failure,
    bool EnabledNext,
    bool RemovalPending);

/// <summary>Lists, switches, installs and removes plugins. Every change takes effect at the next start.</summary>
public interface IPluginAdministration
{
    /// <summary>The per-user folder that installed plugins are copied to.</summary>
    string UserPluginsDirectory { get; }

    IReadOnlyList<PluginEntry> List();

    void SetEnabled(string folder, bool enabled);

    /// <summary>Installs a plugin folder (<c>Name/Name.dll</c>) or a <c>.zip</c> that contains exactly one such folder. Returns its folder name.</summary>
    Task<string> InstallAsync(string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>Schedules removal of a user-installed plugin at the next start. Plugins shipped with the application cannot be removed.</summary>
    void Uninstall(string folder);
}
