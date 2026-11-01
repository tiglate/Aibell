using Aibell.Platform.Linux;
using NUnit.Framework;

namespace Aibell.Platform.Linux.Tests;

[TestFixture]
public sealed class LinuxRemovableVolumeServiceTests
{
    private const string MountInfo = """
        22 1 8:2 / / rw,relatime shared:1 - ext4 /dev/sda2 rw
        45 22 8:17 / /media/daniel/MY\040STICK rw,nosuid,nodev,relatime shared:50 - vfat /dev/sdb1 rw,fmask=0022
        46 22 8:33 / /run/media/daniel/PLAYER rw,nosuid shared:51 - exfat /dev/sdc1 rw
        47 22 0:44 / /media/daniel/backup rw - ext4 /dev/sdd1 rw
        48 22 0:50 / /mnt/data rw - ext4 /dev/sde1 rw
        49 22 0:51 / /media/cdrom rw - iso9660 /dev/sr0 ro
        50 22 0:52 / /media/daniel/share rw - nfs4 server:/share rw
        51 22 0:53 / /media/daniel/sticktwo rw - vfat /dev/sdf1 rw
        52 22 0:53 / /media/daniel/sticktwo rw - vfat /dev/sdf1 rw
        53 22 0:54 / /home/daniel/bind rw - vfat /dev/sdg1 rw
        garbage line
        """;

    [Test]
    public void Parse_KeepsOnlyDesktopMountedMusicFileSystems()
    {
        var found = LinuxRemovableVolumeService.Parse(MountInfo).ToArray();

        Assert.That(found.Select(item => item.MountPoint), Is.EqualTo(Items("/media/daniel/MY STICK", "/run/media/daniel/PLAYER", "/media/daniel/backup", "/media/daniel/sticktwo")));
        Assert.That(found.Select(item => item.FileSystem), Is.EqualTo(Items("vfat", "exfat", "ext4", "vfat")));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Parse_NothingToRead_ReturnsNothing(string? text) => Assert.That(LinuxRemovableVolumeService.Parse(text), Is.Empty);

    [Test]
    public void Parse_RejectsTraversalInMountPoints() =>
        Assert.That(LinuxRemovableVolumeService.Parse("1 1 8:1 / /media/../etc/x rw - vfat /dev/x rw"), Is.Empty);

    [TestCase(@"a\040b", "a b")]
    [TestCase(@"tab\011x", "tab\tx")]
    [TestCase(@"back\134slash", @"back\slash")]
    [TestCase(@"plain", "plain")]
    [TestCase(@"trailing\", @"trailing\")]
    [TestCase(@"short\04", @"short\04")]
    public void Unescape_DecodesOctalSequences(string input, string expected) =>
        Assert.That(LinuxRemovableVolumeService.Unescape(input), Is.EqualTo(expected));

    [Test]
    public void List_ReportsNameSizeAndFreeSpace_AndSkipsUnreadyMounts()
    {
        LinuxRemovableVolumeService service = new(() => MountInfo, mount => mount.EndsWith("PLAYER", StringComparison.Ordinal) ? null : (1000, 400));

        var volumes = service.List();

        Assert.That(volumes.Select(volume => volume.Name), Is.EqualTo(Items("MY STICK", "backup", "sticktwo")));
        Assert.That(volumes[0], Has.Property("RootPath").EqualTo("/media/daniel/MY STICK").And.Property("TotalBytes").EqualTo(1000).And.Property("FreeBytes").EqualTo(400)
            .And.Property("FileSystem").EqualTo("vfat"));
    }

    [Test]
    public void List_WithTheRealSystem_DoesNotThrow() => Assert.That(() => new LinuxRemovableVolumeService().List(), Throws.Nothing);

    [Test]
    public void ReadSpace_ExistingFolder_ReportsTheVolumeSize()
    {
        (long Total, long Free)? space = LinuxRemovableVolumeService.ReadSpace(Path.GetTempPath());

        Assert.That(space, Is.Not.Null);
        Assert.That(space!.Value.Total, Is.GreaterThan(0));
        Assert.That(space.Value.Free, Is.InRange(0, space.Value.Total));
    }

    [Test]
    public void ReadSpace_MissingFolder_ReturnsNothing()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("DriveInfo resolves any path to its drive letter on Windows.");
        Assert.That(LinuxRemovableVolumeService.ReadSpace(Path.Combine(Path.GetTempPath(), "aibell-missing-" + Guid.NewGuid().ToString("N"))), Is.Null);
    }

    private static T[] Items<T>(params T[] items) => items;
}
