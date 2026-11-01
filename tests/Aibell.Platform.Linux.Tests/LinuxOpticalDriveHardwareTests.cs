using Aibell.Application.Discs;
using Aibell.Audio.Cdda;
using NUnit.Framework;

namespace Aibell.Platform.Linux.Tests;

/// <summary>
/// Runs the real kernel ioctls against the first optical drive. Ignored (not failed) on machines without a drive, without
/// read permission on it or without an audio disc inserted, so it is safe in CI; run it by hand with an audio CD in the drive.
/// </summary>
[TestFixture, Category("OpticalDrive")]
public sealed class LinuxOpticalDriveHardwareTests
{
    private LinuxOpticalDriveService service = null!;
    private OpticalDrive drive = null!;
    private DiscToc toc = null!;

    [OneTimeSetUp]
    public async Task FindDisc()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux only.");
        service = new LinuxOpticalDriveService();
        IReadOnlyList<OpticalDrive> drives = service.ListDrives();
        if (drives.Count == 0) Assert.Ignore("No optical drive.");
        drive = drives[0];
        DiscToc? read = await service.ReadTocAsync(drive);
        if (read is null) Assert.Ignore("No readable audio disc in " + drive.Id + ".");
        toc = read;
    }

    [Test]
    public void ReadToc_RealDisc_ListsConsecutiveTracksWithSaneDurations()
    {
        Assert.That(toc.TrackCount, Is.InRange(1, 99));
        Assert.That(toc.TrackOffsets, Is.Ordered.Ascending, "track offsets must increase");
        Assert.That(toc.TrackOffsets[0], Is.GreaterThanOrEqualTo(150), "the first track starts after the 2 second lead-in");
        Assert.That(toc.LeadOutOffset, Is.GreaterThan(toc.TrackOffsets[^1]));
        for (int index = 0; index < toc.TrackCount; index++)
            Assert.That(toc.DurationOf(index), Is.InRange(TimeSpan.FromSeconds(4), TimeSpan.FromMinutes(30)), $"track {index + 1}");
        TestContext.Out.WriteLine($"{drive.DisplayName}: {toc.TrackCount} tracks, total {TimeSpan.FromSeconds((toc.LeadOutOffset - toc.TrackOffsets[0]) / (double)DiscToc.SectorsPerSecond):mm\\:ss}");
    }

    [Test]
    public async Task ReadToc_CalledTwice_GivesTheSameAnswer()
    {
        DiscToc? again = await service.ReadTocAsync(drive);

        Assert.That(again?.TrackOffsets, Is.EqualTo(toc.TrackOffsets));
        Assert.That(again?.LeadOutOffset, Is.EqualTo(toc.LeadOutOffset));
    }

    [Test]
    public async Task ReadToc_CancelledBeforeStart_Throws()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThatAsync(() => service.ReadTocAsync(drive, cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task ReadToc_UnknownDevice_MeansNoDisc() =>
        Assert.That(await service.ReadTocAsync(new OpticalDrive("/dev/sr99", "missing")), Is.Null);

    [Test]
    public void ReadAudio_FirstTrackOfARealDisc_IsRealAudioOfTheTocLength()
    {
        (int start, int end) = toc.SectorRangeOf(0);
        using CddaDecoder decoder = new(CdAudioReader.Open(drive.Id), start, end);
        float[] samples = new float[CddaDecoder.Rate * 2];

        // Ten seconds from the middle of the track: music, so not silent and not clipped noise.
        Assert.That(decoder.Seek(decoder.TotalFrames / 2), Is.True);
        double energy = 0;
        long frames = 0;
        for (int second = 0; second < 10; second++)
        {
            int read = decoder.Read(samples, CddaDecoder.Rate);
            for (int index = 0; index < read * 2; index++) energy += samples[index] * (double)samples[index];
            frames += read;
        }

        Assert.That(frames, Is.EqualTo(10 * CddaDecoder.Rate).Within(CddaDecoder.Rate), "the track is long enough");
        double rms = Math.Sqrt(energy / (frames * 2));
        TestContext.Out.WriteLine($"RMS over 10 s from the middle of track 1: {rms:F4}");
        Assert.That(rms, Is.InRange(0.001, 0.9));
    }
}
