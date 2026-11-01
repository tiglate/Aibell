using Aibell.Infrastructure.Podcasts;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Podcasts;

[TestFixture]
public sealed class PodcastDownloadStoreTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"aibell-podcast-store-{Guid.NewGuid():N}")).FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    [Platform("Linux")]
    public void Store_LinkedRoot_RejectsWritesAndReadsOutsideItsOwnedDirectory()
    {
        string outside = Directory.CreateDirectory(Path.Combine(directory, "outside")).FullName;
        string root = Path.Combine(directory, "podcasts");
        Directory.CreateSymbolicLink(root, outside);
        Guid id = Guid.NewGuid();
        string externalFile = Path.Combine(outside, "episode.mp3");
        File.WriteAllText(externalFile, "Keep this file");
        PodcastDownloadStore store = new(root);

        Assert.Multiple(() =>
        {
            Assert.That(() => store.PrepareDestination(id, "episode.mp3"), Throws.TypeOf<IOException>());
            Assert.That(store.ResolveExisting(Path.Combine(root, "episode.mp3")), Is.Null);
            Assert.That(Directory.GetDirectories(outside), Is.Empty);
            Assert.That(File.ReadAllText(externalFile), Is.EqualTo("Keep this file"));
        });
    }

    [Test]
    public void ResolveExisting_MalformedPersistedPath_ReturnsMissing()
    {
        PodcastDownloadStore store = new(Path.Combine(directory, "podcasts"));
        Assert.That(store.ResolveExisting(Path.Combine(directory, "bad\0path")), Is.Null);
    }

    [TestCase("../escape.mp3")]
    [TestCase("..\\escape.mp3")]
    [TestCase("C:escape.mp3")]
    [TestCase("bad\0name.mp3")]
    public void PrepareDestination_InvalidPortableFileName_DoesNotCreateFolders(string fileName)
    {
        string root = Path.Combine(directory, "podcasts");
        PodcastDownloadStore store = new(root);
        Assert.That(() => store.PrepareDestination(Guid.NewGuid(), fileName), Throws.TypeOf<IOException>());
        Assert.That(Directory.Exists(root), Is.False);
    }
}
