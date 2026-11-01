using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Playback.LibVLC;
using LibVLCSharp.Shared;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.Playback;

[TestFixture]
[NonParallelizable]
[Category("NativePlayback")]
public sealed partial class LibVlcResumeIntegrationTests
{
    private string path = null!;

    [SetUp]
    public void SetUp()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_NATIVE_PLAYBACK_TESTS") != "1")
            Assert.Ignore("Set AIBELL_NATIVE_PLAYBACK_TESTS=1 to verify native resume.");
        path = Path.Combine(Path.GetTempPath(), $"aibell-resume-{Guid.NewGuid():N}.wav");
        WriteProbe(path, 1000);
    }

    [TearDown]
    public void TearDown()
    {
        if (path is not null) File.Delete(path);
    }

    private static void WriteProbe(string probePath, int baseline)
    {
        // Each millisecond has a unique PCM level (baseline + elapsed milliseconds).
        // The first decoded sample identifies its source offset even when the
        // callback output processes faster than wall-clock time.
        using BinaryWriter writer = new(File.Create(probePath));
        const int frames = 48000 * 8;
        writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(frames * 2);
        for (int index = 0; index < frames; index++)
            writer.Write((short)(baseline + index / 48));
    }

    [Test]
    public async Task SeekBeforePlay_LastSeekWins_FirstDecodedSamplesStartAtRequestedRegion()
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track());
        using Capture capture = new(NativePlayer(engine));
        await engine.SeekAsync(TimeSpan.FromSeconds(1));
        await engine.SeekAsync(TimeSpan.FromSeconds(5));
        await engine.SetVolumeAsync(1);
        await engine.PlayAsync();
        short first = await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(first, Is.InRange((short)6000, (short)6005), "The first decoded samples must come from the saved region, without playing the opening first.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StopAsync_PendingOrConsumedResume_NextPlayStartsAtZero(bool startFirst)
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track());
        using Capture capture = new(NativePlayer(engine));
        await engine.SetVolumeAsync(1);
        await engine.SeekAsync(TimeSpan.FromSeconds(5));
        if (startFirst)
        {
            await engine.PlayAsync();
            Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.InRange((short)6000, (short)6005));
        }
        await engine.StopAsync();
        capture.Reset();
        await engine.PlayAsync();
        Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(1000));
    }

    [Test]
    public async Task SeekAsync_PastEndBeforePlay_EndsWithoutDecodingOpeningSamples()
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track());
        using Capture capture = new(NativePlayer(engine));
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, snapshot) =>
        {
            if (snapshot.Status == PlaybackStatus.Ended) ended.TrySetResult();
        };
        await engine.SeekAsync(TimeSpan.FromSeconds(20));
        await engine.PlayAsync();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(capture.FirstSample.Task.IsCompleted, Is.False, "A position clamped to the end must not decode the opening.");
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(8)));
    }

    [Test]
    public async Task SeekAsync_NegativePrePlayPosition_FirstSamplesStartAtZero()
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track());
        using Capture capture = new(NativePlayer(engine));
        await engine.SetVolumeAsync(1);
        await engine.SeekAsync(TimeSpan.FromSeconds(5));
        await engine.SeekAsync(TimeSpan.FromSeconds(-1));
        await engine.PlayAsync();
        Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(1000));
    }

    [Test]
    public async Task LoadAsync_AnotherTrack_ClearsPendingResume()
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track());
        using Capture capture = new(NativePlayer(engine));
        await engine.SeekAsync(TimeSpan.FromSeconds(5));
        Track next = Track();
        await engine.LoadAsync(next);
        await engine.SetVolumeAsync(1);
        await engine.PlayAsync();
        Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(1000));
        Assert.That(engine.Snapshot.Track, Is.EqualTo(next));
    }

    [Test]
    public async Task LoadAsync_WhilePreviousTrackIsPlaying_ResumesNextTrackAtRequestedNativeOffset()
    {
        string firstPath = Path.Combine(Path.GetTempPath(), $"aibell-resume-first-{Guid.NewGuid():N}.wav");
        WriteProbe(firstPath, 10000);
        try
        {
            using LibVlcPlaybackEngine engine = new(["--aout=dummy", "--input-repeat=1000"]);
            Track first = Track() with { Location = new Uri(firstPath) }, next = Track();
            await engine.LoadAsync(first);
            MediaPlayer player = NativePlayer(engine);
            using Capture capture = new(player);
            await engine.SetVolumeAsync(1);
            await engine.SeekAsync(TimeSpan.FromSeconds(1));
            await engine.PlayAsync();
            Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.InRange((short)11000, (short)11005));
            await WaitUntilAsync(() => player.State == VLCState.Playing);
            Assert.That(engine.Snapshot.Track, Is.EqualTo(first));

            // Load synchronously stops/replaces the old native input. Start a new
            // capture only after it returns so old buffers cannot satisfy B's proof.
            await engine.LoadAsync(next);
            capture.Reset();
            await engine.SeekAsync(TimeSpan.FromSeconds(5));
            await engine.PlayAsync();
            Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.InRange((short)6000, (short)6005));
            Assert.That(engine.Snapshot.Track, Is.EqualTo(next));
            await engine.StopAsync();
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        }
        finally
        {
            File.Delete(firstPath);
        }
    }

    [Test]
    public async Task NaturalEnd_ResumedInput_ReplayStartsAtZeroAndPreservesEndSnapshot()
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track());
        using Capture capture = new(NativePlayer(engine));
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, snapshot) =>
        {
            if (snapshot.Status == PlaybackStatus.Ended) ended.TrySetResult();
        };
        await engine.SetVolumeAsync(1);
        await engine.SeekAsync(TimeSpan.FromSeconds(5));
        await engine.PlayAsync();
        Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.InRange((short)6000, (short)6005));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100); // Observe native teardown callbacks after EndReached.
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Ended));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(8)));
        capture.Reset();
        await engine.PlayAsync();
        Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(1000));
    }

    [Test]
    public async Task SeekAsync_PausedAndPlaying_ChangesNativeClock()
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track());
        MediaPlayer player = NativePlayer(engine);
        await engine.PlayAsync();
        await WaitUntilAsync(() => player.Time >= 200);
        await engine.PauseAsync();
        await WaitUntilAsync(() => player.State == VLCState.Paused);
        await engine.SeekAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => player.Time >= 4500 && player.Time <= 5500);
        await engine.PlayAsync();
        await WaitUntilAsync(() => player.State == VLCState.Playing);
        await engine.SeekAsync(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => player.Time >= 750 && player.Time <= 2500);
        await engine.StopAsync();
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public async Task SeekAsync_ClampCancelAndNonEnglishCulture_PreservesRequestedNativeStart()
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track());
        using Capture capture = new(NativePlayer(engine));
        await engine.SeekAsync(TimeSpan.FromSeconds(-1));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
        await engine.SeekAsync(TimeSpan.FromSeconds(20));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(8)));
        await engine.SeekAsync(TimeSpan.FromSeconds(5.25));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await engine.SeekAsync(TimeSpan.FromSeconds(1), canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await engine.StopAsync(canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await engine.LoadAsync(Track(), canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await engine.PlayAsync(canceled.Token));
        await engine.SetVolumeAsync(1);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            await engine.PlayAsync();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
        Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.InRange((short)6250, (short)6255));
    }

    [Test]
    public async Task Dispose_SeekWithoutLoadedTrack_ThrowsObjectDisposed()
    {
        LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        engine.Dispose();
        engine.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await engine.SeekAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await engine.PlayAsync());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10))
                Assert.Fail("Native playback state/clock did not reach the expected value within ten seconds.");
            await Task.Delay(10);
        }
    }

    private Track Track() => new(Guid.NewGuid(), new Uri(path), "Resume probe", "Tests", "Tests", TimeSpan.FromSeconds(8));

    private static MediaPlayer NativePlayer(LibVlcPlaybackEngine engine) =>
        (MediaPlayer)typeof(LibVlcPlaybackEngine).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;

    private sealed class Capture : IDisposable
    {
        private readonly MediaPlayer player;
        private readonly MediaPlayer.LibVLCAudioPlayCb callback;
        public TaskCompletionSource<short> FirstSample { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Capture(MediaPlayer player)
        {
            this.player = player;
            callback = (_, samples, count, _) =>
            {
                if (count > 0) FirstSample.TrySetResult(Marshal.ReadInt16(samples));
            };
            player.SetAudioFormat("S16N", 48000, 1);
            player.SetAudioCallbacks(callback, null, null, null, null);
        }

        public void Reset() => FirstSample = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            player.Stop();
            GC.KeepAlive(callback);
        }
    }
}
