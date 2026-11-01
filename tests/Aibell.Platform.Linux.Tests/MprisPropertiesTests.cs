using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using NUnit.Framework;
using Tmds.DBus.Protocol;

namespace Aibell.Platform.Linux.Tests;

[TestFixture]
public sealed class MprisPropertiesTests
{
    private static readonly string[] ArtistNames = ["Artist"];
    internal static MediaSessionState State(PlaybackStatus status = PlaybackStatus.Playing) => new(
        new(status, new(Guid.Parse("11111111-2222-3333-4444-555555555555"), new Uri("file:///music/tone.flac"), "Title", "Artist", "Album", TimeSpan.FromSeconds(60)),
            TimeSpan.FromSeconds(2), 0.8), new(Title: "Library title 日本語", Artist: "Artist", Album: "Album", TrackNumber: 2),
        new Uri("file:///cache/cover.png"), true, true, true, true, false, true, RepeatMode.One);

    [Test]
    public void Player_TrackMetadata_UsesTypedPathsArraysAndMicroseconds()
    {
        MediaSessionState state = State();
        Dictionary<string, VariantValue> player = MprisProperties.Player(state);
        Dictionary<string, VariantValue> metadata = player["Metadata"].GetDictionary<string, VariantValue>();
        Assert.Multiple(() =>
        {
            Assert.That(player["Position"].GetInt64(), Is.EqualTo(2_000_000));
            Assert.That(player["LoopStatus"].GetString(), Is.EqualTo("Track"));
            Assert.That(metadata["mpris:length"].GetInt64(), Is.EqualTo(60_000_000));
            Assert.That(metadata["mpris:trackid"].GetObjectPathAsString(), Is.EqualTo(MprisProperties.TrackPath(state.Playback.Track!.Id)));
            Assert.That(metadata["xesam:title"].GetString(), Is.EqualTo("Library title 日本語"));
            Assert.That(metadata["xesam:artist"].GetArray<string>(), Is.EqualTo(ArtistNames));
            Assert.That(metadata["xesam:trackNumber"].GetInt32(), Is.EqualTo(2));
            Assert.That(metadata["mpris:artUrl"].GetString(), Is.EqualTo(state.Artwork!.AbsoluteUri));
        });
    }

    [TestCase(PlaybackStatus.Stopped, "Stopped")]
    [TestCase(PlaybackStatus.Playing, "Playing")]
    [TestCase(PlaybackStatus.Paused, "Paused")]
    [TestCase(PlaybackStatus.Ended, "Stopped")]
    [TestCase(PlaybackStatus.Faulted, "Stopped")]
    public void Player_EngineStatus_MapsToMprisStatus(PlaybackStatus status, string expected) =>
        Assert.That(MprisProperties.Player(State(status))["PlaybackStatus"].GetString(), Is.EqualTo(expected));

    [Test]
    public void Root_DesktopEntry_NamesThePackagedDesktopFileWithoutExtension() =>
        Assert.That(MprisProperties.Root()["DesktopEntry"].GetString(), Is.EqualTo("io.github.tiglate.Aibell"));

    [Test]
    public void Player_NoTrackAndMuted_ReportsEmptyMetadataAndEffectiveVolume()
    {
        MediaSessionState original = State();
        MediaSessionState state = original with { Playback = original.Playback with { Track = null, IsMuted = true } };
        Dictionary<string, VariantValue> player = MprisProperties.Player(state);
        Assert.That(player["Metadata"].GetDictionary<string, VariantValue>(), Is.Empty);
        Assert.That(player["Volume"].GetDouble(), Is.Zero);
        Assert.That(MprisProperties.Root()["SupportedUriSchemes"].GetArray<string>(), Is.Empty);
    }

    [Test]
    public void SetProperty_ValidValues_ProducesTypedCommands()
    {
        Assert.That(MprisProperties.SetProperty("Volume", -1d), Is.EqualTo(new MediaSessionCommand(MediaSessionAction.SetVolume, Volume: 0)));
        Assert.That(MprisProperties.SetProperty("Volume", 2d), Is.EqualTo(new MediaSessionCommand(MediaSessionAction.SetVolume, Volume: 1)));
        Assert.That(MprisProperties.SetProperty("Shuffle", true), Is.EqualTo(new MediaSessionCommand(MediaSessionAction.SetShuffle, Shuffle: true)));
        Assert.That(MprisProperties.SetProperty("LoopStatus", "Playlist"), Is.EqualTo(new MediaSessionCommand(MediaSessionAction.SetRepeat, Repeat: RepeatMode.All)));
        Assert.That(MprisProperties.SetProperty("Rate", 0d), Is.EqualTo(new MediaSessionCommand(MediaSessionAction.Pause)));
        Assert.That(MprisProperties.SetProperty("Rate", 1d), Is.Null);
    }

    [Test]
    public void SetProperty_InvalidOrReadOnlyValues_Rejects()
    {
        foreach ((string name, VariantValue value) in new (string, VariantValue)[]
            { ("Volume", double.NaN), ("Volume", double.PositiveInfinity), ("Volume", "1"),
              ("Shuffle", 1), ("LoopStatus", "Invalid"), ("Rate", 2d), ("Position", 1L) })
            Assert.That(() => MprisProperties.SetProperty(name, value), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task StartAsync_MissingBus_IsOptionalAndDisposalIsIdempotent()
    {
        await using MprisMediaSession session = new("");
        Assert.That(async () => await session.StartAsync(new TestController()), Throws.TypeOf<InvalidOperationException>());
        await session.DisposeAsync();
    }
}

internal sealed class TestController : IMediaSessionController
{
    public MediaSessionState State { get; set; } = MprisPropertiesTests.State();
    public List<MediaSessionCommand> Commands { get; } = [];
    public event EventHandler? StateChanged;
    public event EventHandler<TimeSpan>? Seeked;
    public Task ExecuteAsync(MediaSessionCommand command, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Commands.Add(command); return Task.CompletedTask; }
    public void Publish() => StateChanged?.Invoke(this, EventArgs.Empty);
    public void PublishSeek(TimeSpan position) => Seeked?.Invoke(this, position);
}
