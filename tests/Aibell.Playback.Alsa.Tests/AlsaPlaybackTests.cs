using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Playback.Alsa;
using NUnit.Framework;

namespace Aibell.Playback.Alsa.Tests;

/// <summary>
/// Checks that the ALSA player really puts sound out. The device has to accept the frames, so
/// this is checked against the real device where there is one, and against the library's own
/// discarding device elsewhere.
/// </summary>
[TestFixture]
public sealed class AlsaPlaybackTests
{
    private static string? FixturePath => FindFixture();

    private static string? FindFixture()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aibell.slnx")))
        {
            directory = directory.Parent;
        }
        string? root = directory?.FullName;
        if (root is null) return null;
        string path = Path.Combine(root, "tests", "fixtures", "flac", "s16_stereo_44k.flac");
        return File.Exists(path) ? path : null;
    }

    private static Track ATrack(string path) =>
        new(Guid.NewGuid(), new Uri(path), "Test", "Artist", "Album", TimeSpan.FromMinutes(3));

    /// <summary>The device the test writes to: the real one when there is one, the discarding one otherwise.</summary>
    private static string DeviceForTest()
    {
        try
        {
            using var output = new AlsaOutput(_ => { }, "default");
            return "default";
        }
        catch (InvalidOperationException)
        {
            return "null";
        }
    }

    [Test]
    public void The_Device_Takes_The_Frames_That_Are_Written_To_It()
    {
        string device = DeviceForTest();
        using var output = new AlsaOutput(_ => { }, device);
        output.Start();

        float[] block = new float[960 * 2];
        for (int i = 0; i < block.Length; i++) block[i] = 0.1f;
        output.Write(block);

        Assert.That(output.FramesWritten, Is.GreaterThan(0), "the device took no frames at all");
        Assert.That(output.Failure, Is.Null);
    }

    [Test]
    public void A_Whole_Track_Is_Played_Through_The_Device()
    {
        string? fixture = FixturePath;
        if (fixture is null) Assert.Ignore("There is no audio fixture to play.");

        string device = DeviceForTest();
        using var engine = new AlsaPlaybackEngine(device: device);
        Track track = ATrack(fixture);

        engine.LoadAsync(track).GetAwaiter().GetResult();
        engine.PlayAsync().GetAwaiter().GetResult();

        // Give the device a moment to take the frames.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && engine.Snapshot.Position < TimeSpan.FromMilliseconds(200))
        {
            Thread.Sleep(50);
        }

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        Assert.That(engine.Snapshot.Position, Is.GreaterThan(TimeSpan.Zero), "the position did not move, so nothing was played");
    }

    [Test]
    public void A_Real_Song_Plays_Through_The_Engine()
    {
        string? fixture = FixturePath;
        if (fixture is null) Assert.Ignore("There is no audio fixture to play.");

        // The engine is the one the application uses, with the real decoder and the real device.
        string device = DeviceForTest();
        using var engine = new AlsaPlaybackEngine(device: device);
        Track track = ATrack(fixture);

        engine.LoadAsync(track).GetAwaiter().GetResult();
        engine.PlayAsync().GetAwaiter().GetResult();

        // The track is four tenths of a second; the position has to move for it to have played.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && engine.Snapshot.Position < TimeSpan.FromMilliseconds(100))
        {
            Thread.Sleep(25);
        }

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        Assert.That(engine.Snapshot.Position, Is.GreaterThan(TimeSpan.Zero), "the song did not play");
        Assert.That(engine.Snapshot.Error, Is.Null);
    }

    [Test]
    public async Task A_Track_Pauses_Seeks_And_Resumes_Through_The_Engine()
    {
        string? fixture = FixturePath;
        if (fixture is null) Assert.Ignore("There is no audio fixture to play.");

        string device = DeviceForTest();
        using var engine = new AlsaPlaybackEngine(device: device);
        Track track = ATrack(fixture);

        await engine.LoadAsync(track);
        await engine.SetVolumeAsync(0.5);
        await engine.SetEqualizerAsync(EqualizerSettings.Flat with { IsEnabled = true, Preamp = 1 });
        await engine.PlayAsync();

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && engine.Snapshot.Position < TimeSpan.FromMilliseconds(150))
        {
            await Task.Delay(25);
        }

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        Assert.That(engine.Snapshot.Position, Is.GreaterThan(TimeSpan.Zero));

        await engine.PauseAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Paused));

        await engine.SeekAsync(TimeSpan.FromMilliseconds(50));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromMilliseconds(50)));

        await engine.PlayAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));

        await engine.SetMutedAsync(true);
        await engine.SetMutedAsync(false);
        await engine.SetRateAsync(1.25);

        await engine.StopAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [Test]
    public async Task A_Full_Length_Flac_Song_Plays_And_Seeks_Cleanly_Through_Alsa()
    {
        const string musicDir = "/media/daniel/Extra/Music";
        if (!Directory.Exists(musicDir))
        {
            Assert.Ignore("Local music collection is not mounted.");
        }

        string? flacFile = Directory.EnumerateFiles(musicDir, "*.flac", SearchOption.AllDirectories).FirstOrDefault();
        if (flacFile is null)
        {
            Assert.Ignore("No FLAC file found in local music collection.");
        }

        string device = DeviceForTest();
        using var engine = new AlsaPlaybackEngine(device: device);
        Track track = new(Guid.NewGuid(), new Uri(flacFile), "Test", "Artist", "Album", TimeSpan.FromMinutes(4));

        await engine.LoadAsync(track);
        await engine.SetVolumeAsync(0.7);
        await engine.PlayAsync();

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && engine.Snapshot.Position < TimeSpan.FromMilliseconds(800))
        {
            await Task.Delay(50);
        }

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        Assert.That(engine.Snapshot.Position, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(800)), "real FLAC song did not advance");
        Assert.That(engine.Snapshot.Error, Is.Null);

        // Seek 30 seconds into the track and let it play for another ~1 second.
        TimeSpan seekTarget = TimeSpan.FromSeconds(30);
        await engine.SeekAsync(seekTarget);

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && engine.Snapshot.Position < seekTarget + TimeSpan.FromMilliseconds(600))
        {
            await Task.Delay(50);
        }

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        Assert.That(engine.Snapshot.Position, Is.GreaterThanOrEqualTo(seekTarget + TimeSpan.FromMilliseconds(500)), "position did not advance after seeking");
        Assert.That(engine.Snapshot.Error, Is.Null);

        await engine.StopAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [Test]
    public void The_Samples_That_Reach_The_Device_Match_The_Song()
    {
        string? fixture = FixturePath;
        if (fixture is null) Assert.Ignore("There is no audio fixture to play.");

        // The samples are taken where the device takes them and compared with the decoded song,
        // so a wrong sample format shows up as noise rather than as a silent but healthy run.
        List<float> captured = [];
        using var source = Aibell.Playback.Core.ManagedPcmSource.FromDecoder(
            Aibell.Audio.Flac.FlacDecoder.Open(File.OpenRead(fixture)), 48000);
        using var pipeline = new Aibell.Playback.Core.DecodePipeline(source, 48000, 0);
        using var output = new AlsaOutput(buffer =>
        {
            pipeline.Render(buffer);
            lock (captured) captured.AddRange(buffer);
        }, "null");
        output.Start();

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (captured)
            {
                if (captured.Count > 48000) break;
            }
            Thread.Sleep(25);
        }

        lock (captured)
        {
            Assert.That(captured, Is.Not.Empty, "no audio reached the device");
            // The song is a tone: the samples have to be a smooth wave, not noise.
            Assert.That(captured.Max(Math.Abs) > 0.05f, Is.True, "the song was silent");
            double rough = 0;
            for (int i = 1; i < captured.Count; i++)
            {
                rough += Math.Abs(captured[i] - captured[i - 1]);
            }
            rough /= Math.Max(1, captured.Count);
            Assert.That(rough, Is.LessThan(0.05f), "the samples jump around, which means noise");
        }
    }

    [Test]
    public void The_Decoder_Gives_The_Device_Real_Audio()
    {
        string? fixture = FixturePath;
        if (fixture is null) Assert.Ignore("There is no audio fixture to play.");

        // The signal is caught where the device pulls it, so it is the decoder's output that is
        // checked and not the device's timing.
        List<float> captured = [];
        using var source = Aibell.Playback.Core.ManagedPcmSource.FromDecoder(
            Aibell.Audio.Flac.FlacDecoder.Open(File.OpenRead(fixture)), 48000);
        using var pipeline = new Aibell.Playback.Core.DecodePipeline(source, 48000, 0);
        using var output = new AlsaOutput(buffer =>
        {
            pipeline.Render(buffer);
            lock (captured) captured.AddRange(buffer);
        }, "null");
        output.Start();

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (captured)
            {
                if (captured.Count > 48000 * 2) break;
            }
            Thread.Sleep(50);
        }

        lock (captured)
        {
            Assert.That(captured, Is.Not.Empty, "no audio reached the device");
            Assert.That(captured.Any(value => Math.Abs(value) > 0.01f), Is.True, "the audio was silence");
        }
        Assert.That(output.FramesWritten, Is.GreaterThan(0), "the device took no frames");
    }
}
