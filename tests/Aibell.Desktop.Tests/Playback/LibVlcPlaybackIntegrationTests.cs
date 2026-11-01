using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Playback.LibVLC;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.Playback;

[TestFixture]
[NonParallelizable]
[Category("NativePlayback")]
public sealed class LibVlcPlaybackIntegrationTests
{
    private static readonly string[] Formats = ["flac", "m4a", "mp3", "ogg", "opus", "wav"];

    [SetUp]
    public void RequireNativeRuntime()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_NATIVE_PLAYBACK_TESTS") != "1")
        {
            Assert.Ignore("Set AIBELL_NATIVE_PLAYBACK_TESTS=1 to validate the installed native LibVLC runtime.");
        }
    }

    [TestCaseSource(nameof(Formats))]
    public async Task PlayAsync_CodecFixture_ReachesPlayingAndNaturalEnd(string extension)
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        TaskCompletionSource playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int endNotifications = 0;
        engine.StateChanged += (_, snapshot) =>
        {
            if (snapshot.Status == PlaybackStatus.Playing) playing.TrySetResult();
            if (snapshot.Status == PlaybackStatus.Ended)
            {
                Interlocked.Increment(ref endNotifications);
                ended.TrySetResult();
            }
            if (snapshot.Status == PlaybackStatus.Faulted)
            {
                InvalidOperationException failure = new(snapshot.Error);
                playing.TrySetException(failure);
                ended.TrySetException(failure);
            }
        };
        Track track = Fixture(extension);
        await engine.LoadAsync(track);
        await engine.SetMutedAsync(true);
        await engine.PlayAsync();
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Native teardown may emit Stopped and clock callbacks after EndReached.
        await Task.Delay(100);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(track.Id));
            Assert.That(engine.Snapshot.IsMuted, Is.True);
            Assert.That(engine.Snapshot.Position, Is.EqualTo(track.Duration));
            Assert.That(engine.Snapshot.Error, Is.Null);
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Ended));
            Assert.That(Volatile.Read(ref endNotifications), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SetRateAsync_AppliesToTheNativePlayerAndSurvivesTheNextInput()
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy", "--input-repeat=1000"]);
        Track track = Fixture("wav");
        await engine.LoadAsync(track);
        await engine.SetMutedAsync(true);
        await engine.SetRateAsync(1.5);
        await TransitionAsync(engine, PlaybackStatus.Playing, () => engine.PlayAsync());
        Assert.That(engine.NativeRate, Is.EqualTo(1.5f).Within(0.01f));

        await engine.SetRateAsync(2.0);
        Assert.That(engine.NativeRate, Is.EqualTo(2.0f).Within(0.01f), "Changing speed while playing takes effect at once.");
        await engine.StopAsync();
        await engine.LoadAsync(Fixture("flac"));
        await TransitionAsync(engine, PlaybackStatus.Playing, () => engine.PlayAsync());
        Assert.That(engine.Rate, Is.EqualTo(2.0));
        // The speed is re-applied just after playback starts, off LibVLC's event thread.
        for (int attempt = 0; attempt < 100 && Math.Abs(engine.NativeRate - 2.0f) > 0.01f; attempt++) await Task.Delay(20);
        Assert.That(engine.NativeRate, Is.EqualTo(2.0f).Within(0.01f), "A new input keeps the chosen speed until it is reset.");

        await engine.SetRateAsync(1.0);
        Assert.That(engine.NativeRate, Is.EqualTo(1.0f).Within(0.01f));
        Assert.That(async () => await engine.SetRateAsync(double.NaN), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(async () => await engine.SetRateAsync(8), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void NativeEqualizer_BandLayout_MatchesTheDomain()
    {
        using LibVLCSharp.Shared.Equalizer native = new();

        Assert.That((int)native.BandCount, Is.EqualTo(EqualizerSettings.BandCount));
        for (uint band = 0; band < native.BandCount; band++)
            Assert.That(native.BandFrequency(band), Is.EqualTo(EqualizerSettings.BandFrequencies[(int)band]).Within(0.01), $"band {band}");
    }

    [Test]
    public async Task SetEqualizerAsync_BeforeLoadAndWhilePlaying_KeepsPlayingAndRemembersTheCurve()
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy", "--input-repeat=1000"]);
        EqualizerSettings bass = new() { IsEnabled = true, Preamp = -3, Bands = [6, 5, 4, 2, 0, 0, 0, 0, 0, 0] };
        await engine.SetEqualizerAsync(bass);
        await engine.LoadAsync(Fixture("wav"));
        await engine.SetMutedAsync(true);
        await TransitionAsync(engine, PlaybackStatus.Playing, () => engine.PlayAsync());
        Assert.That(engine.Equalizer.SameAs(bass), Is.True);

        await engine.SetEqualizerAsync(bass with { Bands = [0, 0, 0, 0, 0, 0, 0, 0, 6, 6] });
        await engine.SetEqualizerAsync(EqualizerSettings.Flat);
        await engine.StopAsync();
        await engine.LoadAsync(Fixture("flac"));
        await TransitionAsync(engine, PlaybackStatus.Playing, () => engine.PlayAsync());

        Assert.That(engine.Equalizer.IsEnabled, Is.False);
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        Assert.That(engine.Snapshot.Error, Is.Null);
    }

    [Test]
    public async Task TransportControls_PauseSeekResumeAndStop_PreservesTrackAndResetsPosition()
    {
        // Dummy output can decode a one-second tone faster than wall-clock time.
        // Repeating input keeps it available while the test exercises transport.
        using LibVlcPlaybackEngine engine = new(["--aout=dummy", "--input-repeat=1000"]);
        Track track = Fixture("wav");
        await engine.LoadAsync(track);
        await engine.SetMutedAsync(true);
        await engine.SetVolumeAsync(0.25);
        await TransitionAsync(engine, PlaybackStatus.Playing, () => engine.PlayAsync());
        await TransitionAsync(engine, PlaybackStatus.Paused, () => engine.PauseAsync());
        await engine.PauseAsync();
        await engine.SeekAsync(TimeSpan.FromMilliseconds(250));
        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Paused));
            Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromMilliseconds(250)));
            Assert.That(engine.Snapshot.Volume, Is.EqualTo(0.25));
            Assert.That(engine.Snapshot.IsMuted, Is.True);
        });
        await TransitionAsync(engine, PlaybackStatus.Playing, () => engine.PlayAsync());
        await engine.StopAsync();

        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
            Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
            Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(track.Id));
        });
    }

    private static async Task TransitionAsync(IPlaybackEngine engine, PlaybackStatus status, Func<Task> action)
    {
        TaskCompletionSource transition = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void StateChanged(object? sender, PlaybackSnapshot snapshot)
        {
            if (snapshot.Status == status) transition.TrySetResult();
            if (snapshot.Status == PlaybackStatus.Faulted)
                transition.TrySetException(new InvalidOperationException(snapshot.Error));
        }
        engine.StateChanged += StateChanged;
        try
        {
            await action();
            await transition.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            engine.StateChanged -= StateChanged;
        }
    }

    private static Track Fixture(string extension)
    {
        Uri location = new(Path.Combine(AppContext.BaseDirectory, "fixtures", $"fixture.{extension}"));
        return new Track(Guid.NewGuid(), location, "Fixture Tone", "Aibell Test Runner", "Synthetic Fixtures", TimeSpan.FromSeconds(1));
    }
}
