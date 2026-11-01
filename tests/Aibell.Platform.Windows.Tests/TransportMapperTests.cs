using Aibell.Application.Playback;
using Aibell.Platform.Windows.Native;
using NUnit.Framework;

namespace Aibell.Platform.Windows.Tests;

[TestFixture]
internal sealed class TransportMapperTests
{
    [TestCase(PlaybackStatus.Playing, TransportPlayback.Playing)]
    [TestCase(PlaybackStatus.Paused, TransportPlayback.Paused)]
    [TestCase(PlaybackStatus.Stopped, TransportPlayback.Stopped)]
    [TestCase(PlaybackStatus.Ended, TransportPlayback.Stopped)]
    [TestCase(PlaybackStatus.Faulted, TransportPlayback.Stopped)]
    public void Map_PlaybackStatus_UsesMediaPlaybackStatus(PlaybackStatus status, TransportPlayback expected) =>
        Assert.That(TransportMapper.Map(TestStates.Playing(status)).Playback, Is.EqualTo(expected));

    [Test]
    public void Map_Track_PublishesMetadataAndButtonAvailability()
    {
        TransportSnapshot snapshot = TransportMapper.Map(TestStates.Playing(title: "日本語 Title"));
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.HasTrack, Is.True);
            Assert.That(snapshot.Title, Is.EqualTo("日本語 Title"));
            Assert.That(snapshot.Artist, Is.EqualTo("Artist"));
            Assert.That(snapshot.AlbumArtist, Is.EqualTo("Album Artist"));
            Assert.That(snapshot.Album, Is.EqualTo("Album"));
            Assert.That(snapshot.PlayEnabled && snapshot.PauseEnabled && snapshot.StopEnabled && snapshot.NextEnabled, Is.True);
            Assert.That(snapshot.PreviousEnabled, Is.False);
        });
    }

    [Test]
    public void Map_NoMetadata_FallsBackToTrackTags() =>
        Assert.That(TransportMapper.Map(TestStates.Playing() with { Metadata = null }).Title, Is.EqualTo("Track"));

    [Test]
    public void Map_LocalFileArtwork_PublishesItsLocalPath() =>
        Assert.That(TransportMapper.Map(TestStates.Playing() with { Artwork = new Uri(@"C:\Music\Album\cover.jpg") }).ArtworkPath,
            Is.EqualTo(@"C:\Music\Album\cover.jpg"));

    [TestCase(null)]
    [TestCase("https://example.test/cover.jpg")]
    [TestCase("file://server/share/cover.jpg")]
    public void Map_NoLocalArtwork_PublishesNoPath(string? location) =>
        Assert.That(TransportMapper.Map(TestStates.Playing() with { Artwork = location is null ? null : new Uri(location) }).ArtworkPath, Is.Empty);

    [Test]
    public void Map_StoppedTrack_DisablesStop() =>
        Assert.That(TransportMapper.Map(TestStates.Playing(PlaybackStatus.Stopped)).StopEnabled, Is.False);

    [Test]
    public void Map_NoTrack_IsClosedAndInert()
    {
        TransportSnapshot snapshot = TransportMapper.Map(TestStates.Idle);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.EqualTo(TransportSnapshot.Empty));
            Assert.That(snapshot.Playback, Is.EqualTo(TransportPlayback.Closed));
        });
    }

    [TestCase(TransportButton.Play, MediaSessionAction.Play)]
    [TestCase(TransportButton.Pause, MediaSessionAction.Pause)]
    [TestCase(TransportButton.Stop, MediaSessionAction.Stop)]
    [TestCase(TransportButton.Next, MediaSessionAction.Next)]
    [TestCase(TransportButton.Previous, MediaSessionAction.Previous)]
    public void ToCommand_SupportedButton_MapsToAction(TransportButton button, MediaSessionAction expected) =>
        Assert.That(TransportMapper.ToCommand(button)?.Action, Is.EqualTo(expected));

    [TestCase(TransportButton.Record)]
    [TestCase(TransportButton.FastForward)]
    [TestCase(TransportButton.Rewind)]
    [TestCase(TransportButton.ChannelUp)]
    [TestCase(TransportButton.ChannelDown)]
    [TestCase((TransportButton)99)]
    public void ToCommand_UnsupportedButton_IsIgnored(TransportButton button) =>
        Assert.That(TransportMapper.ToCommand(button), Is.Null);

    [Test]
    public void NativeContract_ButtonAndStatusValues_MatchWindowsMedia()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)TransportButton.Next, Is.EqualTo(6));
            Assert.That((int)TransportButton.Previous, Is.EqualTo(7));
            Assert.That((int)TransportPlayback.Playing, Is.EqualTo(3));
            Assert.That((int)TransportPlayback.Paused, Is.EqualTo(4));
            Assert.That(SmtcSlots.AddButtonPressed, Is.EqualTo(32));
            Assert.That(SmtcSlots.RemoveButtonPressed, Is.EqualTo(SmtcSlots.AddButtonPressed + 1));
            Assert.That(SmtcSlots.PutIsEnabled, Is.EqualTo(11));
            Assert.That(SmtcSlots.UpdaterUpdate, Is.EqualTo(SmtcSlots.UpdaterClearAll + 1));
            Assert.That(SmtcSlots.MusicPutArtist, Is.EqualTo(SmtcSlots.MusicPutAlbumArtist + 2));
            Assert.That(SmtcSlots.UpdaterPutThumbnail, Is.EqualTo(SmtcSlots.UpdaterGetThumbnail + 1));
            Assert.That(SmtcSlots.UpdaterGetMusicProperties, Is.EqualTo(SmtcSlots.UpdaterPutThumbnail + 1));
        });
    }

    [Test]
    public void NativeContract_MessageLayout_MatchesWindowsMsg() =>
        Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Message>(), Is.EqualTo(nint.Size == 8 ? 48 : 32));
}
