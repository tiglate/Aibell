using Aibell.Application.Playback;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Application.Tests.Playback;

[TestFixture]
public sealed class PlaybackSnapshotRulesTests
{
    [TestCase(PlaybackStatus.Ended)]
    [TestCase(PlaybackStatus.Faulted)]
    public void AfterNativeStop_TerminalStatus_PreservesPositionAndError(PlaybackStatus status)
    {
        PlaybackSnapshot snapshot = Snapshot(status) with { Error = status == PlaybackStatus.Faulted ? "Cannot decode audio." : null };

        PlaybackSnapshot stopped = PlaybackSnapshotRules.AfterNativeStop(snapshot);

        Assert.That(stopped, Is.SameAs(snapshot));
        Assert.That(PlaybackSnapshotRules.WithEnginePosition(stopped, TimeSpan.Zero), Is.SameAs(snapshot));
    }

    [TestCase(PlaybackStatus.Playing)]
    [TestCase(PlaybackStatus.Paused)]
    public void AfterNativeStop_ActivePlayback_ResetsPositionAndRetainsTrack(PlaybackStatus status)
    {
        PlaybackSnapshot snapshot = Snapshot(status);
        PlaybackSnapshot stopped = PlaybackSnapshotRules.AfterNativeStop(snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(stopped.Status, Is.EqualTo(PlaybackStatus.Stopped));
            Assert.That(stopped.Position, Is.EqualTo(TimeSpan.Zero));
            Assert.That(stopped.Track, Is.SameAs(snapshot.Track));
            Assert.That(stopped.Volume, Is.EqualTo(snapshot.Volume));
        });
    }

    [TestCase(-100, 0)]
    [TestCase(500, 500)]
    [TestCase(2000, 1000)]
    public void WithEnginePosition_ActiveTrack_ClampsToTrackDuration(int milliseconds, int expected)
    {
        PlaybackSnapshot updated = PlaybackSnapshotRules.WithEnginePosition(Snapshot(PlaybackStatus.Playing),
            TimeSpan.FromMilliseconds(milliseconds));

        Assert.That(updated.Position, Is.EqualTo(TimeSpan.FromMilliseconds(expected)));
    }

    [Test]
    public void WithEnginePosition_UnloadedPlayer_IgnoresLateNativeClock()
    {
        PlaybackSnapshot unloaded = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8);

        Assert.That(PlaybackSnapshotRules.WithEnginePosition(unloaded, TimeSpan.FromSeconds(5)), Is.SameAs(unloaded));
    }

    [TestCase("https://example.invalid/live", 0, 25000)]
    [TestCase("file:///music/unknown.flac", 0, 0)]
    [TestCase("https://example.invalid/finite.mp3", 1000, 1000)]
    public void WithEnginePosition_UnknownStreamDuration_KeepsElapsedTimeWithoutSeeking(string address, int duration, int expected)
    {
        PlaybackSnapshot snapshot = Snapshot(PlaybackStatus.Playing);
        snapshot = snapshot with { Track = snapshot.Track! with { Location = new Uri(address), Duration = TimeSpan.FromMilliseconds(duration) } };
        Assert.That(PlaybackSnapshotRules.WithEnginePosition(snapshot, TimeSpan.FromSeconds(25)).Position,
            Is.EqualTo(TimeSpan.FromMilliseconds(expected)));
    }

    private static PlaybackSnapshot Snapshot(PlaybackStatus status) => new(status,
        new Track(Guid.NewGuid(), new Uri("file:///music/track.flac"), "Track", "Artist", "Album", TimeSpan.FromSeconds(1)),
        TimeSpan.FromSeconds(1), 0.25, true);
}
