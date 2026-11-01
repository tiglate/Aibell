using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;

namespace AibellTools.Tests;

[TestFixture]
public sealed class DebPackagerTests
{
    private const string Revision = "abc1230000000000000000000000000000000000";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] ArchiveMembers = ["debian-binary", "control.tar.gz", "data.tar.gz"];

    private TempFolder folder = null!;
    private string source = "", cache = "";

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
        string host = Path.Combine(source, "Aibell.Desktop");
        File.WriteAllBytes(host, [0x7F, (byte)'E', (byte)'L', (byte)'F', .. "fixture"u8]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(host, (UnixFileMode)0x1ED);
        foreach (string name in new[] { "libcoreclr.so", "Aibell.Desktop.dll", "createdump", "libmscordbi.so" }) File.WriteAllText(Path.Combine(source, name), "fixture " + name);
        File.WriteAllText(Path.Combine(source, "Aibell.Desktop.deps.json"),
            "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0/linux-x64\"},\"libraries\":{\"Aibell.Desktop/1.2.3\":{\"type\":\"project\"},\"Example/2.0.0\":{\"type\":\"package\",\"sha512\":\"fixture\"}}}");
        File.WriteAllText(Path.Combine(source, "Aibell.Desktop.runtimeconfig.json"), "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.12\"}]}}");
    }

    [TearDown]
    public void Remove() => folder.Dispose();

    private static void Add(ZipArchive archive, string name, string text)
    {
        using StreamWriter writer = new(archive.CreateEntry(name).Open());
        writer.Write(text);
    }

    private string Build(string? version = null) => DebPackager.Build(source, folder.Combine("out"), RepositoryRoot.Find(), Revision, Stamp, version, cache);

    /// <summary>Reads the members of an ar archive.</summary>
    private static Dictionary<string, byte[]> ReadAr(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.That(Encoding.ASCII.GetString(bytes, 0, 8), Is.EqualTo("!<arch>\n"));
        Dictionary<string, byte[]> members = [];
        for (int offset = 8; offset < bytes.Length;)
        {
            string name = Encoding.ASCII.GetString(bytes, offset, 16).Trim().TrimEnd('/');
            int size = int.Parse(Encoding.ASCII.GetString(bytes, offset + 48, 10).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(Encoding.ASCII.GetString(bytes, offset + 58, 2), Is.EqualTo("`\n"));
            members[name] = bytes[(offset + 60)..(offset + 60 + size)];
            offset += 60 + size + (size % 2);
        }
        return members;
    }

    private static Dictionary<string, TarEntry> ReadTar(byte[] gzipped) => ReadTar(gzipped, out _);

    private static Dictionary<string, TarEntry> ReadTar(byte[] gzipped, out Dictionary<string, byte[]> contents)
    {
        contents = [];
        using GZipStream gzip = new(new MemoryStream(gzipped), CompressionMode.Decompress);
        using TarReader reader = new(gzip);
        Dictionary<string, TarEntry> entries = [];
        for (TarEntry? entry = reader.GetNextEntry(); entry is not null; entry = reader.GetNextEntry())
        {
            MemoryStream copy = new();
            entry.DataStream?.CopyTo(copy);
            string name = entry.Name.TrimStart('.').TrimStart('/');
            contents[name] = copy.ToArray();
            entries[name] = entry;
        }
        return entries;
    }

    [Test]
    public void Build_WritesAValidArchiveWithControlDataAndTheLauncherLink()
    {
        string deb = Build();

        Assert.That(Path.GetFileName(deb), Is.EqualTo("aibell_1.2.3-1_amd64.deb"));
        Dictionary<string, byte[]> members = ReadAr(deb);
        Assert.That(members.Keys, Is.EqualTo(ArchiveMembers));
        Assert.That(Encoding.ASCII.GetString(members["debian-binary"]), Is.EqualTo("2.0\n"));

        Dictionary<string, TarEntry> control = ReadTar(members["control.tar.gz"], out Dictionary<string, byte[]> controlData);
        string text = Encoding.UTF8.GetString(controlData["control"]);
        Assert.That(text, Does.Contain("Package: aibell\nVersion: 1.2.3-1\nArchitecture: amd64\n"));
        Assert.That(text, Does.Contain("Depends: ").And.Contain("libicu74"));
        Assert.That(control.Keys, Does.Contain("md5sums"));

        Dictionary<string, TarEntry> data = ReadTar(members["data.tar.gz"], out Dictionary<string, byte[]> dataFiles);
        Assert.That(data["usr/bin/aibell"].EntryType, Is.EqualTo(TarEntryType.SymbolicLink));
        Assert.That(data["usr/bin/aibell"].LinkName, Is.EqualTo("../lib/aibell/Aibell.Desktop"));
        // Windows has no execute bit to carry over from the fixture host.
        if (!OperatingSystem.IsWindows()) Assert.That(data["usr/lib/aibell/Aibell.Desktop"].Mode.HasFlag(UnixFileMode.UserExecute));
        Assert.That(data["usr/lib/aibell/Aibell.Desktop.dll"].Mode.HasFlag(UnixFileMode.UserExecute), Is.False);
        Assert.That(data["usr/lib/aibell/libcoreclr.so"].Mode.HasFlag(UnixFileMode.UserExecute), Is.False);
        Assert.That(data.Keys, Does.Contain("usr/share/applications/io.github.tiglate.Aibell.desktop")
            .And.Contain("usr/share/icons/hicolor/256x256/apps/io.github.tiglate.Aibell.png")
            .And.Contain("usr/share/icons/hicolor/scalable/apps/io.github.tiglate.Aibell.svg")
            .And.Contain("usr/share/doc/aibell/copyright")
            .And.Contain("usr/share/doc/aibell/dependency-notices/Example/2.0.0/LICENSE.txt"));
        Assert.That(data.Values.All(entry => entry.Uid == 0 && entry.Gid == 0));
        string metainfo = Encoding.UTF8.GetString(dataFiles["usr/share/metainfo/io.github.tiglate.Aibell.metainfo.xml"]);
        Assert.That(metainfo, Does.Contain("<release version=\"1.2.3\" date=\"2026-10-21\" />"));
    }

    [Test]
    public void Build_LeavesOutDiagnosticOnlyRuntimeFiles()
    {
        Dictionary<string, TarEntry> data = ReadTar(ReadAr(Build())["data.tar.gz"]);

        Assert.That(data.Keys, Does.Not.Contain("usr/lib/aibell/createdump"));
        Assert.That(data.Keys, Does.Not.Contain("usr/lib/aibell/libmscordbi.so"));
    }

    [Test]
    public void Build_RejectsDebugSymbolsAndExistingOutput()
    {
        string deb = Build();
        Assert.Throws<IOException>(() => Build());
        File.Delete(deb);
        File.WriteAllText(Path.Combine(source, "Aibell.Desktop.pdb"), "symbols");
        Assert.That(() => Build(), Throws.TypeOf<PackagingException>().With.Message.Contains("DebugType=none"));
    }

    [TestCase("not a version")]
    [TestCase("-1")]
    public void Build_RejectsAnInvalidDebianVersion(string version) =>
        Assert.That(() => Build(version), Throws.TypeOf<PackagingException>());
}
