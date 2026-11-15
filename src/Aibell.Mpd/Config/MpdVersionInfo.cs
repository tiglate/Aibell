using System.Diagnostics;

namespace Aibell.Mpd.Config;

/// <summary>What the installed <c>mpd</c> program was built with, read from <c>mpd --version</c>, so the editor offers only plugins that exist.</summary>
public sealed class MpdVersionInfo
{
    private readonly Dictionary<string, HashSet<string>> sections;

    private MpdVersionInfo(string version, Dictionary<string, HashSet<string>> sections)
    {
        Version = version;
        this.sections = sections;
    }

    /// <summary>The version, for example <c>0.23.14</c>; empty when unknown.</summary>
    public string Version { get; }

    /// <summary>The block that lists each kind of plugin in <c>mpd --version</c>.</summary>
    private static readonly Dictionary<string, string> BlockSections = new(StringComparer.Ordinal)
    {
        ["audio_output"] = "Output plugins",
        ["decoder"] = "Decoders plugins",
        ["database"] = "Database plugins",
        ["neighbors"] = "Neighbor plugins",
        ["input"] = "Input plugins",
        ["archive_plugin"] = "Archive plugins",
        ["playlist_plugin"] = "Playlist plugins",
        ["filter"] = "Filters",
        ["resampler"] = "Filters",
    };

    public static MpdVersionInfo Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Dictionary<string, HashSet<string>> sections = new(StringComparer.Ordinal);
        string version = string.Empty;
        HashSet<string>? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (version.Length == 0 && line.StartsWith("Music Player Daemon ", StringComparison.Ordinal))
            {
                version = line["Music Player Daemon ".Length..].Split(' ')[0];
                continue;
            }
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.EndsWith(':'))
            {
                current = [];
                sections[line[..^1]] = current;
                continue;
            }
            if (current is null || line.Length == 0 || !char.IsWhiteSpace(line[0])) continue;
            foreach (string token in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                string name = token.Trim('[', ']');
                if (name.Length > 0 && token.StartsWith('[') && token.EndsWith(']')) current.Add(name);
                else if (!token.Contains("://", StringComparison.Ordinal) && !token.Contains('.', StringComparison.Ordinal) && !line.TrimStart().StartsWith('[')) current.Add(name);
            }
        }
        return new MpdVersionInfo(version, sections);
    }

    /// <summary>Runs <c>mpd --version</c>. Returns <c>null</c> when the program is missing or does not answer in time.</summary>
    public static async Task<MpdVersionInfo?> DiscoverAsync(string program = "mpd", CancellationToken cancellationToken = default)
    {
        try
        {
            using Process process = new() { StartInfo = new ProcessStartInfo(program, "--version") { RedirectStandardOutput = true, RedirectStandardError = true } };
            process.Start();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return Parse(output);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the plugin is built in, or when this program does not list that kind of plugin at all (so nothing can be ruled out).
    /// </summary>
    public bool Supports(string block, string plugin)
    {
        if (block == "resampler" && plugin == "internal") return true;
        if (!BlockSections.TryGetValue(block, out string? section) || !sections.TryGetValue(section, out HashSet<string>? names) || names.Count == 0) return true;
        return names.Contains(plugin);
    }
}
