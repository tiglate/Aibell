using System.IO.Compression;
using Aibell.Application.Plugins;
using Aibell.Infrastructure.Plugins;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Plugins;

[TestFixture]
public sealed class PluginManagementTests
{
    private const string Shipped = "Aibell.Metadata.MusicBrainz";
    private string root = "";
    private string bundled = "";
    private string user = "";
    private string statePath = "";

    [SetUp]
    public void CreateRoot()
    {
        root = Directory.CreateTempSubdirectory("aibell-plugin-admin").FullName;
        bundled = Path.Combine(root, "bundled");
        user = Path.Combine(root, "user");
        statePath = Path.Combine(root, "plugin-state.json");
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "plugins", Shipped), Path.Combine(bundled, Shipped));
    }

    [TearDown]
    public void DeleteRoot()
    {
        // Windows keeps loaded plugin assemblies locked; the temp folder is disposable.
        try { Directory.Delete(root, recursive: true); }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    [Test]
    public void LoadAll_DisabledPlugin_IsDescribedButNeverStarted()
    {
        PluginLoadReport report = PluginLoader.LoadAll([new PluginSource(bundled, false)], new PluginContext("unused"), new HashSet<string> { Shipped });

        Assert.That(report.Plugins, Is.Empty);
        PluginFound found = report.Discovered.Single();
        Assert.That(found.Started, Is.False);
        Assert.That(found.Name, Is.EqualTo("MusicBrainz"));
        Assert.That(found.Description, Does.Contain("MusicBrainz"));
        Assert.That(found.Version, Is.Not.Empty);
    }

    [Test]
    public void LoadAll_SameFolderInTwoRoots_KeepsTheFirstAndReportsTheSecond()
    {
        CopyDirectory(Path.Combine(bundled, Shipped), Path.Combine(user, Shipped));

        PluginLoadReport report = PluginLoader.LoadAll([new PluginSource(bundled, false), new PluginSource(user, true)],
            new PluginContext("unused"), new HashSet<string>());

        Assert.That(report.Plugins, Has.Count.EqualTo(1));
        Assert.That(report.Failures.Single().IsUserInstalled, Is.True);
    }

    [Test]
    public void LoadAll_StagingFolders_AreIgnored()
    {
        CopyDirectory(Path.Combine(bundled, Shipped), Path.Combine(user, ".installing-abc", Shipped));
        Directory.Move(Path.Combine(user, ".installing-abc", Shipped), Path.Combine(user, ".Aibell.Metadata.MusicBrainz"));

        PluginLoadReport report = PluginLoader.LoadAll(user, new PluginContext("unused"));

        Assert.That(report.Plugins, Is.Empty);
        Assert.That(report.Failures, Is.Empty);
    }

    [Test]
    public void StateStore_PersistsChoicesAndRoundTrips()
    {
        PluginStateStore store = new(statePath);
        store.SetEnabled("Alpha", false);
        store.MarkForRemoval("Beta");

        PluginStateStore reloaded = new(statePath);
        Assert.That(reloaded.Disabled, Is.EquivalentTo(Items("Alpha")));
        Assert.That(reloaded.PendingRemoval, Is.EquivalentTo(Items("Beta")));
        reloaded.SetEnabled("Alpha", true);
        Assert.That(new PluginStateStore(statePath).Disabled, Is.Empty);
    }

    [Test]
    public void StateStore_DamagedFile_MeansNothingIsDisabled()
    {
        File.WriteAllText(statePath, "{ not json");
        Assert.That(new PluginStateStore(statePath).Disabled, Is.Empty);
    }

    [Test]
    public void StateStore_IgnoresUnsafeNamesFromTheFile()
    {
        File.WriteAllText(statePath, """{ "Disabled": ["../escape", "Good"], "PendingRemoval": ["a/b", "..", "Fine"] }""");
        PluginStateStore store = new(statePath);
        Assert.That(store.Disabled, Is.EquivalentTo(Items("Good")));
        Assert.That(store.PendingRemoval, Is.EquivalentTo(Items("Fine")));
    }

    [Test]
    public void StateStore_RejectsUnsafeNames() =>
        Assert.That(() => new PluginStateStore(statePath).SetEnabled("../x", false), Throws.ArgumentException);

    [Test]
    public void ApplyPendingRemovals_DeletesOnlyTheListedFolderAndClearsTheRequest()
    {
        MakePlugin(user, "Keep");
        MakePlugin(user, "Drop");
        PluginStateStore store = new(statePath);
        store.MarkForRemoval("Drop");
        store.SetEnabled("Drop", false);

        store.ApplyPendingRemovals(user);

        Assert.That(Directory.Exists(Path.Combine(user, "Drop")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(user, "Keep")), Is.True);
        Assert.That(new PluginStateStore(statePath).PendingRemoval, Is.Empty);
        Assert.That(new PluginStateStore(statePath).Disabled, Is.Empty);
    }

    [Test]
    public void Install_Folder_CopiesIntoTheUserRoot()
    {
        string source = MakePlugin(Path.Combine(root, "src"), "Sample");

        string name = PluginInstaller.Install(source, user, [bundled]);

        Assert.That(name, Is.EqualTo("Sample"));
        Assert.That(File.Exists(Path.Combine(user, "Sample", "Sample.dll")), Is.True);
        Assert.That(Directory.GetDirectories(user).Select(Path.GetFileName), Is.EqualTo(Items("Sample")));
    }

    [Test]
    public void Install_Zip_ExtractsTheSingleTopLevelFolder()
    {
        string zip = Zip("sample.zip", ("Sample/Sample.dll", "x"), ("Sample/lib/helper.dll", "y"));

        Assert.That(PluginInstaller.Install(zip, user, [bundled]), Is.EqualTo("Sample"));
        Assert.That(File.Exists(Path.Combine(user, "Sample", "lib", "helper.dll")), Is.True);
    }

    [TestCase("../evil/Sample.dll")]
    [TestCase("/abs/Sample.dll")]
    [TestCase("Sample/../../escape.txt")]
    [TestCase("Sample\\Sample.dll")]
    [TestCase("C:/Sample/Sample.dll")]
    public void Install_Zip_UnsafeEntryIsRejectedAndNothingRemains(string entry)
    {
        string zip = Zip("bad.zip", (entry, "x"));

        Assert.That(() => PluginInstaller.Install(zip, user, [bundled]), Throws.InstanceOf<InvalidDataException>());
        Assert.That(Directory.GetFileSystemEntries(user), Is.Empty);
        Assert.That(File.Exists(Path.Combine(root, "escape.txt")), Is.False);
    }

    [Test]
    public void Install_Zip_TwoTopLevelFolders_IsRejected() =>
        Assert.That(() => PluginInstaller.Install(Zip("two.zip", ("A/A.dll", "x"), ("B/B.dll", "x")), user, [bundled]),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("exactly one"));

    [Test]
    public void Install_MissingEntryAssembly_IsRejected()
    {
        string source = Path.Combine(root, "src", "Empty");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "x");

        Assert.That(() => PluginInstaller.Install(source, user, [bundled]), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("Empty.dll"));
        Assert.That(Directory.GetFileSystemEntries(user), Is.Empty);
    }

    [Test]
    public void Install_NameAlreadyShippedOrInstalled_IsRejected()
    {
        string clash = MakePlugin(Path.Combine(root, "src"), Shipped);
        Assert.That(() => PluginInstaller.Install(clash, user, [bundled]), Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("already installed"));

        string once = MakePlugin(Path.Combine(root, "src2"), "Sample");
        PluginInstaller.Install(once, user, [bundled]);
        Assert.That(() => PluginInstaller.Install(once, user, [bundled]), Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Install_InvalidFolderName_IsRejected()
    {
        string source = MakePlugin(Path.Combine(root, "src"), "bad name");
        Assert.That(() => PluginInstaller.Install(source, user, [bundled]), Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Install_NotAFolderOrZip_IsRejected()
    {
        string file = Path.Combine(root, "plugin.dll");
        File.WriteAllText(file, "x");
        Assert.That(() => PluginInstaller.Install(file, user, [bundled]), Throws.ArgumentException);
    }

    [Test]
    public void Install_FolderContainingALink_IsRejected()
    {
        string source = MakePlugin(Path.Combine(root, "src"), "Linked");
        try { File.CreateSymbolicLink(Path.Combine(source, "link.dll"), Path.Combine(source, "Linked.dll")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { Assert.Ignore("Links are not available here."); }

        Assert.That(() => PluginInstaller.Install(source, user, [bundled]), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("links"));
    }

    [Test]
    public void Install_PendingRemoval_AsksForARestart()
    {
        string source = MakePlugin(Path.Combine(root, "src"), "Sample");
        Assert.That(() => PluginInstaller.Install(source, user, [bundled], new HashSet<string> { "Sample" }),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("restart"));
    }

    [Test]
    public async Task Administration_ListShowsBundledInstalledAndFailedPluginsWithPendingChanges()
    {
        string broken = Path.Combine(user, "Broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "Broken.dll"), "not an assembly");
        PluginStateStore store = new(statePath);
        PluginAdministration admin = Create(store);

        string installed = await admin.InstallAsync(MakePlugin(Path.Combine(root, "src"), "Fresh"));
        admin.SetEnabled(Shipped, false);

        IReadOnlyList<PluginEntry> entries = admin.List();
        Assert.That(installed, Is.EqualTo("Fresh"));
        PluginEntry musicBrainz = entries.Single(entry => entry.Folder == Shipped);
        Assert.That(musicBrainz, Has.Property("IsRunning").True.And.Property("EnabledNext").False.And.Property("IsUserInstalled").False);
        Assert.That(entries.Single(entry => entry.Folder == "Broken").Failure, Is.Not.Null);
        PluginEntry fresh = entries.Single(entry => entry.Folder == "Fresh");
        Assert.That(fresh, Has.Property("IsRunning").False.And.Property("EnabledNext").True.And.Property("IsUserInstalled").True);
        Assert.That(entries[0].IsUserInstalled, Is.False, "Bundled plugins are listed first.");
    }

    [Test]
    public void Administration_Uninstall_OnlyForUserPluginsAndTakesEffectAfterRestart()
    {
        MakePlugin(user, "Mine");
        PluginAdministration admin = Create(new PluginStateStore(statePath));

        Assert.That(() => admin.Uninstall(Shipped), Throws.InstanceOf<InvalidOperationException>());
        Assert.That(() => admin.Uninstall("../Mine"), Throws.ArgumentException);
        admin.Uninstall("Mine");

        Assert.That(Directory.Exists(Path.Combine(user, "Mine")), Is.True, "Files are removed at the next start, not while running.");
        Assert.That(admin.List().Single(entry => entry.Folder == "Mine").RemovalPending, Is.True);
        new PluginStateStore(statePath).ApplyPendingRemovals(user);
        Assert.That(Directory.Exists(Path.Combine(user, "Mine")), Is.False);
    }

    private PluginAdministration Create(PluginStateStore store)
    {
        Directory.CreateDirectory(user);
        PluginLoadReport report = PluginLoader.LoadAll([new PluginSource(bundled, false), new PluginSource(user, true)],
            new PluginContext("unused"), store.Disabled);
        return new PluginAdministration(bundled, user, store, report);
    }

    private static string MakePlugin(string parent, string name)
    {
        string folder = Path.Combine(parent, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".dll"), "not a real assembly");
        return folder;
    }

    private string Zip(string fileName, params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(user);
        string path = Path.Combine(root, fileName);
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach ((string name, string content) in entries)
            {
                using StreamWriter writer = new(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        return path;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }

    private static T[] Items<T>(params T[] items) => items;
}
