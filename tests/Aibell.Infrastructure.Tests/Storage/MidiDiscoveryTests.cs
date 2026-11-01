using Aibell.Infrastructure.Files;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Storage;

[TestFixture]
public sealed class MidiDiscoveryTests
{
    private static readonly string[] EmptyQuery = [""];
    private static readonly string[] ExpectedRsnEntries = ["?entry=song_01.spc", "?entry=song_02.spc", "?entry=song_03.spc"];
    private static readonly string[] ExpectedMidiNames = ["a.mid", "b.MIDI", "c.rmi"];
    private static readonly string[] ExpectedAiffNames = ["a.aiff", "b.AIF", "c.aifc"];

    [Test]
    public async Task Discover_FindsSpcFiles()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (string name in (string[])["a.spc", "b.SPC", "c.txt"]) File.WriteAllBytes(Path.Combine(directory, name), [0]);

            List<string> found = [];
            await foreach (Uri file in new FileSystemMediaDiscovery().DiscoverAsync([directory])) found.Add(Path.GetFileName(file.LocalPath));

            Assert.That(found, Is.EquivalentTo((string[])["a.spc", "b.SPC"]));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestCase("songs4.rsn")]
    [TestCase("songs5.rsn")]
    public async Task Discover_ExpandsAnRsnArchiveIntoOneLocationPerSpc(string fixture)
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rar", fixture), Path.Combine(directory, "game.rsn"));

            List<Uri> found = [];
            await foreach (Uri file in new FileSystemMediaDiscovery().DiscoverAsync([directory])) found.Add(file);

            Assert.That(found, Has.Count.EqualTo(3));
            Assert.That(found.Distinct().Count(), Is.EqualTo(3), "the locations must differ under Uri equality");
            Assert.That(found.Select(location => Uri.UnescapeDataString(location.Query)), Is.EqualTo(ExpectedRsnEntries));
            Assert.That(found.Select(location => Path.GetFileName(location.LocalPath)), Is.All.EqualTo("game.rsn"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Discover_ReturnsADamagedRsnAsItIs()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "broken.rsn"), new byte[100]);

            List<Uri> found = [];
            await foreach (Uri file in new FileSystemMediaDiscovery().DiscoverAsync([directory])) found.Add(file);

            Assert.That(found.Select(location => location.Query), Is.EqualTo(EmptyQuery));
            Assert.That(Path.GetFileName(found[0].LocalPath), Is.EqualTo("broken.rsn"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Discover_FindsMidiFiles()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (string name in (string[])["a.mid", "b.MIDI", "c.rmi", "d.txt"]) File.WriteAllBytes(Path.Combine(directory, name), [0]);

            List<string> found = [];
            await foreach (Uri file in new FileSystemMediaDiscovery().DiscoverAsync([directory])) found.Add(Path.GetFileName(file.LocalPath));

            Assert.That(found, Is.EquivalentTo(ExpectedMidiNames));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Discover_FindsAiffFiles()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (string name in (string[])["a.aiff", "b.AIF", "c.aifc", "d.txt"]) File.WriteAllBytes(Path.Combine(directory, name), [0]);

            List<string> found = [];
            await foreach (Uri file in new FileSystemMediaDiscovery().DiscoverAsync([directory])) found.Add(Path.GetFileName(file.LocalPath));

            Assert.That(found, Is.EquivalentTo(ExpectedAiffNames));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
