using System.IO.Compression;
using System.Text;
using NUnit.Framework;

namespace AibellTools.Tests;

[TestFixture]
public sealed class AppImagePackagerTests
{
    private const string Revision = "abc1230000000000000000000000000000000000";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private TempFolder folder = null!;
    private string source = "", cache = "", runtime = "";

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
        runtime = folder.Combine("runtime");
        File.WriteAllBytes(runtime, [0x7F, (byte)'E', (byte)'L', (byte)'F', 0, 0, 0, 0, (byte)'A', (byte)'I', 2, .. "runtime fixture"u8]);
    }

    [TearDown]
    public void Remove() => folder.Dispose();

    private string Build() => AppImagePackager.Build(source, folder.Combine("out"), RepositoryRoot.Find(), Revision, Stamp, runtime, cache);

    private static bool HasMksquashfs() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(directory => File.Exists(Path.Combine(directory, "mksquashfs")));

    [Test]
    public void Build_PrependsTheRuntimeToASquashfsImage()
    {
        Assume.That(OperatingSystem.IsLinux() && HasMksquashfs(), "needs Linux and squashfs-tools");

        string image = Build();

        Assert.That(Path.GetFileName(image), Is.EqualTo("Aibell-1.2.3-x86_64.AppImage"));
        byte[] bytes = File.ReadAllBytes(image);
        byte[] runtimeBytes = File.ReadAllBytes(runtime);
        Assert.That(bytes.AsSpan(0, runtimeBytes.Length).SequenceEqual(runtimeBytes), "the runtime is the image prefix");
        Assert.That(Encoding.ASCII.GetString(bytes, runtimeBytes.Length, 4), Is.EqualTo("hsqs"), "squashfs follows the runtime");
        if (!OperatingSystem.IsWindows()) Assert.That(File.GetUnixFileMode(image).HasFlag(UnixFileMode.UserExecute));
        Assert.That(File.ReadAllText(image + ".sha256"), Does.StartWith(RepositoryRoot.Sha256(image)));
        Assert.Throws<IOException>(() => Build());
    }

    [Test]
    public void Build_RejectsAFileThatIsNotAType2Runtime()
    {
        Assume.That(!OperatingSystem.IsWindows(), "AppImages are built on Linux; Windows refuses before reading the runtime.");
        File.WriteAllBytes(runtime, "not a runtime at all"u8.ToArray());

        Assert.That(() => Build(), Throws.TypeOf<PackagingException>().With.Message.Contains("type-2 runtime"));
    }

    [Test]
    public void EnsureRuntime_RefusesToDownloadWithoutTheFlagAndRejectsWrongBytes()
    {
        string cached = folder.Combine("tools", "runtime");

        Assert.That(() => AppImagePackager.EnsureRuntime(cached, download: false), Throws.TypeOf<ToolException>().With.Message.Contains("--download-runtime"));
        Assert.That(() => AppImagePackager.EnsureRuntime(cached, download: true, () => "tampered"u8.ToArray()), Throws.TypeOf<ToolException>().With.Message.Contains("mismatch"));
        Assert.That(File.Exists(cached), Is.False);
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.WriteAllText(cached, "tampered");
        Assert.That(() => AppImagePackager.EnsureRuntime(cached, download: false), Throws.TypeOf<ToolException>().With.Message.Contains("mismatch"));
    }
}
