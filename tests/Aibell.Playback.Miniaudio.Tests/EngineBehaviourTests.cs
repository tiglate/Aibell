using Aibell.Application.Playback;
using Aibell.Playback.Core;
using NUnit.Framework;
using static Aibell.Playback.Miniaudio.Tests.EngineTestSupport;

namespace Aibell.Playback.Miniaudio.Tests;

/// <summary>
/// Behaviours users rely on that the happy-path tests do not reach: seeking past the end and while paused, volume changes during
/// playback, a stream that dies in the middle of a track, and the race between opening a track and stopping or replacing it.
/// They run on the silent backend with the one-second fixtures and wait for states, not fixed times.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class EngineBehaviourTests
{
    private static bool Finished(PlaybackSnapshot state) => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted;

    [Test]
    public async Task Seek_PastTheDuration_ClampsToTheEndAndTheTrackEnds()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("fixture.wav"));
        await engine.PlayAsync();

        await engine.SeekAsync(TimeSpan.FromSeconds(60));

        Assert.That(engine.Snapshot.Position, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(1)));
        PlaybackSnapshot ended = await WaitForAsync(engine, Finished, 5);
        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Seek_BeforeTheStart_ClampsToZero()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("fixture.wav"));

        await engine.SeekAsync(TimeSpan.FromSeconds(-5));

        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public async Task Seek_WhilePaused_ResumesFromTheNewPosition()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("fixture.wav", 1));
        await engine.PlayAsync();
        await WaitForAsync(engine, state => state.Position > TimeSpan.FromMilliseconds(100));
        await engine.PauseAsync();

        await engine.SeekAsync(TimeSpan.FromSeconds(0.85));
        List<TimeSpan> positions = [];
        engine.StateChanged += (_, state) => { lock (positions) positions.Add(state.Position); };
        await engine.PlayAsync();
        PlaybackSnapshot ended = await WaitForAsync(engine, Finished, 5);

        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
        lock (positions) Assert.That(positions.Where(position => position > TimeSpan.Zero && position < TimeSpan.FromSeconds(1)), Is.All.GreaterThanOrEqualTo(TimeSpan.FromSeconds(0.8)),
            "after resuming, playback continues from the seek target, not from where it paused");
    }

    [Test]
    public async Task Volume_AtFullScaleAndChangedDuringPlayback_StillPlaysToTheEnd()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor("fixture.flac"));
        await engine.SetVolumeAsync(1.0);
        await engine.PlayAsync();

        await engine.SetVolumeAsync(0.25);
        await engine.SetMutedAsync(true);
        await engine.SetMutedAsync(false);
        PlaybackSnapshot ended = await WaitForAsync(engine, Finished, 5);

        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
        Assert.That(ended.Volume, Is.EqualTo(0.25));
        Assert.That(ended.IsMuted, Is.False);
    }

    [Test]
    public async Task Play_StreamThatDiesMidTrack_FaultsWithAMessageAndTheFullException()
    {
        using AudioTestServer server = new(Fixture("fixture.mp3"), live: false) { DropAfterBytes = 3000 };
        using HttpClient client = new();
        using var engine = NewEngine(client);
        await engine.LoadAsync(TrackFor(server.Address, 1));

        try { await engine.PlayAsync(); }
        catch (InvalidOperationException) { /* the connection may already be gone while the track opens */ }

        PlaybackSnapshot faulted = await WaitForAsync(engine, state => state.Status == PlaybackStatus.Faulted, 8);
        Assert.That(faulted.Error, Does.Contain("built-in player"));
        Assert.That(faulted.ErrorDetails, Does.Contain("Exception"));
    }

    [Test]
    public async Task Stop_WhileATrackIsStillOpening_NeverStartsItAndTheNextTrackPlays()
    {
        using ManualResetEventSlim gate = new(false);
        using AudioTestServer server = new(Fixture("fixture.mp3"), live: false) { Gate = gate };
        using HttpClient client = new();
        using var engine = NewEngine(client);
        List<PlaybackStatus> seen = [];
        engine.StateChanged += (_, state) => { lock (seen) seen.Add(state.Status); };
        await engine.LoadAsync(TrackFor(server.Address, 1));
        Task opening = engine.PlayAsync();
        Assert.That(opening.IsCompleted, Is.False, "the open is held by the server");

        await engine.StopAsync();
        gate.Set();
        await opening.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        lock (seen) Assert.That(seen, Does.Not.Contain(PlaybackStatus.Playing));
        await engine.LoadAsync(TrackFor("fixture.wav"));
        await engine.PlayAsync();
        PlaybackSnapshot ended = await WaitForAsync(engine, Finished, 5);
        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Load_WhileATrackIsStillOpening_PlaysTheNewTrackOnly()
    {
        using ManualResetEventSlim gate = new(false);
        using AudioTestServer server = new(Fixture("fixture.mp3"), live: false) { Gate = gate };
        using HttpClient client = new();
        using var engine = NewEngine(client);
        await engine.LoadAsync(TrackFor(server.Address, 1));
        Task opening = engine.PlayAsync();

        await engine.LoadAsync(TrackFor("fixture.wav"));
        gate.Set();
        await opening.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(engine.Snapshot.Track!.Location.IsFile, Is.True, "the replacement track stays loaded");
        Assert.That(engine.Snapshot.Status, Is.Not.EqualTo(PlaybackStatus.Playing), "the abandoned open must not start playback");
        await engine.PlayAsync();
        PlaybackSnapshot ended = await WaitForAsync(engine, Finished, 5);
        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Play_CancelledWhileOpening_ThrowsCancellationAndTheNextPlayWorks()
    {
        using ManualResetEventSlim gate = new(false);
        using AudioTestServer server = new(Fixture("fixture.mp3"), live: false) { Gate = gate };
        using HttpClient client = new();
        using var engine = NewEngine(client);
        using CancellationTokenSource cancel = new();
        await engine.LoadAsync(TrackFor(server.Address, 1));
        Task opening = engine.PlayAsync(cancel.Token);

        await cancel.CancelAsync();

        await Assert.ThatAsync(() => opening, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(engine.Snapshot.Status, Is.Not.EqualTo(PlaybackStatus.Faulted), "cancelling is not a failure");
        gate.Set();
        await engine.LoadAsync(TrackFor("fixture.wav"));
        await engine.PlayAsync();
        PlaybackSnapshot ended = await WaitForAsync(engine, Finished, 5);
        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Pipeline_SourceThatFailsAfterTheFirstChunk_ReportsTheFailureAndFinishes()
    {
        using ManualResetEventSlim proceed = new(false);
        using DecodePipeline pipeline = new(new FailingSource(failOnRead: 6, proceed), 48000, 0);

        // A quarter second (three chunks) is enough to start, and the source fails after five, so the pipeline becomes ready; the later failure is then reported on the pipeline itself.
        // The failing read waits for this point, so a slow wake-up of the waiting thread cannot let the failure overtake the ready signal.
        pipeline.WaitUntilReady(TimeSpan.FromSeconds(5), CancellationToken.None);
        proceed.Set();
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (pipeline.Failure is null && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.That(pipeline.Failure, Is.InstanceOf<InvalidDataException>().With.Message.Contains("damaged"));
        float[] drain = new float[4096 * 2];
        while (!pipeline.Finished && DateTime.UtcNow < deadline) { pipeline.Render(drain); await Task.Delay(5); }
        Assert.That(pipeline.Finished, Is.True, "a failed source ends the pipeline so the engine can report it");
    }

    [Test]
    public void Pipeline_SourceThatFailsImmediately_FailsWaitUntilReady()
    {
        using DecodePipeline pipeline = new(new FailingSource(failOnRead: 1), 48000, 0);

        Assert.That(() => pipeline.WaitUntilReady(TimeSpan.FromSeconds(5), CancellationToken.None), Throws.InstanceOf<InvalidDataException>());
    }

    private sealed class FailingSource(int failOnRead, ManualResetEventSlim? beforeFailing = null) : IPcmSource
    {
        private int reads;

        public int Read(Span<float> destination, int frames)
        {
            if (++reads >= failOnRead)
            {
                beforeFailing?.Wait(TimeSpan.FromSeconds(10));
                throw new InvalidDataException("The audio data is damaged (test).");
            }
            destination[..(frames * NativeAudio.Channels)].Fill(0.1f);
            return frames;
        }

        public bool Seek(long frame) => true;

        public void Dispose()
        {
        }
    }
}
