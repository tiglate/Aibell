using Aibell.Application.Playback;
using NUnit.Framework;
using static Aibell.Playback.Miniaudio.Tests.EngineTestSupport;

namespace Aibell.Playback.Miniaudio.Tests;

[TestFixture]
[NonParallelizable]
public sealed class MiniaudioPlaybackEngineTests
{
    [Test]
    public void Runtime_LoadsTheBundledLibrary()
    {
        Assert.That(MiniaudioRuntime.IsAvailable(out string? problem), Is.True, problem);
    }

    [TestCase("fixture.wav")]
    [TestCase("fixture.flac")]
    [TestCase("fixture.mp3")]
    [TestCase("fixture.ogg")]
    public async Task Play_CommonFormat_RunsToTheEnd(string fixture)
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor(fixture));
        await engine.PlayAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));

        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted);

        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
        Assert.That(ended.Position, Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task Play_SubscriberThatThrowsOnTheEndEvent_BecomesAFaultNotAProcessCrash()
    {
        using var engine = NewEngine();
        engine.StateChanged += (_, state) => { if (state.Status == PlaybackStatus.Ended) throw new InvalidOperationException("subscriber failure"); };
        await engine.LoadAsync(TrackFor("fixture.wav"));
        await engine.PlayAsync();

        PlaybackSnapshot faulted = await WaitForAsync(engine, state => state.Status == PlaybackStatus.Faulted);

        Assert.That(faulted.Error, Does.Contain("subscriber failure"));
        Assert.That(faulted.ErrorDetails, Does.Contain("InvalidOperationException"));
    }

    [Test]
    public async Task Play_UnrecognizedFile_PublishesTheFullExceptionForTheLog()
    {
        string path = Path.Combine(Path.GetTempPath(), "aibell-garbage-" + Guid.NewGuid().ToString("N") + ".ogg");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        try
        {
            using var engine = NewEngine();
            await engine.LoadAsync(TrackFor(new Uri(path)));

            await Assert.ThatAsync(() => engine.PlayAsync(), Throws.TypeOf<InvalidOperationException>());

            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Faulted));
            Assert.That(engine.Snapshot.Error, Is.Not.Null.And.Not.Empty);
            Assert.That(engine.Snapshot.ErrorDetails, Does.Contain("Exception"));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Play_UnreadableMp4OnLinux_ExplainsThatVlcIsNeeded()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Windows plays MP4 audio through Media Foundation.");
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor(new Uri(Fixture("aac-mp4/fragmented.m4a"))));

        InvalidOperationException? error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PlayAsync());

        Assert.That(error!.Message, Does.Contain("VLC"));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Faulted));
        Assert.That(engine.Snapshot.Error, Does.Contain("Preferences"));
    }

    [TestCase("fixture.m4a")]
    [TestCase("aac-mp4/stereo_44k.m4a")]
    public async Task Play_AacM4aOnLinux_PlaysWithTheDefaultEngine(string fixture)
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Windows hands MP4 files to Media Foundation first.");
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor(new Uri(Fixture(fixture))));
        await engine.PlayAsync();

        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted);

        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Play_AppleLosslessOnLinux_PlaysWithTheDefaultEngine()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Windows hands MP4 files to Media Foundation first.");
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("alac.m4a"));
        await engine.PlayAsync();

        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted);

        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Play_AppleLossless_PlaysWithTheManagedEngineOnEverySystem()
    {
        using var engine = new MiniaudioPlaybackEngine(null, useNullBackend: true, managedCodecs: true);
        await engine.LoadAsync(TrackFor("alac.m4a"));
        await engine.PlayAsync();

        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted);

        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Play_AacOnWindows_UsesMediaFoundation()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Media Foundation exists only on Windows.");
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("fixture.m4a"));
        await engine.PlayAsync();

        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted);

        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Play_DamagedFile_Faults()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("corrupt.mp3"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PlayAsync());

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Faulted));
    }

    [Test]
    public async Task Play_MissingFile_Faults()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor(new Uri(Path.Combine(Path.GetTempPath(), "aibell-missing-track.mp3"))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PlayAsync());

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Faulted));
    }

    [Test]
    public async Task Play_WithoutATrack_Throws()
    {
        using var engine = NewEngine();

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PlayAsync());
    }

    [Test]
    public async Task PauseAndResume_KeepThePosition()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("fixture.wav", 5));
        await engine.PlayAsync();
        await Task.Delay(300);

        await engine.PauseAsync();
        TimeSpan paused = engine.Snapshot.Position;
        await Task.Delay(300);

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Paused));
        Assert.That(paused, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(paused));

        await engine.PlayAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        PlaybackSnapshot later = await WaitForAsync(engine, state => state.Position > paused + TimeSpan.FromMilliseconds(100));
        Assert.That(later.Status, Is.EqualTo(PlaybackStatus.Playing));
    }

    [Test]
    public async Task Stop_ResetsToTheStart()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("fixture.wav", 5));
        await engine.PlayAsync();
        await Task.Delay(200);

        await engine.StopAsync();

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public async Task Seek_BeforePlaying_StartsFromThatPosition()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("fixture.wav"));
        await engine.SeekAsync(TimeSpan.FromSeconds(0.6));
        await engine.PlayAsync();

        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted, 5);

        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [TestCase("fixture.wav")]
    [TestCase("fixture.flac")]
    [TestCase("fixture.mp3")]
    [TestCase("fixture.ogg")]
    public async Task Seek_WhilePlaying_JumpsForward(string fixture)
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor(fixture, 1));
        await engine.PlayAsync();

        await engine.SeekAsync(TimeSpan.FromSeconds(0.9));
        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted, 5);

        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Volume_IsClampedAndPublished()
    {
        using var engine = NewEngine();
        await engine.SetVolumeAsync(2);
        await engine.SetMutedAsync(true);

        Assert.That(engine.Snapshot.Volume, Is.EqualTo(1));
        Assert.That(engine.Snapshot.IsMuted, Is.True);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.SetVolumeAsync(double.NaN));
    }

    [Test]
    public async Task Load_WhilePlaying_ReplacesTheTrack()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("fixture.wav", 5));
        await engine.PlayAsync();

        await engine.LoadAsync(TrackFor("fixture.flac"));

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        await engine.PlayAsync();
        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted);
        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public void Engine_OffersTheEqualizerAndPlaybackSpeed()
    {
        using var engine = NewEngine();

        Assert.That(engine, Is.InstanceOf<IEqualizerControl>());
        Assert.That(engine, Is.InstanceOf<IPlaybackRateControl>());
        Assert.That(engine.Rate, Is.EqualTo(1.0));
    }

    [Test]
    public async Task SetRate_RejectsValuesOutsideTheSupportedRange()
    {
        using var engine = NewEngine();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.SetRateAsync(0.4));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.SetRateAsync(4.5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.SetRateAsync(double.NaN));
        await engine.SetRateAsync(1.5);
        Assert.That(engine.Rate, Is.EqualTo(1.5));
    }

    [TestCase(2.0, 0.0, 0.9)]
    [TestCase(0.5, 1.6, 4.0)]
    public async Task Play_AtAChangedSpeed_TakesProportionallyLonger(double speed, double minSeconds, double maxSeconds)
    {
        using var engine = NewEngine();
        await engine.SetRateAsync(speed);
        await engine.LoadAsync(TrackFor("fixture.flac"));
        await engine.PlayAsync();
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted);

        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
        Assert.That(clock.Elapsed.TotalSeconds, Is.InRange(minSeconds, maxSeconds));
    }

    [Test]
    public async Task SetRate_WhilePlaying_KeepsThePositionInSourceTime()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("fixture.wav"));
        await engine.SeekAsync(TimeSpan.FromSeconds(0.2));
        await engine.PlayAsync();
        await Task.Delay(100);

        await engine.SetRateAsync(2.0);
        await Task.Delay(200);
        await engine.PauseAsync();

        // 0.2 s start, ~0.1 s at normal speed, ~0.2 s at double speed: about 0.7 s. Restarting would show ~0.4 s.
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Paused));
        Assert.That(engine.Snapshot.Position.TotalSeconds, Is.InRange(0.5, 0.95));
    }

    [Test]
    public async Task Equalizer_IsStoredNormalizedAndPlaybackStillCompletes()
    {
        using var engine = NewEngine();
        await engine.SetEqualizerAsync(new Aibell.Domain.Playback.EqualizerSettings { IsEnabled = true, Preamp = -3, Bands = [12, 99, 0, 0, 0, 0, 0, 0, 0, -12] });
        Assert.That(engine.Equalizer.Bands[1], Is.EqualTo(12), "gains are clamped like every engine");
        await engine.LoadAsync(TrackFor("fixture.flac"));
        await engine.PlayAsync();

        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted);

        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }
}
