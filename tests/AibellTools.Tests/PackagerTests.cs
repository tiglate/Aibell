using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace AibellTools.Tests;

[TestFixture]
public sealed class PackagerTests
{
    private const string Revision = "abc1230000000000000000000000000000000000";
    private const string Lgpl = "Licensed under the terms of the GNU Lesser General Public License, version 2.1 or later.";
    private const string Gpl = "Licensed under the terms of the GNU General Public License, version 2 or later.";
    private static readonly string HostRid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
    private static readonly string[] TestRids = OperatingSystem.IsWindows() ? ["win-x64"] : ["linux-x64", "win-x64"];

    private static readonly (string Text, VlcLicenses.Kind Kind)[] Declared =
        [(Lgpl, VlcLicenses.Kind.Lgpl), (Gpl, VlcLicenses.Kind.Gpl), ("Licensed under the MIT license.", VlcLicenses.Kind.Unknown)];

    private static readonly string[] BundledPaths = ["libvlc.dll", "libvlccore.dll", "plugins/access.dll"];

    private TempFolder folder = null!;
    private string source = "", cache = "", license = "";

    [SetUp]
    public void Create()
    {
        folder = new TempFolder();
        source = folder.Combine("publish");
        Directory.CreateDirectory(source);
        cache = folder.Combine("nuget");
        string package = Path.Combine(cache, "example", "2.0.0", "example.2.0.0.nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            Add(archive, "Example.nuspec", "<package><metadata><authors>Synthetic Author</authors><license type=\"file\">LICENSE.txt</license></metadata></package>");
            Add(archive, "LICENSE.txt", "Synthetic dependency license");
        }
        license = folder.Combine("LICENSE");
        File.WriteAllText(license, "Synthetic license fixture");
    }

    [TearDown]
    public void Remove() => folder.Dispose();

    private static void Add(ZipArchive archive, string name, string text)
    {
        using StreamWriter writer = new(archive.CreateEntry(name).Open());
        writer.Write(text);
    }

    private void Fixture(string rid)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(source)) { if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); }
        bool windows = rid.StartsWith("win-", StringComparison.Ordinal);
        string host = Path.Combine(source, windows ? "Aibell.Desktop.exe" : "Aibell.Desktop");
        File.WriteAllBytes(host, windows ? "MZfixture"u8.ToArray() : [0x7F, (byte)'E', (byte)'L', (byte)'F', .. "fixture"u8]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(host, (UnixFileMode)0x1ED);
        File.WriteAllText(Path.Combine(source, windows ? "coreclr.dll" : "libcoreclr.so"), "runtime fixture");
        File.WriteAllText(Path.Combine(source, "Aibell.Desktop.dll"), "managed fixture");
        File.WriteAllText(Path.Combine(source, "Aibell.Desktop.deps.json"),
            "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0/" + rid + "\"},\"libraries\":{\"Aibell.Desktop/1.0.0\":{\"type\":\"project\"},\"Example/2.0.0\":{\"type\":\"package\",\"sha512\":\"fixture\"}}}");
        File.WriteAllText(Path.Combine(source, "Aibell.Desktop.runtimeconfig.json"), "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.12\"}]}}");
    }

    private (string Archive, string Checksum) Package(string? rid = null, string? output = null, string? bundle = null, string revision = Revision) =>
        Packager.Package(source, output ?? folder.Combine("packages"), rid ?? HostRid, revision, false, license, cache, bundle);

    /// <summary>A minimal x64 PE whose vlc_entry_license__3_0_0f export returns the text (lea rax,[rip+disp]; ret).</summary>
    internal static byte[] VlcPlugin(string licenseText)
    {
        byte[] text = [.. Encoding.ASCII.GetBytes(licenseText), 0], name = [.. "vlc_entry_license__3_0_0f"u8, 0];
        const int sectionRva = 0x1000, raw = 0x200, exportSize = 40 + 4 + 4 + 2;
        int namesRva = sectionRva + exportSize, functionRva = namesRva + name.Length, textRva = functionRva + 16;
        byte[] code = new byte[8];
        code[0] = 0x48; code[1] = 0x8D; code[2] = 0x05; code[7] = 0xC3;
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(3), textRva - (functionRva + 7));
        byte[] body = new byte[exportSize];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), sectionRva + 40);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(32), sectionRva + 44);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(36), sectionRva + 48);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(40), (uint)functionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(44), (uint)namesRva);
        body = [.. body, .. name, .. code, .. new byte[16 - code.Length], .. text];
        byte[] optional = new byte[240];
        BinaryPrimitives.WriteUInt16LittleEndian(optional, 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(optional.AsSpan(112), sectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(optional.AsSpan(116), exportSize);
        byte[] coff = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(coff, 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(coff.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(coff.AsSpan(16), (ushort)optional.Length);
        byte[] section = new byte[40];
        ".text"u8.CopyTo(section);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(8), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(12), sectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(16), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(20), raw);
        byte[] header = new byte[0x40];
        header[0] = (byte)'M'; header[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x3C), 0x40);
        byte[] image = [.. header, .. "PE\0\0"u8, .. coff, .. optional, .. section];
        return [.. image, .. new byte[raw - image.Length], .. body];
    }

    private string LibVlcFixture(string name = "vlc-runtime")
    {
        string runtime = folder.Combine(name);
        Directory.CreateDirectory(Path.Combine(runtime, "plugins"));
        File.WriteAllText(Path.Combine(runtime, "libvlc.dll"), "libvlc placeholder");
        File.WriteAllText(Path.Combine(runtime, "libvlccore.dll"), "libvlccore placeholder");
        return runtime;
    }

    private string Extract(string archive, string name)
    {
        string destination = folder.Combine(name);
        Directory.CreateDirectory(destination);
        if (archive.EndsWith(".zip", StringComparison.Ordinal)) ZipFile.ExtractToDirectory(archive, destination);
        else
        {
            using GZipStream gzip = new(File.OpenRead(archive), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
        }
        return Directory.GetDirectories(destination).Single();
    }

    private static JsonDocument Manifest(string candidate) => JsonDocument.Parse(File.ReadAllText(Path.Combine(candidate, "candidate-manifest.json")));

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Test]
    public void Package_ArchivesRoundTripManifestHashesDependenciesAndTheExecuteBit()
    {
        foreach (string rid in TestRids)
        {
            Fixture(rid);
            Dictionary<string, byte[]> original = Directory.GetFiles(source).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);

            (string archive, string checksum) = Package(rid, folder.Combine(rid));

            Assert.That(File.ReadAllText(checksum).Split(' ')[0], Is.EqualTo(Sha(archive)), rid);
            string candidate = Extract(archive, "extracted-" + rid);
            using JsonDocument manifest = Manifest(candidate);
            JsonElement root = manifest.RootElement;
            Assert.That(root.GetProperty("revision").GetString(), Is.EqualTo(Revision));
            Assert.That(root.GetProperty("rid").GetString(), Is.EqualTo(rid));
            Assert.That(root.GetProperty("libVlcBundled").GetBoolean(), Is.False);
            Assert.That(root.GetProperty("sourceDirty").GetBoolean(), Is.False);
            foreach (JsonElement entry in root.GetProperty("files").EnumerateArray())
            {
                string payload = Path.Combine(candidate, entry.GetProperty("path").GetString()!);
                Assert.That(new FileInfo(payload).Length, Is.EqualTo(entry.GetProperty("bytes").GetInt64()));
                Assert.That(Sha(payload), Is.EqualTo(entry.GetProperty("sha256").GetString()));
            }
            foreach ((string name, byte[] contents) in original)
            {
                Assert.That(File.ReadAllBytes(Path.Combine(candidate, name)), Is.EqualTo(contents));
                Assert.That(File.ReadAllBytes(Path.Combine(source, name)), Is.EqualTo(contents), "the input is never modified");
            }
            using JsonDocument inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(candidate, "dependency-inventory.json")));
            JsonElement dependency = inventory.RootElement.EnumerateArray().Single(item => item.GetProperty("name").GetString() == "Example");
            Assert.That(dependency.GetProperty("license").GetProperty("value").GetString(), Is.EqualTo("LICENSE.txt"));
            string notice = dependency.GetProperty("noticeFiles")[0].GetString()!;
            Assert.That(File.ReadAllText(Path.Combine(candidate, notice)), Is.EqualTo("Synthetic dependency license"));
            if (rid.StartsWith("linux-", StringComparison.Ordinal) && !OperatingSystem.IsWindows())
            {
                Assert.That(File.GetUnixFileMode(Path.Combine(candidate, "Aibell.Desktop")) & UnixFileMode.UserExecute, Is.EqualTo(UnixFileMode.UserExecute));
                using GZipStream gzip = new(File.OpenRead(archive), CompressionMode.Decompress);
                using TarReader reader = new(gzip);
                TarEntry? entry;
                while ((entry = reader.GetNextEntry()) is not null)
                {
                    Assert.That(entry.EntryType, Is.EqualTo(TarEntryType.RegularFile));
                    Assert.That((entry.Uid, entry.Gid, entry.ModificationTime), Is.EqualTo((0, 0, DateTimeOffset.UnixEpoch)));
                }
            }
        }
    }

    [Test]
    public void Package_ByDefault_LeavesLibVlcUnbundled()
    {
        Fixture(HostRid);

        string candidate = Extract(Package().Archive, "extracted-unbundled");

        using JsonDocument manifest = Manifest(candidate);
        Assert.That(manifest.RootElement.GetProperty("libVlcBundled").GetBoolean(), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(candidate, "CANDIDATE.txt")), Does.Contain("Native LibVLC is NOT bundled."));
        Assert.That(Directory.Exists(Path.Combine(candidate, "dependency-notices", "LibVLC")), Is.False);
    }

    [Test]
    public void BundleLibVlc_CopiesTheRuntimeNextToTheWindowsExecutableAndUpdatesTheManifest()
    {
        Fixture("win-x64");
        string runtime = LibVlcFixture();
        File.WriteAllBytes(Path.Combine(runtime, "plugins", "access.dll"), VlcPlugin(Lgpl));

        string candidate = Extract(Package("win-x64", bundle: runtime).Archive, "extracted-bundled");

        Assert.That(File.ReadAllText(Path.Combine(candidate, "libvlc.dll")), Is.EqualTo("libvlc placeholder"));
        Assert.That(File.ReadAllBytes(Path.Combine(candidate, "plugins", "access.dll")), Is.EqualTo(VlcPlugin(Lgpl)));
        using JsonDocument manifest = Manifest(candidate);
        Assert.That(manifest.RootElement.GetProperty("libVlcBundled").GetBoolean(), Is.True);
        HashSet<string> listed = manifest.RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetProperty("path").GetString()!).ToHashSet();
        Assert.That(listed, Is.SupersetOf(BundledPaths));
        Assert.That(File.ReadAllText(Path.Combine(candidate, "CANDIDATE.txt")), Does.Contain("bundled for Windows").And.Contain(Path.GetFullPath(runtime)));
        string notice = File.ReadAllText(Path.Combine(candidate, "dependency-notices", "LibVLC", "NOTICE.txt"));
        Assert.That(notice, Does.Contain("LGPL libVLC core").And.Contain("1 plugins bundled, 0 GPL plugins left out"));
    }

    [Test]
    public void BundleLibVlc_LeavesGplPluginsOut_ListsThem_AndIgnoresTheCacheFile()
    {
        Fixture("win-x64");
        string runtime = LibVlcFixture();
        string codec = Path.Combine(runtime, "plugins", "codec");
        Directory.CreateDirectory(codec);
        File.WriteAllBytes(Path.Combine(codec, "libflac_plugin.dll"), VlcPlugin(Lgpl));
        File.WriteAllBytes(Path.Combine(codec, "libx264_plugin.dll"), VlcPlugin(Gpl));
        File.WriteAllText(Path.Combine(runtime, "plugins", "plugins.dat"), "cache of the original installation");

        string candidate = Extract(Package("win-x64", bundle: runtime).Archive, "verified-gpl");

        Assert.That(File.Exists(Path.Combine(candidate, "plugins", "codec", "libflac_plugin.dll")), Is.True);
        Assert.That(File.Exists(Path.Combine(candidate, "plugins", "codec", "libx264_plugin.dll")), Is.False);
        Assert.That(File.Exists(Path.Combine(candidate, "plugins", "plugins.dat")), Is.False);
        string notices = Path.Combine(candidate, "dependency-notices", "LibVLC");
        Assert.That(File.ReadAllText(Path.Combine(notices, "excluded-gpl-plugins.txt")), Is.EqualTo("plugins/codec/libx264_plugin.dll\n"));
        Assert.That(File.ReadAllText(Path.Combine(notices, "bundled-plugin-licenses.txt")), Does.Contain("plugins/codec/libflac_plugin.dll\tLicensed under the terms of the GNU Lesser"));
        Assert.That(File.ReadAllText(Path.Combine(notices, "NOTICE.txt")), Does.Contain("1 plugins bundled, 1 GPL plugins left out"));
    }

    [TestCase("not-a-pe")]
    [TestCase("other-license")]
    [TestCase("truncated")]
    public void BundleLibVlc_RefusesAPluginWhoseLicenseCannotBeRead_WithoutOutput(string label)
    {
        Fixture("win-x64");
        string runtime = LibVlcFixture("vlc-" + label);
        File.WriteAllBytes(Path.Combine(runtime, "plugins", "mystery.dll"), label switch
        {
            "not-a-pe" => "placeholder plugin"u8.ToArray(),
            "other-license" => VlcPlugin("Licensed under the MIT license."),
            _ => VlcPlugin(Lgpl)[..300]
        });
        string output = folder.Combine("packages-" + label);

        PackagingException? error = Assert.Throws<PackagingException>(() => Package("win-x64", output, runtime));

        Assert.That(error!.Message, Does.Contain("declared license"));
        Assert.That(Directory.Exists(output), Is.False);
    }

    [Test]
    public void VlcLicenses_ClassifiesDeclaredTexts()
    {
        string runtime = LibVlcFixture("vlc-read");
        foreach ((string text, VlcLicenses.Kind kind) in Declared)
        {
            string plugin = Path.Combine(runtime, "plugins", kind + ".dll");
            File.WriteAllBytes(plugin, VlcPlugin(text));
            Assert.That(VlcLicenses.PluginLicense(plugin), Is.EqualTo(text));
            Assert.That(VlcLicenses.Classify(VlcLicenses.PluginLicense(plugin)), Is.EqualTo(kind));
        }
        string garbage = Path.Combine(runtime, "plugins", "garbage.dll");
        File.WriteAllBytes(garbage, [.. "MZ"u8, .. new byte[400]]);
        Assert.That(VlcLicenses.PluginLicense(garbage), Is.Null);
    }

    [Test]
    public void BundleLibVlc_RejectsTheLinuxRid()
    {
        Fixture("linux-x64");
        string runtime = LibVlcFixture();

        PackagingException? error = Assert.Throws<PackagingException>(() => Package("linux-x64", bundle: runtime));

        Assert.That(error!.Message, Does.Contain("win-x64"));
    }

    [TestCase("root")]
    [TestCase("library")]
    [TestCase("plugins-root")]
    [TestCase("plugin-file")]
    [TestCase("plugin-directory")]
    public void BundleLibVlc_RejectsLinksAtEveryPayloadBoundary_WithoutOutput(string kind)
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Symbolic links need a privileged account on Windows.");
        Fixture("win-x64");
        string runtime = LibVlcFixture("vlc-" + kind);
        string external = folder.Combine("external-" + kind);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "external.dll"), "outside installation");
        string selected = runtime;
        switch (kind)
        {
            case "root":
                selected = folder.Combine("linked-runtime");
                Directory.CreateSymbolicLink(selected, runtime);
                break;
            case "library":
                File.Delete(Path.Combine(runtime, "libvlc.dll"));
                File.CreateSymbolicLink(Path.Combine(runtime, "libvlc.dll"), Path.Combine(external, "external.dll"));
                break;
            case "plugins-root":
                Directory.Delete(Path.Combine(runtime, "plugins"));
                Directory.CreateSymbolicLink(Path.Combine(runtime, "plugins"), external);
                break;
            case "plugin-file":
                File.CreateSymbolicLink(Path.Combine(runtime, "plugins", "linked.dll"), Path.Combine(external, "external.dll"));
                break;
            default:
                Directory.CreateSymbolicLink(Path.Combine(runtime, "plugins", "linked"), external);
                break;
        }
        string output = folder.Combine("packages-" + kind);

        Assert.Throws<PackagingException>(() => Package("win-x64", output, selected));

        Assert.That(Directory.Exists(output), Is.False);
    }

    [Test]
    public void BundleLibVlc_PreservesSuppliedNotices_WithoutClassifyingAllPluginsAsLgpl()
    {
        Fixture("win-x64");
        string runtime = LibVlcFixture();
        File.WriteAllText(Path.Combine(runtime, "COPYING.txt"), "Synthetic GPL fixture");
        File.WriteAllText(Path.Combine(runtime, "COPYING.LIB"), "Synthetic LGPL fixture");
        File.WriteAllBytes(Path.Combine(runtime, "plugins", "access.dll"), VlcPlugin(Lgpl));

        string candidate = Extract(Package("win-x64", bundle: runtime).Archive, "verified-bundle");

        string notices = Path.Combine(candidate, "dependency-notices", "LibVLC");
        Assert.That(File.ReadAllText(Path.Combine(notices, "supplied", "COPYING.txt")), Is.EqualTo("Synthetic GPL fixture"));
        Assert.That(File.ReadAllText(Path.Combine(notices, "supplied", "COPYING.LIB")), Is.EqualTo("Synthetic LGPL fixture"));
        Assert.That(File.ReadAllText(Path.Combine(notices, "NOTICE.txt")), Does.Contain("plugins").And.Contain("different licenses").And.Contain("1 plugins bundled"));
    }

    [TestCase("library.db")]
    [TestCase("settings.json")]
    [TestCase("song.flac")]
    [TestCase(".secret")]
    [TestCase("nested/profile.db")]
    public void Package_RejectsProfilesMediaAndHiddenFiles_WithoutOutput(string name)
    {
        Fixture(HostRid);
        string extra = Path.Combine(source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
        File.WriteAllText(extra, "private fixture");

        Assert.Throws<PackagingException>(() => Package());

        Assert.That(Directory.Exists(folder.Combine("packages")), Is.False);
    }

    [Test]
    public void Package_RejectsSymbolicLinksInThePublishFolder()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Symbolic links need a privileged account on Windows.");
        Fixture(HostRid);
        File.CreateSymbolicLink(Path.Combine(source, "linked.dll"), license);

        Assert.Throws<PackagingException>(() => Package());
    }

    [Test]
    public void Package_WrongRidIncompleteRuntimeAndUnsafeRevision_AreRejected()
    {
        Fixture(HostRid);
        Assert.Throws<PackagingException>(() => Package(HostRid == "win-x64" ? "linux-x64" : "win-x64"));
        Assert.Throws<PackagingException>(() => Package("linux-x64", revision: "../../unsafe"));
        File.WriteAllText(Path.Combine(source, "Aibell.Desktop.runtimeconfig.json"), "{\"runtimeOptions\":{\"framework\":{\"name\":\"Microsoft.NETCore.App\"}}}");
        Assert.Throws<PackagingException>(() => Package());
    }

    [Test]
    public void Package_IsDeterministic_NeverOverwritesAndNeverPackagesItself()
    {
        Fixture(HostRid);

        (string first, _) = Package(output: folder.Combine("first"));
        (string second, _) = Package(output: folder.Combine("second"));

        Assert.That(File.ReadAllBytes(first), Is.EqualTo(File.ReadAllBytes(second)));
        Assert.Throws<IOException>(() => Package(output: folder.Combine("first")));
        Assert.That(File.ReadAllBytes(first), Is.EqualTo(File.ReadAllBytes(second)));
        Assert.Throws<PackagingException>(() => Package(output: Path.Combine(source, "packages")));
    }
}
