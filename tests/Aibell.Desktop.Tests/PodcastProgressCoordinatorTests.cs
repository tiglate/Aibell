using Aibell.Application.Playback;
using Aibell.Desktop.Services;
using Aibell.Domain.Media;
using Aibell.Domain.Podcasts;
using NUnit.Framework;

namespace Aibell.Desktop.Tests;

[TestFixture]
public sealed class PodcastProgressCoordinatorTests
{
    private readonly List<(StoredPodcastEpisode Episode, TimeSpan Position, TimeSpan Duration)> writes = [];
    private static readonly string[] EpisodeIds = ["a", "b"];
    private static readonly double[] RapidPositions = [12d, 34d, 56d];
    private static readonly double[] ReplayPositions = [11d, 19d];
    private static readonly double[] ReplacementPositions = [91d, 2d];
    private static readonly double[] ShutdownPositions = [10d, 30d];
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);
    private static StoredPodcastEpisode Episode(string name = "a") => new(Guid.NewGuid(), name, name,
        new Uri("https://example.invalid/" + name + ".mp3"), LocalPath: Path.Combine(Path.GetTempPath(), name + ".mp3"),
        DownloadedBytes: 100, DownloadedAt: DateTimeOffset.UnixEpoch);
    private static PlaybackSnapshot Snapshot(StoredPodcastEpisode episode, int seconds,
        PlaybackStatus status = PlaybackStatus.Playing) => new(status,
        new Track(Guid.NewGuid(), new Uri(episode.LocalPath!), episode.Title, "Show", "Show", Duration), TimeSpan.FromSeconds(seconds), .5);
    private Task<StoredPodcastEpisode> Save(StoredPodcastEpisode episode, TimeSpan position, TimeSpan duration, CancellationToken token)
    {
        writes.Add((episode, position, duration));
        return Task.FromResult(episode with { SavedProgress = new(position, duration, DateTimeOffset.UnixEpoch) });
    }
    [SetUp] public void SetUp() => writes.Clear();

    [Test]
    public async Task Tick_KnownEpisodeSavesOnceAndNotifies_WithoutAnyViewLifetime()
    {
        await using PodcastProgressCoordinator progress = new(Save, startTimer: false);
        StoredPodcastEpisode episode = Episode();
        StoredPodcastEpisode? notified = null;
        progress.PositionSaved += (_, value) => notified = value;
        progress.Register(episode, new Uri(episode.LocalPath!));
        progress.Observe(Snapshot(episode, 17));
        Assert.That(writes, Is.Empty);
        await progress.TickAsync();
        await progress.TickAsync();
        Assert.That(writes, Has.Count.EqualTo(1));
        Assert.That(notified!.SavedProgress!.Position, Is.EqualTo(TimeSpan.FromSeconds(17)));
    }

    [TestCase(PlaybackStatus.Paused)]
    [TestCase(PlaybackStatus.Stopped)]
    [TestCase(PlaybackStatus.Faulted)]
    public async Task StateBoundary_FlushesLastPosition_AndStoppedZeroDoesNotEraseIt(PlaybackStatus status)
    {
        await using PodcastProgressCoordinator progress = new(Save, startTimer: false);
        StoredPodcastEpisode episode = Episode();
        progress.Register(episode, new Uri(episode.LocalPath!));
        progress.Observe(Snapshot(episode, 73));
        progress.Observe(Snapshot(episode, status == PlaybackStatus.Paused ? 73 : 0, status));
        await progress.TickAsync();
        Assert.That(writes.Single().Position, Is.EqualTo(TimeSpan.FromSeconds(73)));
    }

    [Test]
    public async Task Ended_StoresCompletionBeforeIncomingTrack()
    {
        await using PodcastProgressCoordinator progress = new(Save, startTimer: false);
        StoredPodcastEpisode a = Episode(), b = Episode("b");
        progress.Register(a, new Uri(a.LocalPath!)); progress.Register(b, new Uri(b.LocalPath!));
        progress.Observe(Snapshot(a, 50));
        progress.Observe(Snapshot(a, 0, PlaybackStatus.Ended));
        progress.Observe(Snapshot(b, 2));
        await progress.TickAsync();
        Assert.That(writes.Select(w => w.Position), Is.EqualTo(new[] { Duration, TimeSpan.FromSeconds(2) }));
        Assert.That(writes.Select(w => w.Episode.EpisodeId), Is.EqualTo(EpisodeIds));
    }

    [Test]
    public async Task RapidAToBToA_CapturesEachOutgoingInputBeforeUiCanCoalesceEvents()
    {
        await using PodcastProgressCoordinator progress = new(Save, startTimer: false);
        StoredPodcastEpisode a = Episode(), b = Episode("b");
        progress.Register(a, new Uri(a.LocalPath!)); progress.Register(b, new Uri(b.LocalPath!));
        progress.Observe(Snapshot(a, 12)); progress.Observe(Snapshot(b, 34)); progress.Observe(Snapshot(a, 56));
        await progress.TickAsync();
        Assert.That(writes.Select(w => w.Position.TotalSeconds), Is.EqualTo(RapidPositions));
    }

    [Test]
    public async Task UnknownMusicRadioAndAudiobook_DoNotWrite_AndKnownQueueReplayStillDoes()
    {
        await using PodcastProgressCoordinator progress = new(Save, startTimer: false);
        StoredPodcastEpisode a = Episode(); progress.Register(a, new Uri(a.LocalPath!));
        progress.Observe(Snapshot(Episode("music"), 100));
        progress.Observe(Snapshot(Episode("book"), 100));
        progress.Observe(Snapshot(a, 5) with { Track = Snapshot(a, 5).Track! with { Location = new Uri("https://example.invalid/radio") } });
        await progress.TickAsync(); Assert.That(writes, Is.Empty);
        progress.Observe(Snapshot(a, 11)); await progress.TickAsync();
        progress.Observe(Snapshot(Episode("music"), 20));
        progress.Observe(Snapshot(a, 19)); await progress.TickAsync();
        Assert.That(writes.Select(w => w.Position.TotalSeconds), Is.EqualTo(ReplayPositions));
    }

    [Test]
    public async Task ReplacementRegistration_DoesNotRebindOutgoingSession()
    {
        await using PodcastProgressCoordinator progress = new(Save, startTimer: false);
        StoredPodcastEpisode old = Episode(); StoredPodcastEpisode replacement = old with { DownloadedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        progress.Register(old, new Uri(old.LocalPath!)); progress.Observe(Snapshot(old, 90));
        progress.Register(replacement, new Uri(old.LocalPath!));
        await progress.FlushAsync(Snapshot(old, 91));
        progress.Observe(Snapshot(old, 0, PlaybackStatus.Stopped));
        progress.Observe(Snapshot(replacement, 2)); await progress.TickAsync();
        Assert.That(writes.Select(w => w.Episode.DownloadedAt), Is.EqualTo(new[] { old.DownloadedAt, replacement.DownloadedAt }));
        Assert.That(writes.Select(w => w.Position.TotalSeconds), Is.EqualTo(ReplacementPositions));
    }

    [Test]
    public async Task SlowWrites_AreSerial_AndShutdownWaitsForAcceptedWrites()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrent = 0, maximum = 0;
        PodcastProgressCoordinator progress = new(async (episode, position, duration, token) =>
        {
            int current = Interlocked.Increment(ref concurrent); maximum = Math.Max(maximum, current);
            entered.TrySetResult(); await release.Task;
            StoredPodcastEpisode saved = await Save(episode, position, duration, token);
            Interlocked.Decrement(ref concurrent); return saved;
        }, startTimer: false);
        StoredPodcastEpisode a = Episode(); progress.Register(a, new Uri(a.LocalPath!));
        progress.Observe(Snapshot(a, 10)); Task tick = progress.TickAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        progress.Observe(Snapshot(a, 20)); Task close = progress.ShutdownAsync(Snapshot(a, 30));
        progress.Observe(Snapshot(a, 100));
        Assert.That(close.IsCompleted, Is.False); release.TrySetResult();
        await close.WaitAsync(TimeSpan.FromSeconds(5)); await tick;
        Assert.That(maximum, Is.EqualTo(1));
        Assert.That(writes.Select(w => w.Position.TotalSeconds), Is.EqualTo(ShutdownPositions));
        await progress.DisposeAsync();
    }

    [Test]
    public async Task FailedWrite_RetriesWithoutAbandoningFutureProgress()
    {
        int attempts = 0;
        await using PodcastProgressCoordinator progress = new((episode, position, duration, token) =>
            ++attempts == 1 ? Task.FromException<StoredPodcastEpisode>(new IOException("disk full"))
                : Save(episode, position, duration, token), startTimer: false);
        StoredPodcastEpisode a = Episode(); progress.Register(a, new Uri(a.LocalPath!));
        progress.Observe(Snapshot(a, 22)); await progress.TickAsync();
        Assert.That(progress.LastError, Is.TypeOf<IOException>());
        await progress.TickAsync(); Assert.That(progress.LastError, Is.Null);
        Assert.That(writes.Single().Position.TotalSeconds, Is.EqualTo(22));
    }

    [Test]
    public async Task PeriodicTimer_IsEnabledByDefault_AndUsesFiveSecondInjectedClock()
    {
        using ManualClock clock = new();
        await using PodcastProgressCoordinator progress = new(Save, clock);
        StoredPodcastEpisode episode = Episode(); progress.Register(episode, new Uri(episode.LocalPath!));
        TaskCompletionSource saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        progress.PositionSaved += (_, _) => saved.TrySetResult();
        progress.Observe(Snapshot(episode, 42));
        Assert.That(clock.Interval, Is.EqualTo(TimeSpan.FromSeconds(5)));
        clock.Fire(); await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await progress.ShutdownAsync(Snapshot(episode, 42));
        Assert.That(writes.Single().Position.TotalSeconds, Is.EqualTo(42));
        Assert.That(clock.Disposed, Is.True);
    }

    [Test]
    public async Task Dispose_IsIdempotent_AndFlushesLastObservedPosition()
    {
        PodcastProgressCoordinator progress = new(Save, startTimer: false);
        StoredPodcastEpisode episode = Episode(); progress.Register(episode, new Uri(episode.LocalPath!));
        progress.Observe(Snapshot(episode, 10));
        await progress.DisposeAsync(); await progress.DisposeAsync();
        progress.Observe(Snapshot(episode, 30)); await progress.TickAsync();
        await progress.ShutdownAsync(Snapshot(episode, 40));
        Assert.That(writes.Single().Position.TotalSeconds, Is.EqualTo(10));
    }

    [Test]
    public async Task ThrowingSubscriber_CannotTurnCommittedSaveIntoFailedWrite()
    {
        await using PodcastProgressCoordinator progress = new(Save, startTimer: false);
        progress.PositionSaved += (_, _) => throw new InvalidOperationException("consumer failed");
        StoredPodcastEpisode episode = Episode(); progress.Register(episode, new Uri(episode.LocalPath!));
        progress.Observe(Snapshot(episode, 9)); await progress.TickAsync(); await progress.TickAsync();
        Assert.That(progress.LastError, Is.Null);
        Assert.That(writes, Has.Count.EqualTo(1));
    }

    [Test]
    public void Register_RejectsUnrelatedFileAndUndownloadedEpisode()
    {
        PodcastProgressCoordinator progress = new(Save, startTimer: false);
        StoredPodcastEpisode episode = Episode();
        Assert.That(() => progress.Register(episode, new Uri(Episode("other").LocalPath!)), Throws.TypeOf<ArgumentException>());
        Assert.That(() => progress.Register(episode with { LocalPath = null }, new Uri(episode.LocalPath!)), Throws.TypeOf<ArgumentException>());
        Assert.That(() => progress.Register(episode, new Uri("https://example.invalid/audio")), Throws.TypeOf<ArgumentException>());
        progress.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class ManualClock : TimeProvider, IDisposable
    {
        private ManualTimer? timer;
        public TimeSpan Interval { get; private set; }
        public bool Disposed => timer?.Disposed ?? false;
        public void Fire() => timer!.Fire();
        public void Dispose() => timer?.Dispose();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interval = period;
            Assert.That(dueTime, Is.EqualTo(period));
            return timer = new(callback, state);
        }
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Test]
    public async Task UnknownDurationIncomingEpisode_FlushesOutgoingAndClearsItsActiveCapture()
    {
        await using PodcastProgressCoordinator progress = new(Save, startTimer: false);
        StoredPodcastEpisode a = Episode(), b = Episode("b");
        progress.Register(a, new Uri(a.LocalPath!)); progress.Register(b, new Uri(b.LocalPath!));
        progress.Observe(Snapshot(a, 12));
        PlaybackSnapshot incoming = Snapshot(b, 20);
        progress.Observe(incoming with { Track = incoming.Track! with { Duration = TimeSpan.Zero } });
        await progress.TickAsync(); await progress.TickAsync();
        Assert.That(writes.Single().Episode.EpisodeId, Is.EqualTo("a"));
        Assert.That(writes.Single().Position.TotalSeconds, Is.EqualTo(12));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(2592001)]
    public async Task InvalidEngineDuration_DoesNotReplacePreviousProgress(int durationSeconds)
    {
        await using PodcastProgressCoordinator progress = new(Save, startTimer: false);
        StoredPodcastEpisode a = Episode(); progress.Register(a, new Uri(a.LocalPath!));
        progress.Observe(Snapshot(a, 11)); await progress.TickAsync();
        PlaybackSnapshot invalid = Snapshot(a, 999);
        progress.Observe(invalid with { Track = invalid.Track! with { Duration = TimeSpan.FromSeconds(durationSeconds) } });
        await progress.TickAsync(); Assert.That(writes, Has.Count.EqualTo(1));
    }
}
