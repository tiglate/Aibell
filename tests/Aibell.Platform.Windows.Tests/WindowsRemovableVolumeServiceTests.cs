using Aibell.Application.Devices;
using Aibell.Platform.Windows;
using NUnit.Framework;

namespace Aibell.Platform.Windows.Tests;

[TestFixture]
public sealed class WindowsRemovableVolumeServiceTests
{
    [Test]
    public void Describe_UsesTheLabelAndDriveLetter() =>
        Assert.That(WindowsRemovableVolumeService.Describe(@"E:\", "MUSIC", @"E:\", 100, 40, "FAT32"),
            Is.EqualTo(new RemovableVolume(@"E:\", "MUSIC (E:)", @"E:\", 100, 40, "FAT32")));

    [Test]
    public void Describe_NoLabel_UsesTheDriveLetterOnly() =>
        Assert.That(WindowsRemovableVolumeService.Describe(@"F:\", " ", @"F:\", 1, 1, "exFAT").Name, Is.EqualTo("F:"));

    [Test]
    public void List_ReturnsWhatTheReaderFound()
    {
        RemovableVolume stick = new("E:", "E:", @"E:\", 1, 1, "FAT32");
        Assert.That(new WindowsRemovableVolumeService(() => [stick]).List(), Is.EqualTo(new[] { stick }));
    }

    [Test]
    public void List_WithTheRealSystem_DoesNotThrow() => Assert.That(() => new WindowsRemovableVolumeService().List(), Throws.Nothing);
}
