using System.IO.Compression;
using System.Xml.Linq;
using NUnit.Framework;

namespace AibellTools.Tests;

[TestFixture]
public sealed class MsiPackagerTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly string[] ExpectedFolders = ["de", "licenses", "dependency-notices", "Example"];

    private TempFolder folder = null!;
    private string source = "", cache = "";

    [SetUp]
    public void Create()
    {
        folder = new TempFolder();
        source = folder.Combine("publish");
        Directory.CreateDirectory(Path.Combine(source, "de"));
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
        File.WriteAllBytes(Path.Combine(source, "Aibell.Desktop.exe"), [(byte)'M', (byte)'Z', .. "fixture"u8]);
        File.WriteAllBytes(Path.Combine(source, "createdump.exe"), [(byte)'M', (byte)'Z']);
        foreach (string name in new[] { "coreclr.dll", "Aibell.Desktop.dll", "Aibell.Desktop.pdb", Path.Combine("de", "Aibell.Desktop.resources.dll") })
            File.WriteAllText(Path.Combine(source, name), "fixture " + name);
        File.WriteAllText(Path.Combine(source, "Aibell.Desktop.deps.json"),
            "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0/win-x64\"},\"libraries\":{\"Aibell.Desktop/1.2.3-dev\":{\"type\":\"project\"},\"Example/2.0.0\":{\"type\":\"package\",\"sha512\":\"fixture\"}}}");
        File.WriteAllText(Path.Combine(source, "Aibell.Desktop.runtimeconfig.json"), "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.12\"}]}}");
    }

    [TearDown]
    public void Remove() => folder.Dispose();

    private string Build(MsiPackager.WixCompiler compile, string? output = null) =>
        MsiPackager.Build(source, output ?? folder.Combine("out"), RepositoryRoot.Find(), cache, compile);

    [TestCase("1.0.0", "1.0.0")]
    [TestCase("1.2.3-dev", "1.2.3")]
    [TestCase("2.10.0+abc123", "2.10.0")]
    [TestCase("01.02.0003", "1.2.3")]
    public void ProductVersion_KeepsTheNumericPart(string version, string expected) =>
        Assert.That(MsiPackager.ProductVersion(version), Is.EqualTo(expected));

    [TestCase("1.0")]
    [TestCase("dev")]
    [TestCase("1.0.0.1")]
    [TestCase("256.0.0")]
    [TestCase("1.256.0")]
    [TestCase("1.0.65536")]
    public void ProductVersion_RejectsWhatWindowsInstallerCannotCompare(string version) =>
        Assert.That(() => MsiPackager.ProductVersion(version), Throws.TypeOf<PackagingException>());

    [Test]
    public void Id_IsStableUniqueAndValid()
    {
        string a = MsiPackager.Id("f", "licenses/LICENSE"), b = MsiPackager.Id("f", "licenses/license");

        Assert.That(a, Is.EqualTo(MsiPackager.Id("f", "licenses/LICENSE")));
        Assert.That(a, Is.EqualTo(b), "Windows paths ignore case, so the ids do too");
        Assert.That(a, Is.Not.EqualTo(MsiPackager.Id("c", "licenses/LICENSE")));
        Assert.That(a, Does.Match("^[A-Za-z_][A-Za-z0-9_.]{0,70}$"));
    }

    [Test]
    public void Rtf_EscapesControlCharactersAndNonAscii()
    {
        string rtf = MsiPackager.Rtf("a\\b {c}\r\nd\té");

        Assert.That(rtf, Does.StartWith("{\\rtf1"));
        Assert.That(rtf, Does.Contain("a\\\\b \\{c\\}\\par d\\tab \\u233?"));
        Assert.That(rtf, Does.EndWith("}"));
        Assert.That(rtf.All(character => character is >= ' ' and <= '~'), Is.True, "RTF is plain ASCII");
    }

    [Test]
    public void Source_DescribesAPerMachineInstallWithAShortcutAndUpgrade()
    {
        SortedDictionary<string, string> payload = new(StringComparer.Ordinal)
        {
            ["Aibell.Desktop.exe"] = @"C:\p\Aibell.Desktop.exe",
            ["de/Aibell.Desktop.resources.dll"] = @"C:\p\de\Aibell.Desktop.resources.dll",
            ["licenses/NOTICE.Alac"] = @"C:\x\NOTICE",
            ["licenses/dependency-notices/Example/LICENSE.txt"] = @"C:\x\a\LICENSE.txt"
        };

        XDocument document = XDocument.Parse(MsiPackager.Source(payload, "1.2.3", "1.2.3-dev", @"C:\i\aibell.ico", @"C:\l\license.rtf"));

        XElement package = document.Root!.Element(Wix + "Package")!;
        Assert.That((string?)package.Attribute("Version"), Is.EqualTo("1.2.3"));
        Assert.That((string?)package.Attribute("Scope"), Is.EqualTo("perMachine"));
        Assert.That((string?)package.Attribute("UpgradeCode"), Is.EqualTo(MsiPackager.UpgradeCode));
        Assert.That(package.Element(Wix + "MajorUpgrade"), Is.Not.Null);
        Assert.That(package.Elements(Wix + "Property").Single(item => (string?)item.Attribute("Id") == "ARPCOMMENTS").Attribute("Value")!.Value, Is.EqualTo("Aibell 1.2.3-dev"));
        Assert.That(package.Element(Wix + "WixVariable")!.Attribute("Value")!.Value, Is.EqualTo(@"C:\l\license.rtf"));
        Assert.That(package.Descendants().Single(item => item.Name.LocalName == "WixUI").Attribute("Id")!.Value, Is.EqualTo("WixUI_Minimal"));

        List<XElement> files = [.. package.Descendants(Wix + "File")];
        Assert.That(files, Has.Count.EqualTo(payload.Count));
        Assert.That(files.Select(file => (string?)file.Attribute("Source")), Is.EquivalentTo(payload.Values));
        Assert.That(files.Select(file => (string?)file.Attribute("Id")).Distinct().Count(), Is.EqualTo(files.Count), "file ids are unique");
        Assert.That(files.Single(file => (string?)file.Attribute("Source") == @"C:\x\NOTICE").Attribute("Name")!.Value, Is.EqualTo("NOTICE.Alac"), "the installed name comes from the payload path, not the source");
        XElement shortcut = package.Descendants(Wix + "Shortcut").Single();
        Assert.That(shortcut.Parent!.Attribute("Id")!.Value, Is.EqualTo("AibellExe"));
        Assert.That((string?)shortcut.Attribute("Directory"), Is.EqualTo("ProgramMenuFolder"));
        XElement identity = shortcut.Element(Wix + "ShortcutProperty")!;
        Assert.That((string?)identity.Attribute("Key"), Is.EqualTo("System.AppUserModel.ID"));
        Assert.That((string?)identity.Attribute("Value"), Is.EqualTo(MsiPackager.AppUserModelId));

        XElement install = package.Elements(Wix + "StandardDirectory").Single(item => (string?)item.Attribute("Id") == "ProgramFiles64Folder").Element(Wix + "Directory")!;
        Assert.That((string?)install.Attribute("Name"), Is.EqualTo("Aibell"));
        string[] folders = [.. install.Descendants(Wix + "Directory").Select(item => (string)item.Attribute("Name")!)];
        Assert.That(folders, Is.EquivalentTo(ExpectedFolders));
        Assert.That(install.Descendants(Wix + "Directory").Single(item => (string?)item.Attribute("Name") == "Example").Parent!.Attribute("Name")!.Value, Is.EqualTo("dependency-notices"));
        string[] referenced = [.. package.Descendants(Wix + "Component").Select(item => (string)item.Attribute("Directory")!)];
        string[] defined = [.. install.DescendantsAndSelf(Wix + "Directory").Select(item => (string)item.Attribute("Id")!)];
        Assert.That(referenced.Except(defined), Is.Empty, "every component installs into a directory that exists");
    }

    [Test]
    public void Source_WithoutTheApplicationHost_Fails() =>
        Assert.That(() => MsiPackager.Source(new Dictionary<string, string> { ["a.dll"] = "a.dll" }, "1.0.0", "1.0.0", "i.ico", "l.rtf"), Throws.TypeOf<PackagingException>());

    [Test]
    public void Build_StagesThePayloadWithoutDebugFilesAndNamesTheInstallerAfterTheVersion()
    {
        string wxs = "";
        string msi = Build((input, output, _) =>
        {
            wxs = File.ReadAllText(input);
            File.WriteAllText(output, "stub");
        });

        Assert.That(Path.GetFileName(msi), Is.EqualTo("Aibell-1.2.3-dev-x64.msi"));
        XDocument document = XDocument.Parse(wxs);
        string[] names = [.. document.Descendants(Wix + "File").Select(file => (string)file.Attribute("Name")!)];
        Assert.That(names, Does.Contain("Aibell.Desktop.exe").And.Contain("coreclr.dll").And.Contain("Aibell.Desktop.resources.dll").And.Contain("LICENSE"));
        Assert.That(names, Does.Not.Contain("createdump.exe").And.Not.Contain("Aibell.Desktop.pdb"));
        Assert.That(document.Descendants(Wix + "File").Any(file => ((string)file.Attribute("Source")!).EndsWith("LICENSE.txt", StringComparison.Ordinal)), Is.True, "the dependency notice is staged");
        Assert.That(document.Root!.Element(Wix + "Package")!.Attribute("Version")!.Value, Is.EqualTo("1.2.3"));
    }

    [Test]
    public void Build_StagedFilesExistWhileTheCompilerRunsAndAreRemovedAfterwards()
    {
        string staged = "";
        Build((input, output, _) =>
        {
            staged = Path.GetDirectoryName(input)!;
            foreach (XElement file in XDocument.Load(input).Descendants(Wix + "File").Where(file => ((string)file.Attribute("Source")!).StartsWith(staged, StringComparison.Ordinal)))
                Assert.That(File.Exists((string)file.Attribute("Source")!), Is.True);
            File.WriteAllText(output, "stub");
        });

        Assert.That(Directory.Exists(staged), Is.False);
    }

    [Test]
    public void Build_RefusesToOverwriteAnExistingInstaller()
    {
        Build((_, output, _) => File.WriteAllText(output, "stub"));

        Assert.That(() => Build((_, output, _) => File.WriteAllText(output, "stub")), Throws.TypeOf<IOException>().With.Message.Contains("already exists"));
    }

    [Test]
    public void Build_RefusesOutputInsideThePublishFolder() =>
        Assert.That(() => Build((_, _, _) => { }, Path.Combine(source, "out")), Throws.TypeOf<PackagingException>().With.Message.Contains("outside"));

    [Test]
    public void Build_FailsWhenTheCompilerProducesNothing() =>
        Assert.That(() => Build((_, _, _) => { }), Throws.TypeOf<PackagingException>().With.Message.Contains("did not produce"));

    [Test]
    public void Build_RejectsAnIncompletePublish()
    {
        File.Delete(Path.Combine(source, "coreclr.dll"));

        Assert.That(() => Build((_, output, _) => File.WriteAllText(output, "stub")), Throws.TypeOf<PackagingException>().With.Message.Contains("Incomplete"));
    }

    [Test]
    public void Build_WithWix_ProducesAValidInstallerOnWindows()
    {
        Assume.That(OperatingSystem.IsWindows(), "WiX validation needs Windows Installer");
        string root = RepositoryRoot.Find();
        (int restored, _, _) = RepositoryRoot.Run("dotnet", ["tool", "restore"], root);
        Assume.That(restored, Is.Zero, "the local tools could not be restored (offline?)");
        string output = folder.Combine("real");

        // The tool's own entry point, with the fixture's package cache standing in for the user's.
        string? previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", cache);
        int code;
        try { code = MsiPackager.Run(["--publish-dir", source, "--output", output]); }
        finally { Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous); }

        Assert.That(code, Is.Zero);
        string msi = Directory.GetFiles(output, "*.msi").Single();
        byte[] header = new byte[8];
        using (FileStream stream = File.OpenRead(msi)) _ = stream.Read(header);
        Assert.That(header, Is.EqualTo(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }), "an OLE compound file, which is what Windows Installer opens");
    }
}
