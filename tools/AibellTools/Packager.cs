using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AibellTools;

/// <summary>Creates auditable candidate archives (not releases) from a self-contained publish folder.</summary>
internal static partial class Packager
{
    internal static readonly string[] Rids = ["linux-x64", "win-x64"];
    private static readonly string[] LibVlcLibraries = ["libvlc.dll", "libvlccore.dll"];
    private static readonly string[] NoticeWords = ["license", "notice", "copying"];
    private static readonly string[] NoticePrefixes = ["copying", "license", "notice", "authors"];
    private static readonly string[] JsonFiles = ["Aibell.Desktop.deps.json", "Aibell.Desktop.runtimeconfig.json"];
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(string[] args)
    {
        string root = RepositoryRoot.Find();
        Options options = new(args, new HashSet<string>(), new HashSet<string> { "--rid", "--publish-dir", "--output", "--bundle-libvlc" });
        string rid = options.Value("--rid") ?? throw new ToolException("--rid is required.");
        (_, string revision, _) = RepositoryRoot.Run("git", ["rev-parse", "HEAD"], root);
        (_, string status, _) = RepositoryRoot.Run("git", ["status", "--porcelain", "--untracked-files=normal"], root);
        try
        {
            (string archive, string checksum) = Package(Path.GetFullPath(options.Value("--publish-dir") ?? Path.Combine(root, "artifacts", rid)),
                Path.GetFullPath(options.Value("--output") ?? Path.Combine(root, "artifacts/packages")), rid, revision.Trim(), status.Trim().Length > 0,
                Path.Combine(root, "LICENSE"), null, options.Value("--bundle-libvlc") is { } vlc ? Path.GetFullPath(vlc) : null);
            Console.WriteLine(archive);
            Console.WriteLine(checksum);
            return 0;
        }
        catch (Exception exception) when (exception is PackagingException or IOException or KeyNotFoundException or JsonException or InvalidDataException or System.Xml.XmlException)
        {
            throw new ToolException(exception.Message, exception);
        }
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new PackagingException(message);
    }

    private static bool Executable(string path) => !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    private static bool Regular(string path) => File.Exists(path) && !RepositoryRoot.IsLink(path) && !new FileInfo(path).Attributes.HasFlag(FileAttributes.Device);

    internal static List<string> PayloadFiles(string source, string rid)
    {
        Require(Directory.Exists(source) && !RepositoryRoot.IsLink(source), "Publish input must be an ordinary directory.");
        bool windows = rid.StartsWith("win-", StringComparison.Ordinal);
        HashSet<string> hosts = windows ? ["Aibell.Desktop.exe", "createdump.exe"] : ["Aibell.Desktop", "createdump"];
        List<string> files = [];
        foreach (string path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            Require(!RepositoryRoot.IsLink(path) && !relative.Split('/').Any(part => part.StartsWith('.')), $"Hidden files and symbolic links are not publish payload: {relative}");
            if (Directory.Exists(path)) continue;
            Require(Regular(path), $"Only regular publish files are allowed: {relative}");
            string extension = Path.GetExtension(path).ToLowerInvariant();
            bool allowed = extension is ".dll" or ".so" or ".pdb" || JsonFiles.Contains(relative) || hosts.Contains(relative);
            Require(allowed, $"Unexpected publish file (profiles, media and runtime downloads do not belong here): {relative}");
            files.Add(path);
        }
        HashSet<string> present = files.Select(path => Path.GetRelativePath(source, path).Replace('\\', '/')).ToHashSet();
        string[] required = windows ? ["Aibell.Desktop.dll", .. JsonFiles, "Aibell.Desktop.exe", "coreclr.dll"] : ["Aibell.Desktop.dll", .. JsonFiles, "Aibell.Desktop", "libcoreclr.so"];
        string[] missing = [.. required.Where(name => !present.Contains(name))];
        Require(missing.Length == 0, "Incomplete self-contained publish: " + string.Join(", ", missing.Order(StringComparer.Ordinal)));
        string host = Path.Combine(source, windows ? "Aibell.Desktop.exe" : "Aibell.Desktop");
        byte[] magic = new byte[4];
        using (FileStream stream = File.OpenRead(host)) _ = stream.Read(magic);
        Require(windows ? magic[0] == 'M' && magic[1] == 'Z' : magic[0] == 0x7F && magic[1] == 'E' && magic[2] == 'L' && magic[3] == 'F', "The application host does not match the requested platform.");
        Require(windows || OperatingSystem.IsWindows() || (File.GetUnixFileMode(host) & UnixFileMode.UserExecute) != 0, "The Linux application host must be executable.");
        return files;
    }

    internal static (List<(string Source, string Relative)> Payload, List<string> Excluded, SortedDictionary<string, string> Licenses) LibVlcBundlePayload(string path)
    {
        const string Shape = "--bundle-libvlc must name a directory with libvlc.dll, libvlccore.dll and a plugins subfolder.";
        Require(Directory.Exists(path) && !RepositoryRoot.IsLink(path), Shape);
        string plugins = Path.Combine(path, "plugins");
        Require(File.Exists(Path.Combine(path, "libvlc.dll")) || RepositoryRoot.IsLink(Path.Combine(path, "libvlc.dll")), Shape);
        Require(File.Exists(Path.Combine(path, "libvlccore.dll")) || RepositoryRoot.IsLink(Path.Combine(path, "libvlccore.dll")), Shape);
        Require(Directory.Exists(plugins) && !RepositoryRoot.IsLink(plugins), Shape);
        List<(string, string)> payload = [];
        foreach (string name in LibVlcLibraries)
        {
            Require(Regular(Path.Combine(path, name)), "LibVLC libraries must be ordinary files, not links.");
            payload.Add((Path.Combine(path, name), name));
        }
        List<string> excluded = [];
        SortedDictionary<string, string> licenses = new(StringComparer.Ordinal);
        foreach (string entry in Directory.EnumerateFileSystemEntries(plugins, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(path, entry).Replace('\\', '/');
            Require(!RepositoryRoot.IsLink(entry) && !relative.Split('/').Any(part => part.StartsWith('.')), "Hidden files and links are not LibVLC plugin payload.");
            if (Directory.Exists(entry)) continue;
            Require(Regular(entry), "Only regular LibVLC plugin files are allowed.");
            if (Path.GetFileName(entry).Equals("plugins.dat", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetExtension(entry).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                string? declared = VlcLicenses.PluginLicense(entry);
                VlcLicenses.Kind kind = VlcLicenses.Classify(declared);
                Require(kind != VlcLicenses.Kind.Unknown, $"Cannot read the declared license of {relative}; only plugins that declare LGPL-2.1 or later may be bundled.");
                if (kind == VlcLicenses.Kind.Gpl)
                {
                    excluded.Add(relative);
                    continue;
                }
                licenses[relative] = declared!;
            }
            payload.Add((entry, relative));
        }
        return (payload, excluded, licenses);
    }

    private static List<string> LibVlcNotices(string path)
    {
        List<string> notices = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(entry).ToLowerInvariant();
            if (!NoticePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))) continue;
            Require(Regular(entry) && new FileInfo(entry).Length <= 4 * 1024 * 1024, "Supplied LibVLC notices must be regular files no larger than 4 MiB.");
            notices.Add(entry);
        }
        return notices;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^[A-Za-z0-9_.+-]+$")]
    private static partial Regex SafeVersion();

    internal static List<Dictionary<string, object?>> DependencyNotices(JsonElement libraries, string cache, string stage)
    {
        List<Dictionary<string, object?>> inventory = [];
        foreach (JsonProperty library in libraries.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            int split = library.Name.LastIndexOf('/');
            string name = library.Name[..split], version = library.Name[(split + 1)..];
            string? type = library.Value.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
            Dictionary<string, object?> item = new()
            {
                ["name"] = name,
                ["version"] = version,
                ["type"] = type,
                ["sha512"] = library.Value.TryGetProperty("sha512", out JsonElement hash) ? hash.GetString() : null
            };
            inventory.Add(item);
            if (type is not ("package" or "runtimepack")) continue;
            string packageName = name.StartsWith("runtimepack.", StringComparison.Ordinal) ? name["runtimepack.".Length..] : name;
            Require(SafeName().IsMatch(packageName) && SafeVersion().IsMatch(version), "Unsafe package identity in dependency graph.");
            string archivePath = Path.Combine(cache, packageName.ToLowerInvariant(), version.ToLowerInvariant(), $"{packageName.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg");
            Require(File.Exists(archivePath), $"Restore the package before collecting its notice metadata: {library.Name}");
            string destination = Path.Combine(stage, "dependency-notices", packageName, version);
            Directory.CreateDirectory(destination);
            using ZipArchive package = ZipFile.OpenRead(archivePath);
            List<ZipArchiveEntry> specs = [.. package.Entries.Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal))];
            Require(specs.Count == 1 && specs[0].Length <= 1024 * 1024, $"Invalid package metadata: {library.Name}");
            byte[] metadata = ReadEntry(specs[0]);
            File.WriteAllBytes(Path.Combine(destination, "package.nuspec"), metadata);
            XElement spec = XDocument.Parse(Encoding.UTF8.GetString(metadata).TrimStart('﻿')).Root!;
            XElement? license = spec.Descendants().FirstOrDefault(element => element.Name.LocalName == "license");
            string? licenseType = (string?)license?.Attribute("type");
            item["license"] = license is null ? null : new Dictionary<string, string?> { ["type"] = licenseType, ["value"] = license.Value };
            item["licenseUrl"] = spec.Descendants().FirstOrDefault(element => element.Name.LocalName == "licenseUrl")?.Value;
            item["authors"] = spec.Descendants().FirstOrDefault(element => element.Name.LocalName == "authors")?.Value;
            if (licenseType == "file") Require(package.Entries.Any(entry => entry.FullName == license!.Value), $"Declared license file is missing from package: {library.Name}");
            List<string> notices = [];
            foreach (ZipArchiveEntry entry in package.Entries)
            {
                bool declared = licenseType == "file" && entry.FullName == license!.Value;
                string extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
                if (entry.FullName.EndsWith('/') || (!declared && extension is not ("" or ".txt" or ".md" or ".html"))) continue;
                string fileName = Path.GetFileName(entry.FullName).ToLowerInvariant();
                if (!declared && !NoticeWords.Any(fileName.Contains)) continue;
                Require(!Path.IsPathRooted(entry.FullName) && !entry.FullName.Contains(':') && !entry.FullName.Split('/').Contains("..") && !entry.FullName.Contains('\\') && entry.Length <= 4 * 1024 * 1024,
                    $"Invalid notice path or size in package: {library.Name}");
                string target = Path.Combine(destination, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, ReadEntry(entry));
                notices.Add(Path.GetRelativePath(stage, target).Replace('\\', '/'));
            }
            item["noticeFiles"] = notices;
        }
        return inventory;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex FullSha();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?$")]
    private static partial Regex SafeAppVersion();

    internal static (string Archive, string Checksum) Package(string source, string output, string rid, string revision, bool dirty, string licensePath,
        string? packageCache, string? bundleLibVlc)
    {
        Require(Rids.Contains(rid) && FullSha().IsMatch(revision), "Use a supported RID and a full lowercase Git commit SHA.");
        bool windows = rid.StartsWith("win-", StringComparison.Ordinal);
        List<(string Source, string Relative)> bundleFiles = [];
        List<string> bundleNotices = [], excludedPlugins = [];
        SortedDictionary<string, string> pluginLicenses = new(StringComparer.Ordinal);
        if (bundleLibVlc is not null)
        {
            Require(windows, "--bundle-libvlc is only supported for win-x64; Linux keeps native LibVLC as a system dependency.");
            (bundleFiles, excludedPlugins, pluginLicenses) = LibVlcBundlePayload(bundleLibVlc);
            bundleNotices = LibVlcNotices(bundleLibVlc);
            bundleLibVlc = Path.GetFullPath(bundleLibVlc);
        }
        Require(!RepositoryRoot.IsLink(source), "Publish input must not be a symbolic link.");
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        Require(output != source && !output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Archive output must be outside the publish input.");
        List<string> files = PayloadFiles(source, rid);
        using JsonDocument depsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "Aibell.Desktop.deps.json")));
        JsonElement deps = depsDocument.RootElement;
        string runtimeTarget = deps.GetProperty("runtimeTarget").GetProperty("name").GetString() ?? "";
        Require(runtimeTarget.EndsWith("/" + rid, StringComparison.Ordinal), "Publish dependency graph does not match the requested RID.");
        using JsonDocument runtimeDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "Aibell.Desktop.runtimeconfig.json")));
        bool selfContained = runtimeDocument.RootElement.GetProperty("runtimeOptions").TryGetProperty("includedFrameworks", out JsonElement included)
            && included.EnumerateArray().Any(framework => framework.TryGetProperty("name", out JsonElement n) && n.GetString() == "Microsoft.NETCore.App");
        Require(selfContained, "Publish must include the self-contained .NET runtime.");
        string[] versions = [.. deps.GetProperty("libraries").EnumerateObject().Where(item => item.Name.StartsWith("Aibell.Desktop/", StringComparison.Ordinal)).Select(item => item.Name["Aibell.Desktop/".Length..])];
        Require(versions.Length == 1 && SafeAppVersion().IsMatch(versions[0]), "Cannot determine a safe compiled application version.");
        string version = versions[0];
        string candidate = $"aibell-{version.Split('+')[0]}-dev.{revision[..12]}" + (dirty ? ".dirty" : "") + $"-{rid}";
        string archiveName = candidate + (windows ? ".zip" : ".tar.gz");
        Directory.CreateDirectory(output);
        string archive = Path.Combine(output, archiveName), checksum = Path.Combine(output, archiveName + ".sha256");
        if (File.Exists(archive) || File.Exists(checksum)) throw new IOException("Candidate output already exists; choose a fresh output directory.");
        List<string> created = [];
        string temporary = Path.Combine(output, ".aibell-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string stage = Path.Combine(temporary, candidate);
            Directory.CreateDirectory(stage);
            foreach (string path in files)
            {
                string destination = Path.Combine(stage, Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(path, destination);
                SetMode(destination, Executable(path));
            }
            if (bundleLibVlc is not null)
            {
                string hostDirectory = Path.GetDirectoryName(Path.Combine(stage, files.Select(path => Path.GetRelativePath(source, path)).First(name => Path.GetFileName(name) == "Aibell.Desktop.exe")))!;
                foreach ((string itemSource, string relative) in bundleFiles)
                {
                    string destination = Path.Combine(hostDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(itemSource, destination);
                    SetMode(destination, false);
                }
            }
            File.Copy(licensePath, Path.Combine(stage, "LICENSE"));
            string opusNotice = Path.Combine(Path.GetDirectoryName(licensePath)!, "src", "Aibell.Audio.Opus", "COPYING.Opus");
            if (File.Exists(opusNotice)) File.Copy(opusNotice, Path.Combine(stage, "COPYING.Opus"));
            string rarNotice = Path.Combine(Path.GetDirectoryName(licensePath)!, "src", "Aibell.Archive.Rar", "COPYING.Rar");
            if (File.Exists(rarNotice)) File.Copy(rarNotice, Path.Combine(stage, "COPYING.Rar"));
            string alacDirectory = Path.Combine(Path.GetDirectoryName(licensePath)!, "src", "Aibell.Audio.Alac");
            if (File.Exists(Path.Combine(alacDirectory, "LICENSE.Alac"))) File.Copy(Path.Combine(alacDirectory, "LICENSE.Alac"), Path.Combine(stage, "LICENSE.Alac"));
            if (File.Exists(Path.Combine(alacDirectory, "NOTICE"))) File.Copy(Path.Combine(alacDirectory, "NOTICE"), Path.Combine(stage, "NOTICE.Alac"));
            string libVlcText = bundleLibVlc is not null
                ? "The .NET runtime is included. Native LibVLC is bundled for Windows from\n" +
                  $"a locally supplied VLC installation at {bundleLibVlc} (its VLC version\n" +
                  "is not verified by this packaging tool). Only plugins that declare LGPL-2.1 or later are\n" +
                  $"bundled ({pluginLicenses.Count}); {excludedPlugins.Count} GPL plugins were left out. See dependency-notices/LibVLC/.\n" +
                  "Linux: install compatible system LibVLC 3 and its codec/output plugins.\n"
                : "The .NET runtime is included. Native LibVLC is NOT bundled.\n" +
                  "Linux: install compatible system LibVLC 3 and its codec/output plugins.\n" +
                  "Windows: install VLC 3, or set AIBELL_LIBVLC_PATH to an absolute folder\n" +
                  "containing libvlc.dll and libvlccore.dll with its plugins.\n";
            File.WriteAllText(Path.Combine(stage, "CANDIDATE.txt"),
                "Aibell candidate build — not a signed release or installer.\n" +
                $"Revision: {revision}\nCompiled application version: {version}\nRID: {rid}\n" +
                $"Source worktree dirty: {(dirty ? "True" : "False")}\n\n" + libVlcText +
                "Run Aibell.Desktop (Linux) or Aibell.Desktop.exe (Windows).\n" +
                "To isolate evaluation, set AIBELL_DATA_DIRECTORY to an absolute temporary\n" +
                "directory. No user profile or media is included in this archive.\n\n" +
                "Signing, installers, native runtime distribution, full third-party notice\n" +
                "review and release acceptance remain open gates. LICENSE\n" +
                "covers the project. dependency-notices/ retains available NuGet license/notice\n" +
                "files and metadata. dependency-inventory.json is not a license clearance.\n");
            string cache = packageCache ?? Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages");
            List<Dictionary<string, object?>> inventory = DependencyNotices(deps.GetProperty("libraries"), cache, stage);
            File.WriteAllText(Path.Combine(stage, "dependency-inventory.json"), JsonSerializer.Serialize(inventory, Indented) + "\n");
            if (bundleLibVlc is not null) WriteLibVlcNotices(stage, bundleLibVlc, bundleNotices, pluginLicenses, excludedPlugins);
            var entries = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => new
            {
                path = Path.GetRelativePath(stage, path).Replace('\\', '/'),
                bytes = new FileInfo(path).Length,
                sha256 = RepositoryRoot.Sha256(path),
                executable = Executable(path)
            }).ToList();
            var manifest = new
            {
                schemaVersion = 1,
                candidate,
                applicationVersion = version,
                revision,
                sourceDirty = dirty,
                rid,
                runtimeTarget,
                selfContained = true,
                libVlcBundled = bundleLibVlc is not null,
                files = entries
            };
            File.WriteAllText(Path.Combine(stage, "candidate-manifest.json"), JsonSerializer.Serialize(manifest, Indented) + "\n");
            string stagedArchive = Path.Combine(temporary, archiveName);
            if (windows) WriteZip(stagedArchive, stage, candidate);
            else WriteTarGz(stagedArchive, stage, candidate);
            string stagedChecksum = Path.Combine(temporary, archiveName + ".sha256");
            File.WriteAllText(stagedChecksum, $"{RepositoryRoot.Sha256(stagedArchive)}  {archiveName}\n", Encoding.ASCII);
            try
            {
                // Same-filesystem moves publish complete files without replacing an existing candidate.
                foreach ((string staged, string final) in new[] { (stagedArchive, archive), (stagedChecksum, checksum) })
                {
                    File.Move(staged, final, overwrite: false);
                    created.Add(final);
                }
            }
            catch
            {
                foreach (string path in created) File.Delete(path);
                throw;
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
        return (archive, checksum);
    }

    private static void SetMode(string path, bool executable)
    {
        if (OperatingSystem.IsWindows()) return;
        UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        if (executable) mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, mode);
    }

    private static void WriteLibVlcNotices(string stage, string bundle, List<string> supplied, SortedDictionary<string, string> licenses, List<string> excluded)
    {
        string directory = Path.Combine(stage, "dependency-notices", "LibVLC");
        Directory.CreateDirectory(directory);
        foreach (string notice in supplied)
        {
            string target = Path.Combine(directory, "supplied", Path.GetFileName(notice));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(notice, target);
        }
        File.WriteAllText(Path.Combine(directory, "NOTICE.txt"),
            "VideoLAN documents an LGPL libVLC core (libvlc.dll, libvlccore.dll). VLC plugins can use different licenses, including GPL.\n" +
            "https://videolan.videolan.me/vlc/master/libvlc.html\n\n" +
            "This tool read the license each plugin DLL declares about itself (its vlc_entry_license export) and bundled only plugins that\n" +
            $"declare LGPL-2.1 or later: {licenses.Count} plugins bundled, {excluded.Count} GPL plugins left out (listed in\n" +
            "excluded-gpl-plugins.txt). A plugin whose declared license cannot be read stops the build. bundled-plugin-licenses.txt lists\n" +
            "every bundled plugin with its declared license. plugins.dat (VLC's generated cache) is not bundled; VLC rebuilds it.\n\n" +
            "Available installation COPYING/LICENSE/NOTICE/AUTHORS files are retained under supplied/. The core libraries' LGPL status\n" +
            "comes from VideoLAN's documentation; this tool does not machine-verify it. The license text of the LGPL must accompany the\n" +
            "binaries and users must be able to replace the LibVLC libraries (they are dynamically loaded).\n\n" +
            "This candidate bundles a copy from a locally supplied VLC installation at\n" +
            $"{bundle}. Its reported VLC version is not verified by this packaging tool.\n");
        File.WriteAllText(Path.Combine(directory, "excluded-gpl-plugins.txt"), string.Concat(excluded.Select(item => item + "\n")));
        File.WriteAllText(Path.Combine(directory, "bundled-plugin-licenses.txt"), string.Concat(licenses.Select(item => $"{item.Key}\t{item.Value}\n")));
    }

    private static void WriteZip(string archive, string stage, string candidate)
    {
        using FileStream file = File.Create(archive);
        using ZipArchive zip = new(file, ZipArchiveMode.Create);
        foreach (string path in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            ZipArchiveEntry entry = zip.CreateEntry(candidate + "/" + Path.GetRelativePath(stage, path).Replace('\\', '/'), CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = (0x8000 | (Executable(path) ? 0x1ED : 0x1A4)) << 16; // regular file, 0755 or 0644
            using Stream target = entry.Open();
            using FileStream source = File.OpenRead(path);
            source.CopyTo(target);
        }
    }

    private static void WriteTarGz(string archive, string stage, string candidate)
    {
        using FileStream file = File.Create(archive);
        using GZipStream gzip = new(file, CompressionLevel.Optimal);
        using TarWriter tar = new(gzip, TarEntryFormat.Ustar, leaveOpen: false);
        foreach (string path in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            UstarTarEntry entry = new(TarEntryType.RegularFile, candidate + "/" + Path.GetRelativePath(stage, path).Replace('\\', '/'))
            {
                ModificationTime = DateTimeOffset.UnixEpoch,
                Uid = 0,
                Gid = 0,
                UserName = "",
                GroupName = "",
                Mode = (UnixFileMode)(Executable(path) ? 0x1ED : 0x1A4)
            };
            entry.DataStream = File.OpenRead(path);
            tar.WriteEntry(entry);
            entry.DataStream.Dispose();
        }
    }
}

/// <summary>An input that must not be packaged (the equivalent of a rejected publish folder or LibVLC bundle).</summary>
internal sealed class PackagingException(string message) : Exception(message);
