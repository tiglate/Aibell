using Aibell.Application.Discs;
using Aibell.Audio.Cdda;
using Aibell.Playback.Core;
using NUnit.Framework;

namespace Aibell.Playback.Miniaudio.Tests;

[TestFixture]
public sealed class DiscPlaybackTests
{
    // Three audio tracks of 100, 200 and 300 sectors after the 150-sector lead-in.
    private static readonly DiscToc Toc = new([150, 250, 450], 750);
    private static readonly Uri Track2 = DiscLocation.Create("/dev/sr0", 2);

    [Test]
    public async Task OpenAsync_DiscTrack_PlaysOnlyThatTracksSectorsAtTheDeviceRate()
    {
        Reader reader = new();
        using IPcmSource source = await new AudioSourceFactory(null, drives: new Drives(Toc), openDisc: _ => reader).OpenAsync(Track2, 48000, CancellationToken.None);

        long frames = 0;
        float[] buffer = new float[4096 * 2];
        int read;
        while ((read = source.Read(buffer, 4096)) > 0) frames += read;

        Assert.That(frames, Is.EqualTo(200 * CddaDecoder.FramesPerSector * 48000.0 / 44100).Within(64), "the resampler holds back a few frames at the end");
        Assert.That(reader.Reads.Min(), Is.EqualTo(100), "track 2 starts at sector 250 - 150");
        Assert.That(reader.Reads.Max(), Is.EqualTo(299), "and ends before track 3");
        Assert.That(reader.Disposed, Is.False);
    }

    [Test]
    public async Task OpenAsync_DiscTrack_DisposingTheSourceClosesTheDrive()
    {
        Reader reader = new();
        IPcmSource source = await new AudioSourceFactory(null, drives: new Drives(Toc), openDisc: _ => reader).OpenAsync(Track2, 44100, CancellationToken.None);

        source.Dispose();

        Assert.That(reader.Disposed, Is.True);
    }

    [Test]
    public async Task Seek_DiscTrack_JumpsInsideTheTrack()
    {
        Reader reader = new();
        using IPcmSource source = await new AudioSourceFactory(null, drives: new Drives(Toc), openDisc: _ => reader).OpenAsync(Track2, 44100, CancellationToken.None);

        Assert.That(source.Seek(100L * CddaDecoder.FramesPerSector), Is.True);
        Assert.That(source.Read(new float[200], 100), Is.EqualTo(100));
        Assert.That(reader.Reads.Last(), Is.EqualTo(100 + 100 + 15), "the last sector of the request that served the seek");
    }

    [Test]
    public async Task OpenAsync_NoDisc_Throws() =>
        await Assert.ThatAsync(() => new AudioSourceFactory(null, drives: new Drives(null), openDisc: _ => new Reader()).OpenAsync(Track2, 44100, CancellationToken.None),
            Throws.InstanceOf<IOException>().With.Message.Contains("no audio disc"));

    [Test]
    public async Task OpenAsync_TrackNotOnTheDisc_Throws() =>
        await Assert.ThatAsync(() => new AudioSourceFactory(null, drives: new Drives(Toc), openDisc: _ => new Reader()).OpenAsync(DiscLocation.Create("/dev/sr0", 4), 44100, CancellationToken.None),
            Throws.InstanceOf<IOException>());

    [Test]
    public async Task OpenAsync_DriveCannotBeOpened_Throws() =>
        await Assert.ThatAsync(() => new AudioSourceFactory(null, drives: new Drives(Toc), openDisc: _ => throw new IOException("busy")).OpenAsync(Track2, 44100, CancellationToken.None),
            Throws.InstanceOf<IOException>());

    [Test]
    public async Task OpenAsync_WithoutDriveService_KeepsSayingChooseVlc() =>
        await Assert.ThatAsync(() => new AudioSourceFactory(null).OpenAsync(Track2, 44100, CancellationToken.None),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("VLC"));

    private sealed class Drives(DiscToc? toc) : IOpticalDriveService
    {
        public IReadOnlyList<OpticalDrive> ListDrives() => [new OpticalDrive("/dev/sr0", "CD drive")];

        public Task<DiscToc?> ReadTocAsync(OpticalDrive drive, CancellationToken cancellationToken = default) => Task.FromResult(toc);
    }

    private sealed class Reader : ICdAudioReader
    {
        public List<int> Reads { get; } = [];

        public bool Disposed { get; private set; }

        public bool ReadSectors(int lba, int count, Span<byte> destination)
        {
            for (int sector = 0; sector < count; sector++) Reads.Add(lba + sector);
            destination[..(count * CddaDecoder.SectorBytes)].Clear();
            return true;
        }

        public void Dispose() => Disposed = true;
    }
}
