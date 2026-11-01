using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aibell.Infrastructure.Plugins;

public static partial class PluginNames
{
    /// <summary>A plugin folder name is a single safe path segment, because it is also used to build file system paths.</summary>
    public static bool IsValid(string? name) =>
        name is not null && Pattern().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex Pattern();
}

/// <summary>
/// Remembers which plugins the user switched off and which user-installed plugins are waiting to be deleted. Plugin assemblies stay
/// loaded (and on Windows locked) while the application runs, so a removal is carried out at the next start before anything loads.
/// A missing or damaged file means "nothing disabled", never a startup failure.
/// </summary>
public sealed class PluginStateStore
{
    private readonly string path;
    private readonly object gate = new();
    private readonly HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pendingRemoval = new(StringComparer.OrdinalIgnoreCase);

    public PluginStateStore(string path)
    {
        this.path = path ?? throw new ArgumentNullException(nameof(path));
        try
        {
            if (!File.Exists(path)) return;
            State? state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            foreach (string name in state?.Disabled ?? []) if (PluginNames.IsValid(name)) disabled.Add(name);
            foreach (string name in state?.PendingRemoval ?? []) if (PluginNames.IsValid(name)) pendingRemoval.Add(name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            disabled.Clear();
            pendingRemoval.Clear();
        }
    }

    public IReadOnlySet<string> Disabled { get { lock (gate) return new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase); } }

    public IReadOnlySet<string> PendingRemoval { get { lock (gate) return new HashSet<string>(pendingRemoval, StringComparer.OrdinalIgnoreCase); } }

    public void SetEnabled(string folder, bool enabled)
    {
        Require(folder);
        lock (gate)
        {
            if (enabled ? !disabled.Remove(folder) : !disabled.Add(folder)) return;
            Save();
        }
    }

    public void MarkForRemoval(string folder)
    {
        Require(folder);
        lock (gate)
        {
            if (pendingRemoval.Add(folder)) Save();
        }
    }

    /// <summary>Deletes the folders waiting for removal under <paramref name="userRoot"/>. Call before loading plugins. A folder that cannot be deleted stays pending.</summary>
    public void ApplyPendingRemovals(string userRoot)
    {
        lock (gate)
        {
            bool changed = false;
            foreach (string name in pendingRemoval.ToArray())
            {
                string folder = Path.Combine(userRoot, name);
                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                    pendingRemoval.Remove(name);
                    disabled.Remove(name);
                    changed = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Try again next start. */ }
            }
            if (changed) Save();
        }
    }

    private static void Require(string folder)
    {
        if (!PluginNames.IsValid(folder)) throw new ArgumentException("Not a valid plugin folder name.", nameof(folder));
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new State([.. disabled.Order(StringComparer.Ordinal)], [.. pendingRemoval.Order(StringComparer.Ordinal)])));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The plugin settings could not be saved: " + exception.Message, exception);
        }
    }

    private sealed record State(List<string>? Disabled, List<string>? PendingRemoval);
}
