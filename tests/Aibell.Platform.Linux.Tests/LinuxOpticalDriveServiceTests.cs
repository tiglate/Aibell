using Aibell.Application.Discs;
using NUnit.Framework;

namespace Aibell.Platform.Linux.Tests;

[TestFixture]
public sealed class LinuxOpticalDriveServiceTests
{
    private const string KernelInfo = "CD-ROM information, Id: cdrom.c 3.20\n\ndrive name:\t\tsr1\tsr0\ndrive speed:\t\t0\t0\n";

    [Test]
    public void ListDrives_ParsesKernelDriveNames() =>
        Assert.That(new LinuxOpticalDriveService(() => KernelInfo).ListDrives().Select(drive => drive.Id), Is.EqualTo(Items("/dev/sr1", "/dev/sr0")));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("CD-ROM information\n\ndrive name:\n")]
    public void ListDrives_NoDrives_ReturnsEmpty(string? text) =>
        Assert.That(new LinuxOpticalDriveService(() => text).ListDrives(), Is.Empty);

    [Test]
    public void ListDrives_IgnoresNamesThatCouldEscapeTheDeviceFolder() =>
        Assert.That(LinuxOpticalDriveService.ParseDriveInfo("drive name:\t../etc/passwd sr0\n").Select(drive => drive.Id), Is.EqualTo(Items("/dev/sr0")));

    [Test]
    public void DecodeEntry_AudioTrack_AddsTheLeadIn()
    {
        byte[] entry = new byte[LinuxOpticalDriveService.TocEntrySize];
        entry[0] = 2;
        entry[1] = 0x10; // adr 0, control 1 (audio with pre-emphasis)
        BitConverter.GetBytes(15213).CopyTo(entry, 4);

        RawDiscTrack track = LinuxOpticalDriveService.DecodeEntry(entry, 2);

        Assert.That(track, Is.EqualTo(new RawDiscTrack(2, false, 15363)));
    }

    [Test]
    public void DecodeEntry_DataControlBit_IsDataTrack()
    {
        byte[] entry = new byte[LinuxOpticalDriveService.TocEntrySize];
        entry[1] = 0x40;
        Assert.That(LinuxOpticalDriveService.DecodeEntry(entry, 3).IsData, Is.True);
    }

    [Test]
    public async Task ReadTocAsync_DeviceOutsideDev_ReturnsNull() =>
        Assert.That(await new LinuxOpticalDriveService().ReadTocAsync(new OpticalDrive("/etc/passwd", "x")), Is.Null);

    [Test]
    public async Task ReadTocAsync_MissingDevice_ReturnsNull() =>
        Assert.That(await new LinuxOpticalDriveService().ReadTocAsync(new OpticalDrive("/dev/aibell-no-such-drive", "x")), Is.Null);

    private static T[] Items<T>(params T[] items) => items;

    [Test]
    public void ReadToc_AudioDisc_BuildsTheTableOfContents()
    {
        DiscToc? toc = LinuxOpticalDriveService.ReadToc("/dev/sr0", _ => new FakeCdrom((1, 2), new() { [1] = new(1, false, 150), [2] = new(2, false, 15363), [0xAA] = new(0xAA, false, 30000) }));

        Assert.That(toc!.TrackOffsets, Is.EqualTo(Items(150, 15363)));
        Assert.That(toc.LeadOutOffset, Is.EqualTo(30000));
    }

    [Test]
    public void ReadToc_FakeIsDisposed()
    {
        FakeCdrom fake = new((1, 1), new() { [1] = new(1, false, 150), [0xAA] = new(0xAA, false, 9000) });
        LinuxOpticalDriveService.ReadToc("/dev/sr0", _ => fake);
        Assert.That(fake.Disposed, Is.True);
    }

    [Test]
    public void ReadToc_EmptyDrive_ReturnsNull() =>
        Assert.That(LinuxOpticalDriveService.ReadToc("/dev/sr0", _ => new FakeCdrom(null, [])), Is.Null);

    [TestCase(0, 3)]
    [TestCase(3, 2)]
    [TestCase(1, 100)]
    public void ReadToc_ImplausibleHeader_ReturnsNull(int first, int last) =>
        Assert.That(LinuxOpticalDriveService.ReadToc("/dev/sr0", _ => new FakeCdrom((first, last), [])), Is.Null);

    [Test]
    public void ReadToc_EntryRefused_ReturnsNull() =>
        Assert.That(LinuxOpticalDriveService.ReadToc("/dev/sr0", _ => new FakeCdrom((1, 2), new() { [1] = new(1, false, 150) })), Is.Null);

    [Test]
    public void ReadToc_NoLeadOut_ReturnsNull() =>
        Assert.That(LinuxOpticalDriveService.ReadToc("/dev/sr0", _ => new FakeCdrom((1, 1), new() { [1] = new(1, false, 150) })), Is.Null);

    [Test]
    public void ReadToc_OpenFails_ReturnsNull() =>
        Assert.That(LinuxOpticalDriveService.ReadToc("/dev/sr0", _ => throw new IOException("no medium")), Is.Null);

    [Test]
    public async Task ReadTocAsync_DeviceThatIsNotACdDrive_RefusesTheRequestAndReturnsNull() =>
        Assert.That(await new LinuxOpticalDriveService().ReadTocAsync(new OpticalDrive("/dev/null", "not a drive")), Is.Null);

    [Test]
    public void ListDrives_ReadsTheRealKernelFileWithoutThrowing() =>
        Assert.DoesNotThrow(() => new LinuxOpticalDriveService().ListDrives());

    private sealed class FakeCdrom((int, int)? header, Dictionary<byte, RawDiscTrack> entries) : LinuxOpticalDriveService.ICdromControl
    {
        public bool Disposed { get; private set; }
        public (int First, int Last)? ReadHeader() => header is { } value ? (value.Item1, value.Item2) : null;
        public RawDiscTrack? ReadEntry(byte track) => entries.TryGetValue(track, out RawDiscTrack entry) ? entry : null;
        public void Dispose() => Disposed = true;
    }
}
