using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AibellTools;

/// <summary>One file, directory-less symlink or executable in a Linux package tree. A null <see cref="Content"/> with a link target is a symbolic link.</summary>
internal readonly record struct LayoutEntry(byte[]? Content, bool Executable, string? LinkTarget);

/// <summary>The file tree every Linux package (deb, AppImage, Flatpak, Snap) installs, rooted at <c>usr/</c>, built from one optimized publish folder.</summary>
internal static class LinuxLayout
{
    internal const string AppId = "io.github.tiglate.Aibell";
    internal const string InstallRoot = "usr/lib/aibell";

    /// <summary>Runtime files that only serve crash dumps, debuggers, LTTng tracing and alternative garbage collectors.</summary>
    private static readonly HashSet<string> Unneeded = new(StringComparer.Ordinal)
    {
        "createdump", "libmscordaccore.so", "libmscordbi.so", "libcoreclrtraceptprovider.so", "libclrgc.so", "libclrgcexp.so"
    };

    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256, 512];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static (SortedDictionary<string, LayoutEntry> Payload, string AppVersion) Stage(string source, string repository, DateTimeOffset stamp, string packageCache)
    {
        List<string> files = Packager.PayloadFiles(source, "linux-x64");
        using JsonDocument deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "Aibell.Desktop.deps.json")));
        string appVersion = deps.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(item => item.Name.StartsWith("Aibell.Desktop/", StringComparison.Ordinal)).Select(item => item.Name["Aibell.Desktop/".Length..].Split('+')[0]).SingleOrDefault()
            ?? throw new PackagingException("Cannot determine the compiled application version.");
        string[] extras = [.. files.Select(path => Path.GetRelativePath(source, path).Replace('\\', '/')).Where(name => name.EndsWith(".pdb", StringComparison.Ordinal))];
        Packager.Require(extras.Length == 0, "Publish with DebugType=none; debug symbols do not belong in the package: " + string.Join(", ", extras.Take(3)));

        SortedDictionary<string, LayoutEntry> payload = new(StringComparer.Ordinal);
        foreach (string path in files)
        {
            string relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            if (Unneeded.Contains(relative)) continue;
            payload[$"{InstallRoot}/{relative}"] = new(File.ReadAllBytes(path), relative == "Aibell.Desktop" && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0, null);
        }
        payload["usr/bin/aibell"] = new(null, false, "../lib/aibell/Aibell.Desktop");

        string packaging = Path.Combine(repository, "packaging", "linux");
        string icons = Path.Combine(repository, "data", "icons");
        string date = stamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        payload[$"usr/share/applications/{AppId}.desktop"] = new(File.ReadAllBytes(Path.Combine(packaging, AppId + ".desktop")), false, null);
        string metainfo = File.ReadAllText(Path.Combine(packaging, AppId + ".metainfo.xml")).Replace("@VERSION@", appVersion, StringComparison.Ordinal).Replace("@DATE@", date, StringComparison.Ordinal);
        payload[$"usr/share/metainfo/{AppId}.metainfo.xml"] = new(Encoding.UTF8.GetBytes(metainfo), false, null);
        foreach (int size in IconSizes)
            payload[$"usr/share/icons/hicolor/{size}x{size}/apps/{AppId}.png"] = new(File.ReadAllBytes(Path.Combine(icons, $"aibell-{size}.png")), false, null);
        payload[$"usr/share/icons/hicolor/scalable/apps/{AppId}.svg"] = new(File.ReadAllBytes(Path.Combine(icons, "aibell.svg")), false, null);

        payload["usr/share/doc/aibell/LICENSE"] = new(File.ReadAllBytes(Path.Combine(repository, "LICENSE")), false, null);
        string opusNotice = Path.Combine(repository, "src", "Aibell.Audio.Opus", "COPYING.Opus");
        if (File.Exists(opusNotice)) payload["usr/share/doc/aibell/COPYING.Opus"] = new(File.ReadAllBytes(opusNotice), false, null);
        string rarNotice = Path.Combine(repository, "src", "Aibell.Archive.Rar", "COPYING.Rar");
        if (File.Exists(rarNotice)) payload["usr/share/doc/aibell/COPYING.Rar"] = new(File.ReadAllBytes(rarNotice), false, null);
        string alacDirectory = Path.Combine(repository, "src", "Aibell.Audio.Alac");
        if (File.Exists(Path.Combine(alacDirectory, "LICENSE.Alac"))) payload["usr/share/doc/aibell/LICENSE.Alac"] = new(File.ReadAllBytes(Path.Combine(alacDirectory, "LICENSE.Alac")), false, null);
        if (File.Exists(Path.Combine(alacDirectory, "NOTICE"))) payload["usr/share/doc/aibell/NOTICE.Alac"] = new(File.ReadAllBytes(Path.Combine(alacDirectory, "NOTICE")), false, null);

        string notices = Path.Combine(Path.GetTempPath(), "aibell-notices-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(notices);
            List<Dictionary<string, object?>> inventory = Packager.DependencyNotices(deps.RootElement.GetProperty("libraries"), packageCache, notices);
            foreach (string path in Directory.EnumerateFiles(notices, "*", SearchOption.AllDirectories))
                payload["usr/share/doc/aibell/" + Path.GetRelativePath(notices, path).Replace('\\', '/')] = new(File.ReadAllBytes(path), false, null);
            payload["usr/share/doc/aibell/dependency-inventory.json"] = new(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(inventory, Indented) + "\n"), false, null);
        }
        finally
        {
            Directory.Delete(notices, recursive: true);
        }
        return (payload, appVersion);
    }
}
