using System.Globalization;
using System.Text;

namespace AibellTools;

/// <summary>Stages the shared Linux layout under <c>/app</c> and builds a Flatpak with flatpak-builder from a generated manifest.</summary>
internal static class FlatpakPackager
{
    private const string DefaultRuntime = "24.08";

    public static int Run(string[] args)
    {
        string root = RepositoryRoot.Find();
        Options options = new(args, new HashSet<string> { "--stage-only" }, new HashSet<string> { "--publish-dir", "--output", "--runtime-version" });
        (_, string stamp, _) = RepositoryRoot.Run("git", ["log", "-1", "--format=%ct"], root);
        try
        {
            string path = Build(Path.GetFullPath(options.Value("--publish-dir") ?? Path.Combine(root, "artifacts", "deb-publish")),
                Path.GetFullPath(options.Value("--output") ?? Path.Combine(root, "artifacts/packages")), root,
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(stamp.Trim(), CultureInfo.InvariantCulture)), options.Value("--runtime-version") ?? DefaultRuntime, options.Has("--stage-only"),
                Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages"));
            Console.WriteLine(path);
            return 0;
        }
        catch (Exception exception) when (exception is PackagingException or IOException or KeyNotFoundException or System.Text.Json.JsonException or FormatException)
        {
            throw new ToolException(exception.Message, exception);
        }
    }

    internal static string Manifest(string runtimeVersion) => $"""
        app-id: {LinuxLayout.AppId}
        runtime: org.freedesktop.Platform
        runtime-version: '{runtimeVersion}'
        sdk: org.freedesktop.Sdk
        command: aibell
        finish-args:
          # Avalonia draws through X11 (XWayland on Wayland desktops).
          - --share=ipc
          - --socket=x11
          - --device=dri
          - --socket=pulseaudio
          # Podcasts, internet radio and the optional MusicBrainz lookup.
          - --share=network
          # The music library, and USB drives for Devices sync.
          - --filesystem=xdg-music
          - --filesystem=/media
          - --filesystem=/run/media
          # MPRIS media keys and track-change notifications. The app owns org.mpris.MediaPlayer2.aibell.instance<PID>,
          # so the wildcard is needed: a plain name only matches itself.
          - --own-name=org.mpris.MediaPlayer2.aibell.*
          - --talk-name=org.freedesktop.Notifications
        modules:
          - name: aibell
            buildsystem: simple
            build-commands:
              - cp -a . /app/
            sources:
              - type: dir
                path: stage

        """;

    /// <summary>Returns the exported bundle, or with <paramref name="stageOnly"/> the folder holding the manifest and stage tree.</summary>
    internal static string Build(string source, string output, string repository, DateTimeOffset stamp, string runtimeVersion, bool stageOnly, string packageCache)
    {
        if (OperatingSystem.IsWindows()) throw new PackagingException("Flatpaks are built on Linux.");
        Packager.Require(!RepositoryRoot.IsLink(source), "Publish input must not be a symbolic link.");
        Packager.Require(System.Text.RegularExpressions.Regex.IsMatch(runtimeVersion, @"^[0-9]{2}\.[0-9]{2}$"), "--runtime-version looks like 24.08.");
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        Packager.Require(output != source && !output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Package output must be outside the publish input.");
        (SortedDictionary<string, LayoutEntry> payload, string appVersion) = LinuxLayout.Stage(source, repository, stamp, packageCache);

        string work = Path.Combine(output, "flatpak-work");
        string bundle = Path.Combine(output, $"Aibell-{appVersion}-x86_64.flatpak");
        if (!stageOnly && File.Exists(bundle)) throw new IOException("The Flatpak already exists; choose a fresh output directory.");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        string stage = Path.Combine(work, "stage");
        Directory.CreateDirectory(stage);
        foreach ((string path, LayoutEntry entry) in payload)
        {
            string target = Path.Combine(stage, path["usr/".Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (entry.LinkTarget is not null) File.CreateSymbolicLink(target, entry.LinkTarget);
            else
            {
                File.WriteAllBytes(target, entry.Content!);
                File.SetUnixFileMode(target, (UnixFileMode)(entry.Executable ? 0x1ED : 0x1A4));
            }
        }
        string manifest = Path.Combine(work, LinuxLayout.AppId + ".yml");
        File.WriteAllText(manifest, Manifest(runtimeVersion).ReplaceLineEndings("\n"), new UTF8Encoding(false));
        if (stageOnly) return work;

        string repo = Path.Combine(work, "repo");
        int code;
        string log, error;
        try
        {
            (code, log, error) = RepositoryRoot.Run("flatpak-builder", ["--user", "--force-clean", "--disable-cache", "--disable-rofiles-fuse", "--repo=" + repo, Path.Combine(work, "build"), manifest],
                work, timeout: TimeSpan.FromMinutes(20));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new PackagingException("flatpak-builder is not installed (sudo apt install flatpak-builder); use --stage-only to write only the manifest.");
        }
        Packager.Require(code == 0, "flatpak-builder failed (it needs org.freedesktop.Platform and Sdk " + runtimeVersion + " installed): " + (error + log).Trim());
        (code, log, error) = RepositoryRoot.Run("flatpak", ["build-bundle", repo, bundle, LinuxLayout.AppId], work);
        Packager.Require(code == 0, "flatpak build-bundle failed: " + (error + log).Trim());
        File.WriteAllText(bundle + ".sha256", $"{RepositoryRoot.Sha256(bundle)}  {Path.GetFileName(bundle)}\n", Encoding.ASCII);
        return bundle;
    }
}
