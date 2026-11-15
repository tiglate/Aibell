using Aibell.Mpd.Config;

namespace Aibell.Mpd.Tests;

[TestFixture]
public sealed class MpdConfigFileTests
{
    private string directory = string.Empty;

    [SetUp]
    public void CreateDirectory() => directory = Directory.CreateTempSubdirectory("aibell-mpd-").FullName;

    [TearDown]
    public void DeleteDirectory() => Directory.Delete(directory, recursive: true);

    [Test]
    public void Candidates_FollowMpdsSearchOrder()
    {
        Assert.That(MpdConfigFile.Candidates("/cfg", "/home/a"), Is.EqualTo(new[] { "/cfg/mpd/mpd.conf", "/home/a/.mpdconf", "/home/a/.mpd/mpd.conf", "/etc/mpd.conf" }));
        Assert.That(MpdConfigFile.Candidates(null, "/home/a")[0], Is.EqualTo("/home/a/.config/mpd/mpd.conf"));
        Assert.That(MpdConfigFile.Candidates("", ""), Is.EqualTo(new[] { "/etc/mpd.conf" }));
        Assert.That(MpdConfigFile.DefaultNewPath("/cfg", "/home/a"), Is.EqualTo("/cfg/mpd/mpd.conf"));
    }

    [Test]
    public void FindExisting_ReturnsTheFirstFileThatExists()
    {
        string home = Path.Combine(directory, "home");
        Directory.CreateDirectory(home);
        Assert.That(MpdConfigFile.FindExisting(Path.Combine(directory, "cfg"), home), Is.Null.Or.EqualTo("/etc/mpd.conf"));
        File.WriteAllText(Path.Combine(home, ".mpdconf"), "port \"1\"\n");
        Assert.That(MpdConfigFile.FindExisting(Path.Combine(directory, "cfg"), home), Is.EqualTo(Path.Combine(home, ".mpdconf")));
    }

    [Test]
    public void Save_WritesBacksUpAndKeepsPermissions()
    {
        string path = Path.Combine(directory, "sub", "mpd.conf");
        MpdConfigDocument document = MpdConfigDocument.CreateEmpty();
        document.Set("port", "6600");
        Assert.That(MpdConfigFile.Save(document, path), Is.Null, "nothing to back up yet");
        Assert.That(File.ReadAllText(path), Is.EqualTo("port \"6600\"\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        MpdConfigDocument edited = MpdConfigFile.Load(path);
        edited.Set("port", "6601");
        DateTimeOffset when = new(2026, 11, 15, 10, 30, 0, TimeSpan.Zero);
        string? backup = MpdConfigFile.Save(edited, path, when);
        Assert.That(backup, Is.EqualTo($"{path}.bak-20261115-103000"));
        Assert.That(File.ReadAllText(backup!), Is.EqualTo("port \"6600\"\n"));
        Assert.That(File.ReadAllText(path), Is.EqualTo("port \"6601\"\n"));
        string? second = MpdConfigFile.Save(edited, path, when);
        Assert.That(second, Is.EqualTo($"{path}.bak-20261115-103000-2"));
        if (!OperatingSystem.IsWindows()) Assert.That(File.GetUnixFileMode(path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"), Is.Empty);
    }

    [Test]
    public void Save_RefusesAnUnclosedBlock()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse("audio_output {\n");
        Assert.Throws<InvalidOperationException>(() => MpdConfigFile.Save(document, Path.Combine(directory, "x.conf")));
        Assert.Throws<ArgumentNullException>(() => MpdConfigFile.Save(null!, "x"));
        Assert.Throws<ArgumentException>(() => MpdConfigFile.Save(document, ""));
    }

    [Test]
    public void Load_ReadsUtf8WithoutLosingText()
    {
        string path = Path.Combine(directory, "mpd.conf");
        File.WriteAllText(path, "zeroconf_name \"Música ♪\"\n");
        Assert.That(MpdConfigFile.Load(path).Get("zeroconf_name"), Is.EqualTo("Música ♪"));
        Assert.Throws<ArgumentException>(() => MpdConfigFile.Load(""));
    }

    [Test]
    public void CanWrite_ReflectsPermissions()
    {
        string path = Path.Combine(directory, "mpd.conf");
        Assert.That(MpdConfigFile.CanWrite(path), Is.True, "a new file in a writable folder");
        Assert.That(MpdConfigFile.CanWrite(Path.Combine(directory, "a", "b", "c.conf")), Is.True, "missing folders are created on save");
        File.WriteAllText(path, "x");
        Assert.That(MpdConfigFile.CanWrite(path), Is.True);
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        Assert.That(MpdConfigFile.CanWrite(path), Is.False);
        Assert.That(MpdConfigFile.CanWrite("/etc/mpd.conf"), Is.False);
        Assert.Throws<ArgumentException>(() => MpdConfigFile.CanWrite(""));
    }
}
