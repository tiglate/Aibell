using Aibell.Application.Plugins;
using Aibell.Infrastructure.Plugins;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Plugins;

[TestFixture]
public sealed class PluginLoaderTests
{
    private static readonly PluginContext Context = new("unused");
    private string root = "";

    [SetUp]
    public void CreateRoot() => root = Directory.CreateTempSubdirectory("aibell-plugins").FullName;

    [TearDown]
    public void DeleteRoot()
    {
        // Windows keeps loaded plugin assemblies locked; the temp folder is disposable.
        try { Directory.Delete(root, recursive: true); }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static string ShippedPlugins => Path.Combine(AppContext.BaseDirectory, "plugins");

    [Test]
    public void LoadAll_MissingFolder_ReturnsNoPluginsAndNoFailures()
    {
        PluginLoadReport report = PluginLoader.LoadAll(Path.Combine(root, "absent"), Context);
        Assert.That(report.Plugins, Is.Empty);
        Assert.That(report.Failures, Is.Empty);
    }

    [Test]
    public void LoadAll_NullFolder_ReturnsNothing() =>
        Assert.That(PluginLoader.LoadAll(null, Context).Plugins, Is.Empty);

    [Test]
    public void LoadAll_ShippedMusicBrainzPlugin_LoadsAndContributesLookupProviders()
    {
        PluginLoadReport report = PluginLoader.LoadAll(ShippedPlugins, Context);

        Assert.That(report.Failures, Is.Empty);
        LoadedPlugin plugin = report.Plugins.Single();
        Assert.That(plugin.Id, Is.EqualTo("musicbrainz"));
        // These casts only succeed when host and plugin share one copy of the contract assembly.
        Assert.That(plugin.Contributions.ReleaseProviders, Has.Count.EqualTo(1));
        Assert.That(plugin.Contributions.ArtistProviders, Has.Count.EqualTo(1));
        Assert.That(plugin.Contributions.EnrichmentProviders, Has.Count.EqualTo(1));
    }

    [Test]
    public void LoadAll_BrokenAssembly_ReportsFailureAndKeepsOtherPlugins()
    {
        string broken = Directory.CreateDirectory(Path.Combine(root, "Broken")).FullName;
        File.WriteAllText(Path.Combine(broken, "Broken.dll"), "this is not an assembly");
        CopyShipped(Path.Combine(root, "Aibell.Metadata.MusicBrainz"));

        PluginLoadReport report = PluginLoader.LoadAll(root, Context);

        Assert.That(report.Failures.Select(failure => failure.Name), Is.EqualTo(Items("Broken")));
        Assert.That(report.Plugins.Select(plugin => plugin.Id), Is.EqualTo(Items("musicbrainz")));
    }

    [Test]
    public void LoadAll_FolderWithoutMatchingAssembly_IsIgnored()
    {
        Directory.CreateDirectory(Path.Combine(root, "Empty"));
        File.WriteAllText(Path.Combine(root, "Empty", "other.dll"), "x");
        PluginLoadReport report = PluginLoader.LoadAll(root, Context);
        Assert.That(report.Plugins, Is.Empty);
        Assert.That(report.Failures, Is.Empty);
    }

    [Test]
    public void LoadAll_AssemblyWithoutEntryPoint_ReportsFailure()
    {
        // The infrastructure assembly itself contains no IAibellPlugin implementation.
        string folder = Directory.CreateDirectory(Path.Combine(root, "Aibell.Domain")).FullName;
        File.Copy(typeof(Aibell.Domain.Media.Track).Assembly.Location, Path.Combine(folder, "Aibell.Domain.dll"));

        PluginLoadReport report = PluginLoader.LoadAll(root, Context);

        Assert.That(report.Plugins, Is.Empty);
        Assert.That(report.Failures.Single().Reason, Does.Contain("entry point"));
    }

    private static void CopyShipped(string destination)
    {
        Directory.CreateDirectory(destination);
        string source = Path.Combine(ShippedPlugins, "Aibell.Metadata.MusicBrainz");
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    private static T[] Items<T>(params T[] items) => items;
}
