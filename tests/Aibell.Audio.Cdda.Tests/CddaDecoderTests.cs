using System.Runtime.InteropServices;

namespace Aibell.Audio.Cdda.Tests;

[TestFixture]
public sealed class CddaDecoderTests
{
    private const int Frames = CddaDecoder.FramesPerSector;

    private static float Expected(long frame) => FakeDisc.Left(frame) / 32768f;

    [Test]
    public void Properties_DescribeRedBookAudio()
    {
        using CddaDecoder decoder = new(new FakeDisc(100), 10, 40);

        Assert.That(decoder.SampleRate, Is.EqualTo(44100));
        Assert.That(decoder.Channels, Is.EqualTo(2));
        Assert.That(decoder.TotalFrames, Is.EqualTo(30L * Frames));
    }

    [Test]
    public void Read_WholeTrack_ReturnsEverySampleInOrderAcrossChunkBoundaries()
    {
        // 40 sectors: more than two 16-sector requests, the last one short.
        using CddaDecoder decoder = new(new FakeDisc(100), 10, 50);
        float[] samples = new float[1000 * 2];
        long position = 0;

        int read;
        while ((read = decoder.Read(samples, 1000)) > 0)
        {
            for (int frame = 0; frame < read; frame++)
            {
                Assert.That(samples[frame * 2], Is.EqualTo(Expected(10L * Frames + position + frame)), $"left of frame {position + frame}");
                Assert.That(samples[frame * 2 + 1], Is.EqualTo(-Expected(10L * Frames + position + frame)), $"right of frame {position + frame}");
            }
            position += read;
        }

        Assert.That(position, Is.EqualTo(decoder.TotalFrames));
        Assert.That(decoder.Read(samples, 1000), Is.Zero, "stays at the end");
    }

    [Test]
    public void Read_NeverTouchesSectorsOutsideTheTrack()
    {
        FakeDisc disc = new(100);
        using CddaDecoder decoder = new(disc, 10, 50);
        float[] samples = new float[4096 * 2];

        while (decoder.Read(samples, 4096) > 0) { }

        Assert.That(disc.Requests.Min(request => request.Lba), Is.EqualTo(10));
        Assert.That(disc.Requests.Max(request => request.Lba + request.Count), Is.EqualTo(50));
    }

    [Test]
    public void Seek_MidSector_ContinuesAtThatSample()
    {
        using CddaDecoder decoder = new(new FakeDisc(100), 10, 50);
        long target = 25L * Frames + 123;
        float[] samples = new float[10 * 2];

        Assert.That(decoder.Seek(target), Is.True);
        Assert.That(decoder.Read(samples, 10), Is.EqualTo(10));

        for (int frame = 0; frame < 10; frame++) Assert.That(samples[frame * 2], Is.EqualTo(Expected(10L * Frames + target + frame)));
    }

    [Test]
    public void Seek_BackToStartAfterPlayingToEnd_PlaysAgain()
    {
        using CddaDecoder decoder = new(new FakeDisc(100), 10, 20);
        float[] samples = new float[Frames * 20];
        while (decoder.Read(samples, Frames * 10) > 0) { }

        Assert.That(decoder.Seek(0), Is.True);
        Assert.That(decoder.Read(samples, 4), Is.EqualTo(4));
        Assert.That(samples[0], Is.EqualTo(Expected(10L * Frames)));
    }

    [Test]
    public void Seek_ToTheEnd_IsAllowedAndReadsNothing()
    {
        using CddaDecoder decoder = new(new FakeDisc(100), 10, 20);

        Assert.That(decoder.Seek(decoder.TotalFrames), Is.True);
        Assert.That(decoder.Read(new float[20], 10), Is.Zero);
    }

    [TestCase(-1L)]
    [TestCase(10L * Frames + 1)]
    public void Seek_OutsideTheTrack_Fails(long frame)
    {
        using CddaDecoder decoder = new(new FakeDisc(100), 10, 20);

        Assert.That(decoder.Seek(frame), Is.False);
    }

    [Test]
    public void Read_SectorFailsOnceThenWorks_IsRetried()
    {
        FakeDisc disc = new(100);
        disc.FailuresLeft[12] = 2;
        using CddaDecoder decoder = new(disc, 10, 30);
        float[] samples = new float[Frames * 6];

        Assert.That(decoder.Seek(2L * Frames), Is.True);
        Assert.That(decoder.Read(samples, Frames * 3), Is.EqualTo(Frames * 3));

        Assert.That(samples[0], Is.EqualTo(Expected(12L * Frames)), "the retried sector holds its real audio");
    }

    [Test]
    public void Read_UnreadableSector_PlaysSilenceAndContinues()
    {
        FakeDisc disc = new(100);
        disc.BadSectors.Add(11);
        using CddaDecoder decoder = new(disc, 10, 30);
        float[] samples = new float[Frames * 3 * 2];

        int read = decoder.Read(samples, Frames * 3);

        Assert.That(read, Is.EqualTo(Frames * 3));
        Assert.That(samples.AsSpan(Frames * 2, Frames * 2).ToArray(), Has.All.Zero, "the bad sector is silent");
        Assert.That(samples[0], Is.EqualTo(Expected(10L * Frames)), "the sector before is intact");
        Assert.That(samples[Frames * 4], Is.EqualTo(Expected(12L * Frames)), "the sector after is intact");
    }

    [Test]
    public void Read_LongRunOfUnreadableSectors_Throws()
    {
        FakeDisc disc = new(1000);
        for (int lba = 0; lba < 1000; lba++) disc.BadSectors.Add(lba);
        using CddaDecoder decoder = new(disc, 0, 1000);
        float[] samples = new float[Frames * 2 * 16];

        Assert.Throws<IOException>(() =>
        {
            while (decoder.Read(samples, Frames * 16) > 0) { }
        });
    }

    [Test]
    public void Read_DiscRemoved_Throws()
    {
        FakeDisc disc = new(100) { Gone = true };
        using CddaDecoder decoder = new(disc, 0, 50);

        Assert.Throws<IOException>(() => decoder.Read(new float[200], 100));
    }

    [Test]
    public void Dispose_ClosesTheReader_AndReadsAfterwardsThrow()
    {
        FakeDisc disc = new(100);
        CddaDecoder decoder = new(disc, 0, 50);

        decoder.Dispose();
        decoder.Dispose();

        Assert.That(disc.Disposed, Is.True);
        Assert.Throws<ObjectDisposedException>(() => decoder.Read(new float[20], 10));
    }

    [Test]
    public void Constructor_EmptyOrNegativeRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new CddaDecoder(new FakeDisc(10), 5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new CddaDecoder(new FakeDisc(10), -1, 5));
    }

    [Test]
    public void Read_ExtremeSamples_MapToTheFullFloatRange()
    {
        using CddaDecoder decoder = new(new ConstantReader(short.MinValue, short.MaxValue), 0, 4);
        float[] samples = new float[2];

        Assert.That(decoder.Read(samples, 1), Is.EqualTo(1));

        Assert.That(samples[0], Is.EqualTo(-1f));
        Assert.That(samples[1], Is.EqualTo(32767f / 32768f));
    }

    [Test]
    public void LinuxRequestStruct_MatchesTheKernelLayout()
    {
        // struct cdrom_read_audio: union (4), format (1) + padding, int nframes, pointer. 24 bytes with 8-byte pointers, 16 with 4-byte ones.
        Assert.That(Marshal.SizeOf<LinuxCdAudioReader.ReadAudioRequest>(), Is.EqualTo(IntPtr.Size == 8 ? 24 : 16));
        Assert.That(Marshal.OffsetOf<LinuxCdAudioReader.ReadAudioRequest>(nameof(LinuxCdAudioReader.ReadAudioRequest.Frames)), Is.EqualTo((IntPtr)8));
    }

    [Test]
    public void WindowsRequest_MatchesTheDriverDefinitions()
    {
        Assert.That(WindowsCdAudioReader.IoctlCdromRawRead, Is.EqualTo(0x0002403Eu), "winioctl.h: IOCTL_CDROM_RAW_READ");
        Assert.That(Marshal.SizeOf<WindowsCdAudioReader.RawReadInfo>(), Is.EqualTo(16));
    }

    [Test]
    public void Open_NotADevice_Throws()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux only.");

        Assert.Throws<IOException>(() => CdAudioReader.Open("/etc/passwd-not-a-device"));
        Assert.Throws<FileNotFoundException>(() => CdAudioReader.Open("/dev/aibell-no-such-drive"));
    }

    private sealed class ConstantReader(short left, short right) : ICdAudioReader
    {
        public bool ReadSectors(int lba, int count, Span<byte> destination)
        {
            for (int at = 0; at < count * CddaDecoder.SectorBytes; at += 4)
            {
                BitConverter.TryWriteBytes(destination.Slice(at, 2), left);
                BitConverter.TryWriteBytes(destination.Slice(at + 2, 2), right);
            }
            return true;
        }

        public void Dispose() { }
    }
}
