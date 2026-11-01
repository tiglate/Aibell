using Aibell.Desktop.Services;
using NUnit.Framework;

namespace Aibell.Desktop.Tests;

[TestFixture]
public sealed class StartupFilesTests
{
    private string directory = "";

    [SetUp]
    public void CreateDirectory() => Directory.CreateDirectory(directory = Path.Combine(Path.GetTempPath(), "aibell-startup-" + Guid.NewGuid().ToString("N")));

    [TearDown]
    public void DeleteDirectory() => Directory.Delete(directory, recursive: true);

    [Test]
    public void Resolve_KeepsExistingFilesAndMakesRelativePathsAbsolute()
    {
        File.WriteAllText(Path.Combine(directory, "song.mp3"), "x");
        IReadOnlyList<Uri> files = StartupFiles.Resolve(["song.mp3", "missing.mp3", directory, "--verbose", " "], directory);
        Assert.That(files.Select(file => file.LocalPath), Is.EqualTo(new[] { Path.Combine(directory, "song.mp3") }));
    }

    [Test]
    public void Resolve_AcceptsFileUris()
    {
        string path = Path.Combine(directory, "a song.flac");
        File.WriteAllText(path, "x");
        IReadOnlyList<Uri> files = StartupFiles.Resolve([new Uri(path).AbsoluteUri], "/");
        Assert.That(files.Select(file => file.LocalPath), Is.EqualTo(new[] { path }));
    }
}
