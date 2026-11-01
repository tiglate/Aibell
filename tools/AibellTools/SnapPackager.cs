using System.Globalization;
using System.Text;

namespace AibellTools;

/// <summary>Stages the shared Linux layout as a snapcraft project (core24, gnome extension) and optionally builds it with <c>snapcraft pack</c>.</summary>
internal static class SnapPackager
{
    public static int Run(string[] args)
    {
        string root = RepositoryRoot.Find();
        Options options = new(args, new HashSet<string> { "--stage-only" }, new HashSet<string> { "--publish-dir", "--output" });
        (_, string stamp, _) = RepositoryRoot.Run("git", ["log", "-1", "--format=%ct"], root);
        try
        {
            string path = Build(Path.GetFullPath(options.Value("--publish-dir") ?? Path.Combine(root, "artifacts", "deb-publish")),
                Path.GetFullPath(options.Value("--output") ?? Path.Combine(root, "artifacts/packages")), root,
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(stamp.Trim(), CultureInfo.InvariantCulture)), !options.Has("--stage-only"),
                Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages"));
            Console.WriteLine(path);
            return 0;
        }
        catch (Exception exception) when (exception is PackagingException or IOException or KeyNotFoundException or System.Text.Json.JsonException or FormatException)
        {
            throw new ToolException(exception.Message, exception);
        }
    }

    internal static string SnapcraftYaml(string version) => $"""
        name: aibell
        title: Aibell
        base: core24
        version: '{version}'
        summary: Music library and player
        description: |
          Aibell is a desktop music library and player built with .NET and Avalonia. It has a dense library browser,
          playlists and smart playlists, podcasts, audiobooks, internet radio, a ten-band equalizer and tag and artwork editing.
          The built-in player plays MP3, FLAC, Ogg Vorbis and WAV; LibVLC is not part of this snap.
        license: MIT
        website: https://github.com/tiglate/Aibell
        issues: https://github.com/tiglate/Aibell/issues
        source-code: https://github.com/tiglate/Aibell
        contact: https://github.com/tiglate/Aibell/issues
        icon: snap/gui/icon.png
        grade: stable
        confinement: strict
        lint:
          ignore:
            # Avalonia loads these with dlopen, which the linter cannot see.
            - library:
                - libSM.so.6
                - libXcursor.so.1
                - libXi.so.6
                - libXrandr.so.2
        platforms:
          amd64:
        apps:
          aibell:
            command: usr/bin/aibell
            extensions: [gnome]
            plugs: [audio-playback, network, home, removable-media]
            environment:
              # AppArmor denies the flock .NET takes on every opened file (document-portal paths, /proc/self).
              DOTNET_SYSTEM_IO_DISABLEFILELOCKING: "1"
            slots: [mpris]
        slots:
          # MPRIS media keys. Publishing to the Snap Store needs a manual review for this slot.
          mpris:
            interface: mpris
            name: aibell
        parts:
          aibell:
            plugin: dump
            source: stage
            # What the Debian package declares as Depends; the .NET runtime itself is inside the publish.
            stage-packages:
              - libicu74
              - libx11-6
              - libxext6
              - libxi6
              - libxrandr2
              - libxcursor1
              - libice6
              - libsm6
              - libfontconfig1

        """;

    /// <summary>Returns the snap file when built, otherwise the staged snapcraft project folder.</summary>
    internal static string Build(string source, string output, string repository, DateTimeOffset stamp, bool build, string packageCache)
    {
        if (OperatingSystem.IsWindows()) throw new PackagingException("Snaps are built on Linux.");
        Packager.Require(!RepositoryRoot.IsLink(source), "Publish input must not be a symbolic link.");
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        Packager.Require(output != source && !output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Package output must be outside the publish input.");
        (SortedDictionary<string, LayoutEntry> payload, string appVersion) = LinuxLayout.Stage(source, repository, stamp, packageCache);

        string work = Path.Combine(output, "snap-work");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        string stage = Path.Combine(work, "stage");
        foreach ((string path, LayoutEntry entry) in payload)
        {
            string target = Path.Combine(stage, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (entry.LinkTarget is not null) File.CreateSymbolicLink(target, entry.LinkTarget);
            else
            {
                File.WriteAllBytes(target, entry.Content!);
                File.SetUnixFileMode(target, (UnixFileMode)(entry.Executable ? 0x1ED : 0x1A4));
            }
        }
        string gui = Path.Combine(work, "snap", "gui");
        Directory.CreateDirectory(gui);
        File.WriteAllBytes(Path.Combine(gui, "icon.png"), payload[$"usr/share/icons/hicolor/256x256/apps/{LinuxLayout.AppId}.png"].Content!);
        string desktop = Encoding.UTF8.GetString(payload[$"usr/share/applications/{LinuxLayout.AppId}.desktop"].Content!)
            .Replace("Icon=" + LinuxLayout.AppId, "Icon=${SNAP}/meta/gui/icon.png", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(gui, "aibell.desktop"), desktop, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(work, "snap", "snapcraft.yaml"), SnapcraftYaml(appVersion).ReplaceLineEndings("\n"), new UTF8Encoding(false));

        // Offline schema check of the project, including the gnome extension, when snapcraft is installed.
        bool installed = true;
        try
        {
            (int check, string checkLog, string checkError) = RepositoryRoot.Run("snapcraft", ["expand-extensions"], work, timeout: TimeSpan.FromMinutes(2));
            if (check != 0) throw new PackagingException("snapcraft rejected the generated project: " + (checkError + checkLog).Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            installed = false;
        }
        if (!build) return work;
        Packager.Require(installed, "snapcraft is not installed (sudo snap install snapcraft --classic); use --stage-only to write only the project.");

        (int code, string log, string error) = RepositoryRoot.Run("snapcraft", ["pack"], work, timeout: TimeSpan.FromMinutes(60));
        Packager.Require(code == 0, "snapcraft pack failed (core24 builds need LXD or a matching Ubuntu 24.04 host with --destructive-mode): " + (error + log).Trim());
        // snapcraft only writes inside the project tree, so the snap is moved out afterwards.
        string built = Directory.EnumerateFiles(work, "aibell_*.snap").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? throw new PackagingException("snapcraft finished but no aibell_*.snap was written.");
        string snap = Path.Combine(output, Path.GetFileName(built));
        if (File.Exists(snap)) throw new IOException("The snap already exists; choose a fresh output directory.");
        File.Move(built, snap);
        File.WriteAllText(snap + ".sha256", $"{RepositoryRoot.Sha256(snap)}  {Path.GetFileName(snap)}\n", Encoding.ASCII);
        return snap;
    }
}
