using Aibell.Application.Discs;
using NUnit.Framework;

namespace Aibell.Platform.Windows.Tests;

[TestFixture]
public sealed class WindowsOpticalDriveServiceTests
{
    private static byte[] Toc(params (byte Track, byte Control, byte M, byte S, byte F)[] entries)
    {
        byte[] buffer = new byte[4 + entries.Length * 8];
        int length = 2 + entries.Length * 8;
        buffer[0] = (byte)(length >> 8);
        buffer[1] = (byte)length;
        buffer[2] = 1;
        buffer[3] = (byte)(entries.Length - 1);
        for (int index = 0; index < entries.Length; index++)
        {
            int at = 4 + index * 8;
            buffer[at + 1] = entries[index].Control;
            buffer[at + 2] = entries[index].Track;
            buffer[at + 5] = entries[index].M;
            buffer[at + 6] = entries[index].S;
            buffer[at + 7] = entries[index].F;
        }
        return buffer;
    }

    [Test]
    public void ParseToc_AudioDisc_ConvertsMinuteSecondFrameToSectors()
    {
        // 00:02:00 = 150 sectors, 03:24:33 = 15363, lead-out 21:12:62 = 95462? Use a smaller lead-out: 10:00:00 = 45000.
        DiscToc? toc = WindowsOpticalDriveService.ParseToc(Toc((1, 0, 0, 2, 0), (2, 0, 3, 24, 63), (0xAA, 0, 10, 0, 0)));

        Assert.That(toc, Is.Not.Null);
        Assert.That(toc!.TrackOffsets, Is.EqualTo(new[] { 150, 3 * 4500 + 24 * 75 + 63 }));
        Assert.That(toc.LeadOutOffset, Is.EqualTo(45000));
    }

    [Test]
    public void ParseToc_DataTrackBit_ExcludesThatTrack()
    {
        DiscToc? toc = WindowsOpticalDriveService.ParseToc(Toc((1, 0, 0, 2, 0), (2, 4, 6, 0, 0), (0xAA, 0, 20, 0, 0)));
        Assert.That(toc?.TrackCount, Is.EqualTo(1));
    }

    [Test]
    public void ParseToc_NoLeadOut_ReturnsNull() =>
        Assert.That(WindowsOpticalDriveService.ParseToc(Toc((1, 0, 0, 2, 0), (2, 0, 3, 0, 0))), Is.Null);

    [Test]
    public void ParseToc_TruncatedBuffer_ReturnsNull() =>
        Assert.That(WindowsOpticalDriveService.ParseToc(new byte[6]), Is.Null);

    [Test]
    public void ParseToc_ZeroLengthHeader_ReturnsNull() =>
        Assert.That(WindowsOpticalDriveService.ParseToc(new byte[12]), Is.Null);

    [Test]
    public void ParseToc_MoreEntriesThanTheLimit_StopsAtOneHundredTracks()
    {
        (byte, byte, byte, byte, byte)[] entries = new (byte, byte, byte, byte, byte)[120];
        for (int index = 0; index < entries.Length; index++) entries[index] = ((byte)(index % 99 + 1), 0, 0, 2, 0);
        Assert.DoesNotThrow(() => WindowsOpticalDriveService.ParseToc(Toc(entries)));
    }

    [Test]
    public void ListDrives_OnThisMachine_ReturnsOnlyDriveLetters()
    {
        IReadOnlyList<OpticalDrive> drives = new WindowsOpticalDriveService().ListDrives();
        Assert.That(drives.Select(drive => drive.Id), Has.All.Matches<string>(id => id is { Length: 2 } && id[1] == ':'));
    }

    [Test]
    public void ReadTocAsync_NullDrive_Throws() =>
        Assert.Throws<ArgumentNullException>(() => _ = new WindowsOpticalDriveService().ReadTocAsync(null!));

    [Test]
    public void ReadTocAsync_CancelledToken_Throws()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            _ = new WindowsOpticalDriveService().ReadTocAsync(new OpticalDrive("D:", "drive"), cancellation.Token));
    }

    [TestCase("")]
    [TestCase("D")]
    [TestCase("D:\\")]
    [TestCase("1:")]
    [TestCase("\\\\.\\PhysicalDrive0")]
    public async Task ReadTocAsync_MalformedDriveId_MeansNoDisc(string id) =>
        Assert.That(await new WindowsOpticalDriveService().ReadTocAsync(new OpticalDrive(id, "drive")), Is.Null);

    [Test]
    public async Task ReadTocAsync_DriveThatIsNotOptical_MeansNoDisc()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows only.");
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        Assert.That(await new WindowsOpticalDriveService().ReadTocAsync(new OpticalDrive(systemDrive, "system")), Is.Null);
    }

    [Test]
    public void ParseToc_LengthLargerThanBuffer_DoesNotReadPastIt() =>
        Assert.DoesNotThrow(() => WindowsOpticalDriveService.ParseToc(new byte[] { 0xFF, 0xFF, 1, 1, 0, 0, 1, 0, 0, 0, 2, 0 }));
}
