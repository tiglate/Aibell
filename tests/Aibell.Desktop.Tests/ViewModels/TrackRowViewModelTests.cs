using Aibell.Application.Playback;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.ViewModels;

[TestFixture]
public sealed class TrackRowViewModelTests
{
    [TestCase(PlaybackStatus.Playing, true, false, "Playing")]
    [TestCase(PlaybackStatus.Paused, false, true, "Paused")]
    [TestCase(PlaybackStatus.Stopped, false, false, "")]
    [TestCase(PlaybackStatus.Ended, false, false, "")]
    [TestCase(PlaybackStatus.Faulted, false, false, "")]
    public void UpdatePlayback_CurrentTrack_ShowsOnlyTheActiveState(
        PlaybackStatus status,
        bool isPlaying,
        bool isPaused,
        string text)
    {
        Track track = CreateTrack();
        TrackRowViewModel row = TrackRowViewModel.FromTrack(track, [], false, false);

        row.UpdatePlayback(new PlaybackSnapshot(status, track, TimeSpan.Zero, 0.8));

        Assert.Multiple(() =>
        {
            Assert.That(row.IsPlaying, Is.EqualTo(isPlaying));
            Assert.That(row.IsPaused, Is.EqualTo(isPaused));
            Assert.That(row.HasPlaybackIndicator, Is.EqualTo(isPlaying || isPaused));
            Assert.That(row.PlaybackIndicatorText, Is.EqualTo(text));
        });
    }

    [Test]
    public void UpdatePlayback_DifferentTrackOrNoTrack_ClearsIndicator()
    {
        Track track = CreateTrack();
        TrackRowViewModel row = TrackRowViewModel.FromTrack(track, [], false, false);
        row.UpdatePlayback(new PlaybackSnapshot(PlaybackStatus.Playing, track, TimeSpan.Zero, 0.8));

        row.UpdatePlayback(new PlaybackSnapshot(
            PlaybackStatus.Playing, track with { Id = Guid.NewGuid() }, TimeSpan.Zero, 0.8));
        Assert.That(row.HasPlaybackIndicator, Is.False);

        row.UpdatePlayback(new PlaybackSnapshot(PlaybackStatus.Paused, track, TimeSpan.Zero, 0.8));
        row.UpdatePlayback(new PlaybackSnapshot(PlaybackStatus.Paused, null, TimeSpan.Zero, 0.8));
        Assert.That(row.HasPlaybackIndicator, Is.False);
    }

    [Test]
    public void UpdatePlayback_PositionAndVolumeChanges_DoNotNotifyRowBindings()
    {
        Track track = CreateTrack();
        TrackRowViewModel row = TrackRowViewModel.FromTrack(track, [], false, false);
        row.UpdatePlayback(new PlaybackSnapshot(PlaybackStatus.Playing, track, TimeSpan.Zero, 0.8));
        List<string?> notifications = [];
        row.PropertyChanged += (_, eventArgs) => notifications.Add(eventArgs.PropertyName);

        row.UpdatePlayback(new PlaybackSnapshot(
            PlaybackStatus.Playing, track, TimeSpan.FromSeconds(10), 0.5, true));

        Assert.That(notifications, Is.Empty);
    }

    [Test]
    public void UpdatePlayback_Pause_NotifiesIconBindingsAndPreservesPlaylistDragHandle()
    {
        Track track = CreateTrack();
        TrackRowViewModel row = TrackRowViewModel.FromTrack(track, [], true, true);
        row.UpdatePlayback(new PlaybackSnapshot(PlaybackStatus.Playing, track, TimeSpan.Zero, 0.8));
        List<string?> notifications = [];
        row.PropertyChanged += (_, eventArgs) => notifications.Add(eventArgs.PropertyName);

        row.UpdatePlayback(new PlaybackSnapshot(PlaybackStatus.Paused, track, TimeSpan.Zero, 0.8));

        Assert.Multiple(() =>
        {
            Assert.That(notifications, Does.Contain(nameof(row.IsPlaying)));
            Assert.That(notifications, Does.Contain(nameof(row.IsPaused)));
            Assert.That(notifications, Does.Contain(nameof(row.PlaybackIndicatorText)));
            Assert.That(row.IsPlaying, Is.False);
            Assert.That(row.IsPaused, Is.True);
            Assert.That(row.CanReorder, Is.True);
        });
    }

    [Test]
    public void ToString_Row_IsTheSpokenNameNotTheTypeName() =>
        Assert.That(TrackRowViewModel.FromTrack(CreateTrack(), [], false, false).ToString(),
            Is.EqualTo("Cold Cold Heart, Norah Jones, Come Away With Me, 3:00"));

    [Test]
    public void ToString_MissingTags_SkipsEmptyParts()
    {
        Track track = CreateTrack() with { Artist = "", Album = " " };
        Assert.That(TrackRowViewModel.FromTrack(track, [], false, false).ToString(), Is.EqualTo("Cold Cold Heart, 3:00"));
    }

    [Test]
    public void LibrarySource_ToString_ReadsAsNameAndPath()
    {
        // Built from the platform's own separator and root: a Windows path is not a folder path on Linux, and the reverse.
        string root = System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath())!;
        string path = System.IO.Path.Combine(root, "Users", "me", "Music") + System.IO.Path.DirectorySeparatorChar;
        Assert.That(new LibrarySourceViewModel(path).ToString(), Is.EqualTo($"Music, {path}"));
    }

    [Test]
    public void LibrarySource_ToString_ForADriveOrFilesystemRoot_IsJustThePath()
    {
        string root = System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath())!;
        Assert.That(new LibrarySourceViewModel(root).ToString(), Is.EqualTo(root));
    }

    private static Track CreateTrack()
    {
        return new Track(Guid.NewGuid(), new Uri("file:///music/track.flac"),
            "Cold Cold Heart", "Norah Jones", "Come Away With Me", TimeSpan.FromMinutes(3));
    }
}
