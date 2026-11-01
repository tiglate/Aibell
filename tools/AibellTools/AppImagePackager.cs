using System.Globalization;
using System.Text;

namespace AibellTools;

/// <summary>Builds a type-2 AppImage: the AppImage runtime followed by a squashfs of an AppDir made from the shared Linux layout.</summary>
internal static class AppImagePackager
{
    internal const string RuntimeSha256 = "2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d";
    private const int RuntimeSize = 944632;
    private const string RuntimeUrl = "https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64";

    private const string AppRun = """
        #!/bin/sh
        # Starts Aibell from inside the mounted AppImage.
        HERE="$(dirname "$(readlink -f "$0")")"
        export PATH="$HERE/usr/bin:$PATH"
        # .NET needs ICU unless globalization is invariant; fall back instead of failing to start on a host without it.
        if ! { ldconfig -p 2>/dev/null || /sbin/ldconfig -p 2>/dev/null; } | grep -q 'libicuuc\.so'; then
            export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
        fi
        exec "$HERE/usr/lib/aibell/Aibell.Desktop" "$@"
        """;

    public static int Run(string[] args)
    {
        string root = RepositoryRoot.Find();
        Options options = new(args, new HashSet<string> { "--download-runtime" }, new HashSet<string> { "--publish-dir", "--output", "--runtime" });
        string runtime = options.Value("--runtime") ?? Path.Combine(root, "artifacts", "tools", "appimage-runtime-x86_64");
        if (options.Value("--runtime") is null) EnsureRuntime(runtime, options.Has("--download-runtime"));
        (_, string revision, _) = RepositoryRoot.Run("git", ["rev-parse", "HEAD"], root);
        (_, string stamp, _) = RepositoryRoot.Run("git", ["log", "-1", "--format=%ct"], root);
        try
        {
            string path = Build(Path.GetFullPath(options.Value("--publish-dir") ?? Path.Combine(root, "artifacts", "deb-publish")),
                Path.GetFullPath(options.Value("--output") ?? Path.Combine(root, "artifacts/packages")), root, revision.Trim(),
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(stamp.Trim(), CultureInfo.InvariantCulture)), Path.GetFullPath(runtime),
                Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages"));
            Console.WriteLine(path);
            return 0;
        }
        catch (Exception exception) when (exception is PackagingException or IOException or KeyNotFoundException or System.Text.Json.JsonException or FormatException)
        {
            throw new ToolException(exception.Message, exception);
        }
    }

    /// <summary>Restores the pinned AppImage type-2 runtime (release 20251108) into the cache path, verifying its checksum before and after.</summary>
    internal static void EnsureRuntime(string path, bool download, Func<byte[]>? fetch = null)
    {
        if (File.Exists(path))
        {
            if (RepositoryRoot.Sha256(path) != RuntimeSha256) throw new ToolException("Cached AppImage runtime checksum mismatch; delete it and use --download-runtime.");
            return;
        }
        if (!download) throw new ToolException("AppImage runtime missing; use --download-runtime to fetch the pinned release, or --runtime <file>.");
        byte[] payload;
        if (fetch is not null) payload = fetch();
        else
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };
            payload = client.GetByteArrayAsync(RuntimeUrl).GetAwaiter().GetResult();
        }
        if (payload.Length != RuntimeSize || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)) != RuntimeSha256)
            throw new ToolException("Downloaded AppImage runtime size/checksum mismatch.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".download";
        File.WriteAllBytes(temporary, payload);
        File.Move(temporary, path, overwrite: false);
    }

    internal static string Build(string source, string output, string repository, string revision, DateTimeOffset stamp, string runtime, string packageCache)
    {
        if (OperatingSystem.IsWindows()) throw new PackagingException("AppImages are built on Linux.");
        Packager.Require(!RepositoryRoot.IsLink(source), "Publish input must not be a symbolic link.");
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        Packager.Require(output != source && !output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Package output must be outside the publish input.");
        Packager.Require(File.Exists(runtime) && !RepositoryRoot.IsLink(runtime), "--runtime must name an ordinary file.");
        byte[] header = new byte[11];
        using (FileStream stream = File.OpenRead(runtime)) _ = stream.Read(header);
        Packager.Require(header[0] == 0x7F && header[1] == 'E' && header[2] == 'L' && header[3] == 'F' && header[8] == 'A' && header[9] == 'I' && header[10] == 2,
            "--runtime is not an AppImage type-2 runtime (no ELF header with the AI\\x02 marker).");

        (SortedDictionary<string, LayoutEntry> payload, string appVersion) = LinuxLayout.Stage(source, repository, stamp, packageCache);
        string name = $"Aibell-{appVersion}-x86_64.AppImage";
        Directory.CreateDirectory(output);
        string image = Path.Combine(output, name);
        if (File.Exists(image)) throw new IOException("The AppImage already exists; choose a fresh output directory.");

        string work = Path.Combine(Path.GetTempPath(), "aibell-appimage-" + Guid.NewGuid().ToString("N"));
        try
        {
            string appDir = Path.Combine(work, "AppDir");
            Directory.CreateDirectory(appDir);
            foreach ((string path, LayoutEntry entry) in payload) Write(appDir, path, entry);
            Write(appDir, "AppRun", new(Encoding.UTF8.GetBytes(AppRun.ReplaceLineEndings("\n") + "\n"), true, null));
            Write(appDir, LinuxLayout.AppId + ".desktop", payload[$"usr/share/applications/{LinuxLayout.AppId}.desktop"] with { Executable = false });
            Write(appDir, LinuxLayout.AppId + ".png", payload[$"usr/share/icons/hicolor/256x256/apps/{LinuxLayout.AppId}.png"]);
            Write(appDir, ".DirIcon", payload[$"usr/share/icons/hicolor/256x256/apps/{LinuxLayout.AppId}.png"]);

            string squashfs = Path.Combine(work, "image.squashfs");
            string epoch = stamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            (int code, _, string error) = RepositoryRoot.Run("mksquashfs", [appDir, squashfs, "-root-owned", "-noappend", "-quiet", "-no-progress",
                "-comp", "zstd", "-Xcompression-level", "19", "-b", "256K", "-all-time", epoch, "-mkfs-time", epoch], timeout: TimeSpan.FromMinutes(10));
            Packager.Require(code == 0, "mksquashfs failed (install squashfs-tools 4.5 or newer): " + error.Trim());

            string partial = image + ".partial";
            using (FileStream target = File.Create(partial))
            {
                using (FileStream head = File.OpenRead(runtime)) head.CopyTo(target);
                using FileStream body = File.OpenRead(squashfs);
                body.CopyTo(target);
            }
            File.SetUnixFileMode(partial, (UnixFileMode)0x1ED);
            File.Move(partial, image, overwrite: false);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
            File.Delete(image + ".partial");
        }
        File.WriteAllText(image + ".sha256", $"{RepositoryRoot.Sha256(image)}  {name}\n", Encoding.ASCII);
        return image;
    }

    private static void Write(string root, string relative, LayoutEntry entry)
    {
        if (OperatingSystem.IsWindows()) throw new PackagingException("AppImages are built on Linux.");
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (entry.LinkTarget is not null)
        {
            File.CreateSymbolicLink(path, entry.LinkTarget);
            return;
        }
        File.WriteAllBytes(path, entry.Content!);
        File.SetUnixFileMode(path, (UnixFileMode)(entry.Executable ? 0x1ED : 0x1A4));
    }
}
