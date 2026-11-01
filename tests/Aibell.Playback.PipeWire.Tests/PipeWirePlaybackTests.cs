using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Playback.PipeWire;
using NUnit.Framework;

namespace Aibell.Playback.PipeWire.Tests;

/// <summary>
/// Checks that the PipeWire stream and playback engine actually connect to the running PipeWire
/// daemon and play audio frames through it.
/// </summary>
[TestFixture]
public sealed class PipeWirePlaybackTests
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

    private static Track ATrack(string path, TimeSpan? duration = null) =>
        new(Guid.NewGuid(), new Uri(path), "Test", "Artist", "Album", duration ?? TimeSpan.FromMinutes(3));

    private static void RequirePipeWire()
    {
        if (!PipeWireRuntime.IsAvailable(out string? problem))
        {
            Assert.Ignore(problem ?? "PipeWire is not available on this machine.");
        }
    }

    [Test]
    public async Task SeekWhilePlaying_RepeatedlyContinuesFromEachTarget()
    {
        RequirePipeWire();
        string path = Path.Combine(Path.GetTempPath(), $"aibell-seek-{Guid.NewGuid():N}.wav");
        try
        {
            const int rate = 48000;
            const int seconds = 12;
            const int dataBytes = rate * seconds * 4;
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8);
                writer.Write(36 + dataBytes);
                writer.Write("WAVEfmt "u8);
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)2);
                writer.Write(rate);
                writer.Write(rate * 4);
                writer.Write((short)4);
                writer.Write((short)16);
                writer.Write("data"u8);
                writer.Write(dataBytes);
                writer.Write(new byte[dataBytes]);
            }
            using var engine = new PipeWirePlaybackEngine();
            await engine.SetMutedAsync(true);
            await engine.LoadAsync(ATrack(path, TimeSpan.FromSeconds(seconds)));
            await engine.PlayAsync();
            await Task.Delay(500);
            foreach (int second in new[] { 2, 6, 3, 7, 4 })
            {
                TimeSpan target = TimeSpan.FromSeconds(second);
                await engine.SeekAsync(target);
                bool advanced = false;
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && engine.Snapshot.Status == PlaybackStatus.Playing)
                {
                    if (engine.Snapshot.Position > target + TimeSpan.FromMilliseconds(300)) { advanced = true; break; }
                    await Task.Delay(25);
                }
                Assert.That(advanced, Is.True, $"Playback stopped advancing after seeking to {second}s: {engine.Snapshot}");
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task ForwardSeek_ContinuesPlayingOnFirstAttempt()
    {
        RequirePipeWire();
        string? path = Environment.GetEnvironmentVariable("AIBELL_TEST_AUDIO");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Ignore("Set AIBELL_TEST_AUDIO to a long seekable track.");
        using var engine = new PipeWirePlaybackEngine();
        await engine.SetMutedAsync(true);
        await engine.LoadAsync(ATrack(path, TimeSpan.FromMinutes(10)));
        await engine.PlayAsync();
        await Task.Delay(2000);
        foreach (int seconds in new[] { 101, 210, 180, 230, 140 })
        {
            TimeSpan target = TimeSpan.FromSeconds(seconds);
            await engine.SeekAsync(target);
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && engine.Snapshot.Position < target + TimeSpan.FromMilliseconds(500) && engine.Snapshot.Status == PlaybackStatus.Playing)
                await Task.Delay(25);
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing), engine.Snapshot.Error);
            Assert.That(engine.Snapshot.Position, Is.GreaterThan(target + TimeSpan.FromMilliseconds(500)), $"seek to {seconds}s");
        }
    }

    [Test]
    public void RenderFailure_IsReportedWithoutEscapingTheNativeCallback()
    {
        RequirePipeWire();
        using var stream = new PipeWireStream(_ => throw new InvalidOperationException("render failed"));
        stream.WaitUntilReady(TimeSpan.FromSeconds(5));
        stream.Start();
        Assert.That(SpinWait.SpinUntil(() => stream.Failure is not null, TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(stream.Failure!.Message, Is.EqualTo("render failed"));
    }

    [Test]
    public async Task A_Track_ReachesEndedAfterTheStreamDrains()
    {
        RequirePipeWire();
        string? fixture = FixturePath;
        if (fixture is null) Assert.Ignore("There is no audio fixture to play.");
        using var engine = new PipeWirePlaybackEngine();
        await engine.LoadAsync(ATrack(fixture));
        await engine.PlayAsync();
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && engine.Snapshot.Status == PlaybackStatus.Playing)
            await Task.Delay(25);
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Ended), engine.Snapshot.Error);
    }

    [Test]
    public void The_Stream_Connects_And_Sends_Frames_To_The_Daemon()
    {
        RequirePipeWire();

        using var stream = new PipeWireStream(output =>
        {
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = 0.05f * MathF.Sin(i * 0.05f);
            }
        });

        Assert.That(stream.WaitUntilReady(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(stream.Ready, Is.True);
        Assert.That(stream.SampleRate, Is.GreaterThan(0u));
        Assert.That(PipeWireStream.Channels, Is.EqualTo(2));

        stream.Start();
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && stream.FramesWritten < 4096)
        {
            Thread.Sleep(20);
        }

        stream.Stop();
        Assert.That(stream.FramesWritten, Is.GreaterThan(0), "the PipeWire daemon took no frames");
        Assert.That(stream.Failure, Is.Null);
    }

    [Test]
    public async Task A_Real_Track_Plays_Pauses_Seeks_And_Resumes_Through_PipeWire()
    {
        RequirePipeWire();
        string? fixture = FixturePath;
        if (fixture is null) Assert.Ignore("There is no audio fixture to play.");

        using var engine = new PipeWirePlaybackEngine();
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
        Assert.That(engine.Snapshot.Position, Is.GreaterThan(TimeSpan.Zero), "the position did not move");
        Assert.That(engine.Snapshot.Error, Is.Null);

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
    public async Task A_Full_Length_Flac_Song_Plays_And_Seeks_Cleanly_Through_PipeWire()
    {
        RequirePipeWire();
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

        using var engine = new PipeWirePlaybackEngine();
        Track track = ATrack(flacFile, TimeSpan.FromMinutes(4));

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
}
