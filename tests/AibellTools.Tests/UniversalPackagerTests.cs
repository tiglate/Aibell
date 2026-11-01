using System.IO.Compression;
using NUnit.Framework;

namespace AibellTools.Tests;

/// <summary>The Flatpak and Snap stagers, exercised without flatpak-builder or snapcraft.</summary>
[TestFixture]
public sealed class UniversalPackagerTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private TempFolder folder = null!;
    private string source = "", cache = "";

    [SetUp]
    public void Create()
    {
        Assume.That(!OperatingSystem.IsWindows(), "Linux packages are staged on Linux");
        folder = new TempFolder();
        source = folder.Combine("publish");
        Directory.CreateDirectory(source);
        cache = folder.Combine("nuget");
        string package = Path.Combine(cache, "example", "2.0.0", "example.2.0.0.nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            foreach ((string name, string text) in new[] { ("Example.nuspec", "<package><metadata><license type=\"file\">LICENSE.txt</license></metadata></package>"), ("LICENSE.txt", "Synthetic dependency license") })
            {
                using StreamWriter writer = new(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        string host = Path.Combine(source, "Aibell.Desktop");
        File.WriteAllBytes(host, [0x7F, (byte)'E', (byte)'L', (byte)'F', .. "fixture"u8]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(host, (UnixFileMode)0x1ED);
        foreach (string name in new[] { "libcoreclr.so", "Aibell.Desktop.dll" }) File.WriteAllText(Path.Combine(source, name), "fixture " + name);
        File.WriteAllText(Path.Combine(source, "Aibell.Desktop.deps.json"),
            "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0/linux-x64\"},\"libraries\":{\"Aibell.Desktop/1.2.3\":{\"type\":\"project\"},\"Example/2.0.0\":{\"type\":\"package\",\"sha512\":\"fixture\"}}}");
        File.WriteAllText(Path.Combine(source, "Aibell.Desktop.runtimeconfig.json"), "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.12\"}]}}");
    }

    [TearDown]
    public void Remove() => folder?.Dispose();

    [Test]
    public void Flatpak_StageOnlyWritesTheManifestAndAnAppRootedTree()
    {
        string work = FlatpakPackager.Build(source, folder.Combine("out"), RepositoryRoot.Find(), Stamp, "24.08", stageOnly: true, cache);

        string manifest = File.ReadAllText(Path.Combine(work, "io.github.tiglate.Aibell.yml"));
        Assert.That(manifest, Does.Contain("app-id: io.github.tiglate.Aibell").And.Contain("runtime-version: '24.08'").And.Contain("--own-name=org.mpris.MediaPlayer2.aibell.*"));
        Assert.That(File.Exists(Path.Combine(work, "stage", "lib", "aibell", "Aibell.Desktop")));
        Assert.That(new FileInfo(Path.Combine(work, "stage", "bin", "aibell")).LinkTarget, Is.EqualTo("../lib/aibell/Aibell.Desktop"));
        Assert.That(File.Exists(Path.Combine(work, "stage", "share", "applications", "io.github.tiglate.Aibell.desktop")));
    }

    [Test]
    public void Flatpak_RejectsAnUnsafeRuntimeVersion() =>
        Assert.That(() => FlatpakPackager.Build(source, folder.Combine("out"), RepositoryRoot.Find(), Stamp, "24.08\nx", stageOnly: true, cache), Throws.TypeOf<PackagingException>());

    [Test]
    public void Snap_StagesAProjectWhoseDesktopFilePointsAtTheSnapIcon()
    {
        string work = SnapPackager.Build(source, folder.Combine("out"), RepositoryRoot.Find(), Stamp, build: false, cache);

        string yaml = File.ReadAllText(Path.Combine(work, "snap", "snapcraft.yaml"));
        Assert.That(yaml, Does.Contain("version: '1.2.3'").And.Contain("base: core24").And.Contain("extensions: [gnome]").And.Contain("command: usr/bin/aibell"));
        Assert.That(File.ReadAllText(Path.Combine(work, "snap", "gui", "aibell.desktop")), Does.Contain("Icon=${SNAP}/meta/gui/icon.png"));
        Assert.That(File.Exists(Path.Combine(work, "snap", "gui", "icon.png")));
        Assert.That(File.Exists(Path.Combine(work, "stage", "usr", "lib", "aibell", "Aibell.Desktop")));
    }
}
