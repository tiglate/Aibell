using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AibellTools;

/// <summary>Builds a Debian/Ubuntu <c>.deb</c> from a self-contained linux-x64 publish folder, without needing dpkg-deb.</summary>
internal static partial class DebPackager
{
    // .NET and Avalonia need these at run time; X11 libraries are loaded with dlopen, so no ELF scan would find them.
    private const string Depends =
        "libc6 (>= 2.35), libgcc-s1, libstdc++6, zlib1g, libssl3t64 | libssl3, libicu74 | libicu76 | libicu72 | libicu70, " +
        "libfontconfig1, libx11-6, libxext6, libxi6, libxrandr2, libxcursor1, libice6, libsm6";

    private const string Recommends = "libpulse0 | libasound2t64 | libasound2, libgl1, libvlc5, vlc-plugin-base";

    public static int Run(string[] args)
    {
        string root = RepositoryRoot.Find();
        Options options = new(args, new HashSet<string>(), new HashSet<string> { "--publish-dir", "--output", "--deb-version" });
        (_, string revision, _) = RepositoryRoot.Run("git", ["rev-parse", "HEAD"], root);
        (_, string stamp, _) = RepositoryRoot.Run("git", ["log", "-1", "--format=%ct"], root);
        try
        {
            string path = Build(Path.GetFullPath(options.Value("--publish-dir") ?? Path.Combine(root, "artifacts", "deb-publish")),
                Path.GetFullPath(options.Value("--output") ?? Path.Combine(root, "artifacts/packages")), root, revision.Trim(),
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(stamp.Trim(), CultureInfo.InvariantCulture)), options.Value("--deb-version"),
                Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages"));
            Console.WriteLine(path);
            return 0;
        }
        catch (Exception exception) when (exception is PackagingException or IOException or KeyNotFoundException or JsonException or FormatException)
        {
            throw new ToolException(exception.Message, exception);
        }
    }

    [GeneratedRegex(@"^[0-9][A-Za-z0-9.+~-]*$")]
    private static partial Regex SafeDebVersion();

    internal static string Build(string source, string output, string repository, string revision, DateTimeOffset stamp, string? debVersion, string packageCache)
    {
        Packager.Require(!RepositoryRoot.IsLink(source), "Publish input must not be a symbolic link.");
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        Packager.Require(output != source && !output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Package output must be outside the publish input.");
        (SortedDictionary<string, LayoutEntry> payload, string appVersion) = LinuxLayout.Stage(source, repository, stamp, packageCache);
        string version = debVersion ?? appVersion.Replace('-', '~') + "-1";
        Packager.Require(SafeDebVersion().IsMatch(version), $"Not a valid Debian version: {version}");

        payload.Remove("usr/share/doc/aibell/LICENSE"); // the license text is inside the copyright file
        payload["usr/share/doc/aibell/copyright"] = new(Encoding.UTF8.GetBytes(Copyright(File.ReadAllText(Path.Combine(repository, "LICENSE")))), false, null);
        payload["usr/share/doc/aibell/changelog.Debian.gz"] = new(Gzip(Encoding.UTF8.GetBytes(Changelog(version, revision, stamp))), false, null);
        long installedKib = (payload.Values.Sum(item => (long)(item.Content?.Length ?? 0)) + 1023) / 1024;
        StringBuilder md5sums = new();
        foreach ((string path, (byte[]? content, _, _)) in payload)
#pragma warning disable CA5351 // The Debian md5sums control file format is defined in terms of MD5; it is an integrity listing, not a security control.
            if (content is not null) md5sums.Append(Convert.ToHexStringLower(MD5.HashData(content))).Append("  ").Append(path).Append('\n');
#pragma warning restore CA5351
        string control =
            "Package: aibell\n" +
            $"Version: {version}\n" +
            "Architecture: amd64\n" +
            "Maintainer: Tiglate Pileser III <tiglate@users.noreply.github.com>\n" +
            $"Installed-Size: {installedKib}\n" +
            $"Depends: {Depends}\n" +
            $"Recommends: {Recommends}\n" +
            "Section: sound\n" +
            "Priority: optional\n" +
            "Homepage: https://github.com/tiglate/Aibell\n" +
            "Description: Music library and player\n" +
            " Aibell is a desktop music library and player built with .NET and Avalonia.\n" +
            " It has a dense library browser, playlists and smart playlists, podcasts,\n" +
            " audiobooks, internet radio, a ten-band equalizer, and tag and artwork\n" +
            " editing.\n" +
            " .\n" +
            " The package is self-contained: it carries its own .NET runtime and a\n" +
            " built-in player for MP3, FLAC, Ogg Vorbis and WAV. LibVLC is recommended\n" +
            " for other formats such as M4A and Opus.\n";

        DateTimeOffset epoch = stamp;
        byte[] controlTar = Tar([("./", null, false, null), ("./control", Encoding.UTF8.GetBytes(control), false, null), ("./md5sums", Encoding.UTF8.GetBytes(md5sums.ToString()), false, null)], epoch);
        List<(string Path, byte[]? Content, bool Executable, string? Link)> entries = [("./", null, false, null)];
        HashSet<string> directories = [];
        foreach ((string path, (byte[]? content, bool executable, string? link)) in payload)
        {
            string[] parts = path.Split('/');
            for (int depth = 1; depth < parts.Length; depth++)
            {
                string directory = "./" + string.Join('/', parts[..depth]) + "/";
                if (directories.Add(directory)) entries.Add((directory, null, false, null));
            }
            entries.Add(("./" + path, content, executable, link));
        }
        byte[] dataTar = Tar(entries, epoch);

        Directory.CreateDirectory(output);
        string deb = Path.Combine(output, $"aibell_{version}_amd64.deb");
        if (File.Exists(deb)) throw new IOException("The .deb already exists; choose a fresh output directory or --deb-version.");
        string temporary = deb + ".partial";
        try
        {
            using (FileStream file = File.Create(temporary))
                WriteAr(file, epoch, [("debian-binary", "2.0\n"u8.ToArray()), ("control.tar.gz", Gzip(controlTar)), ("data.tar.gz", Gzip(dataTar))]);
            File.Move(temporary, deb, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
        File.WriteAllText(deb + ".sha256", $"{RepositoryRoot.Sha256(deb)}  {Path.GetFileName(deb)}\n", Encoding.ASCII);
        return deb;
    }

    private static string Copyright(string license)
    {
        StringBuilder text = new();
        text.Append("Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/\n")
            .Append("Upstream-Name: Aibell\nUpstream-Contact: Tiglate Pileser III\nSource: https://github.com/tiglate/Aibell\n\n")
            .Append("Files: *\nCopyright: 2026 Tiglate Pileser III\nLicense: MIT\n\n")
            .Append("Files: usr/lib/aibell/*\nCopyright: Microsoft, the Avalonia project and the other authors listed in\n /usr/share/doc/aibell/dependency-notices\nLicense: MIT\n")
            .Append(" The bundled .NET runtime and NuGet libraries keep their own licenses; their notices are in\n /usr/share/doc/aibell/dependency-notices.\n\n")
            .Append("License: MIT\n");
        foreach (string line in license.ReplaceLineEndings("\n").TrimEnd().Split('\n'))
            text.Append(line.Length == 0 ? " .\n" : " " + line + "\n");
        return text.ToString();
    }

    private static string Changelog(string version, string revision, DateTimeOffset stamp) =>
        $"aibell ({version}) unstable; urgency=low\n\n  * Built from revision {revision[..12]}.\n\n" +
        $" -- Tiglate Pileser III <tiglate@users.noreply.github.com>  {stamp.UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture)} +0000\n";

    private static byte[] Gzip(byte[] content)
    {
        using MemoryStream memory = new();
        using (GZipStream gzip = new(memory, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(content);
        return memory.ToArray();
    }

    private static byte[] Tar(IEnumerable<(string Path, byte[]? Content, bool Executable, string? Link)> entries, DateTimeOffset stamp)
    {
        using MemoryStream memory = new();
        using (TarWriter tar = new(memory, TarEntryFormat.Gnu, leaveOpen: true))
            foreach ((string path, byte[]? content, bool executable, string? link) in entries)
            {
                GnuTarEntry entry = link is not null ? new(TarEntryType.SymbolicLink, path.TrimEnd('/')) { LinkName = link }
                    : content is null ? new(TarEntryType.Directory, path)
                    : new(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(content) };
                entry.ModificationTime = stamp;
                entry.Uid = 0;
                entry.Gid = 0;
                entry.UserName = "root";
                entry.GroupName = "root";
                entry.Mode = link is not null ? (UnixFileMode)0x1FF : content is null ? (UnixFileMode)0x1ED : (UnixFileMode)(executable ? 0x1ED : 0x1A4);
                tar.WriteEntry(entry);
            }
        return memory.ToArray();
    }

    private static void WriteAr(Stream output, DateTimeOffset stamp, IEnumerable<(string Name, byte[] Content)> members)
    {
        output.Write("!<arch>\n"u8);
        foreach ((string name, byte[] content) in members)
        {
            string header = string.Create(CultureInfo.InvariantCulture, $"{name + "/",-16}{stamp.ToUnixTimeSeconds(),-12}{0,-6}{0,-6}{"100644",-8}{content.Length,-10}`\n");
            output.Write(Encoding.ASCII.GetBytes(header));
            output.Write(content);
            if (content.Length % 2 == 1) output.WriteByte((byte)'\n');
        }
    }
}
