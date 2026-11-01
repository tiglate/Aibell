using System.Diagnostics;
using Aibell.Application.Plugins;

namespace Aibell.Infrastructure.Plugins;

/// <summary>
/// The plugin manager's backend. It reads what the loader found at startup, then layers the user's pending choices on top:
/// enabled or not, installed this session, scheduled for removal.
/// </summary>
public sealed class PluginAdministration(
    string bundledRoot,
    string userRoot,
    PluginStateStore state,
    PluginLoadReport report) : IPluginAdministration
{
    public string UserPluginsDirectory => userRoot;

    public IReadOnlyList<PluginEntry> List()
    {
        IReadOnlySet<string> disabled = state.Disabled;
        IReadOnlySet<string> removing = state.PendingRemoval;
        Dictionary<string, PluginEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        foreach (PluginFound found in report.Discovered)
            entries[found.Folder] = new(found.Folder, found.Name, found.Description, found.Version, found.IsUserInstalled, found.Started, null,
                !disabled.Contains(found.Folder), removing.Contains(found.Folder));
        foreach (PluginLoadFailure failure in report.Failures)
            entries[failure.Name] = new(failure.Name, failure.Name, "", "", failure.IsUserInstalled, false, failure.Reason,
                !disabled.Contains(failure.Name), removing.Contains(failure.Name));
        // Installed after startup: present on disk, not yet loaded.
        if (Directory.Exists(userRoot))
            foreach (string folder in Directory.EnumerateDirectories(userRoot).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(folder);
                string assembly = Path.Combine(folder, name + ".dll");
                if (name.StartsWith('.') || entries.ContainsKey(name) || !File.Exists(assembly)) continue;
                entries[name] = new(name, name, "", FileVersionOf(assembly), true, false, null, !disabled.Contains(name), removing.Contains(name));
            }
        return [.. entries.Values.OrderBy(entry => entry.IsUserInstalled).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public void SetEnabled(string folder, bool enabled) => state.SetEnabled(folder, enabled);

    public Task<string> InstallAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(userRoot);
        return Task.Run(() => PluginInstaller.Install(sourcePath, userRoot, [bundledRoot], state.PendingRemoval, cancellationToken), cancellationToken);
    }

    public void Uninstall(string folder)
    {
        if (!PluginNames.IsValid(folder)) throw new ArgumentException("Not a valid plugin folder name.", nameof(folder));
        if (!Directory.Exists(Path.Combine(userRoot, folder)) || Directory.Exists(Path.Combine(bundledRoot, folder)))
            throw new InvalidOperationException("Only plugins you installed can be removed. Plugins shipped with the application can be switched off.");
        state.MarkForRemoval(folder);
    }

    private static string FileVersionOf(string assembly)
    {
        try
        {
            string? version = FileVersionInfo.GetVersionInfo(assembly).ProductVersion;
            int build = version?.IndexOf('+', StringComparison.Ordinal) ?? -1;
            return version is null ? "" : build > 0 ? version[..build] : version;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return ""; }
    }
}
