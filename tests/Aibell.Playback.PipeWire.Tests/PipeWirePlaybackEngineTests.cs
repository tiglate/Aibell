using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Playback.PipeWire;
using NUnit.Framework;

namespace Aibell.Playback.PipeWire.Tests;

/// <summary>
/// Checks the PipeWire player's transport behaviour. The daemon is only contacted when a track is
/// played, so these checks run on any machine.
/// </summary>
[TestFixture]
public sealed class PipeWirePlaybackEngineTests
{
    private static Track ATrack() =>
        new(Guid.NewGuid(), new Uri("file:///tmp/aibell-test-track.wav"), "Test", "Artist", "Album", TimeSpan.FromMinutes(3));

    [Test]
    public void A_New_Engine_Is_Stopped()
    {
        using var engine = new PipeWirePlaybackEngine();

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        Assert.That(engine.Snapshot.Track, Is.Null);
    }

    [Test]
    public void Loading_A_Track_Keeps_It_Stopped()
    {
        using var engine = new PipeWirePlaybackEngine();
        Track track = ATrack();

        engine.LoadAsync(track).GetAwaiter().GetResult();

        Assert.That(engine.Snapshot.Track, Is.EqualTo(track));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [Test]
    public async Task Playing_Without_A_Track_Is_An_Error()
    {
        using var engine = new PipeWirePlaybackEngine();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await engine.PlayAsync());
    }

    [Test]
    public void The_Volume_Is_Kept()
    {
        using var engine = new PipeWirePlaybackEngine();

        engine.SetVolumeAsync(0.25).GetAwaiter().GetResult();

        Assert.That(engine.Snapshot.Volume, Is.EqualTo(0.25));
    }

    [Test]
    public async Task A_Volume_Outside_The_Range_Is_Brought_Back_Into_It()
    {
        using var engine = new PipeWirePlaybackEngine();

        await engine.SetVolumeAsync(2);

        Assert.That(engine.Snapshot.Volume, Is.EqualTo(1));
    }

    [Test]
    public async Task A_Volume_That_Is_Not_A_Number_Is_An_Error()
    {
        using var engine = new PipeWirePlaybackEngine();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await engine.SetVolumeAsync(double.NaN));
    }

    [Test]
    public void The_Mute_Is_Kept()
    {
        using var engine = new PipeWirePlaybackEngine();

        engine.SetMutedAsync(true).GetAwaiter().GetResult();

        Assert.That(engine.Snapshot.IsMuted, Is.True);
    }

    [Test]
    public void The_Playback_Rate_Is_Kept()
    {
        using var engine = new PipeWirePlaybackEngine();

        engine.SetRateAsync(1.5).GetAwaiter().GetResult();

        Assert.That(engine.Rate, Is.EqualTo(1.5));
    }

    [Test]
    public async Task A_Playback_Rate_Outside_The_Range_Is_An_Error()
    {
        using var engine = new PipeWirePlaybackEngine();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await engine.SetRateAsync(0.1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await engine.SetRateAsync(10));
    }

    [Test]
    public void The_Equalizer_Is_Kept()
    {
        using var engine = new PipeWirePlaybackEngine();
        EqualizerSettings settings = EqualizerSettings.Flat with { IsEnabled = true, Preamp = 3 };

        engine.SetEqualizerAsync(settings).GetAwaiter().GetResult();

        Assert.That(engine.Equalizer.IsEnabled, Is.True);
    }

    [Test]
    public async Task Stopping_Without_Playing_Is_Harmless()
    {
        using var engine = new PipeWirePlaybackEngine();

        await Assert.DoesNotThrowAsync(async () => await engine.StopAsync());
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [Test]
    public async Task Pausing_Without_Playing_Is_Harmless()
    {
        using var engine = new PipeWirePlaybackEngine();

        await Assert.DoesNotThrowAsync(async () => await engine.PauseAsync());
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [Test]
    public async Task Seeking_Without_A_Track_Is_Harmless()
    {
        using var engine = new PipeWirePlaybackEngine();

        await Assert.DoesNotThrowAsync(async () => await engine.SeekAsync(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public async Task Seeking_With_A_Loaded_Track_Updates_The_Position()
    {
        using var engine = new PipeWirePlaybackEngine();
        await engine.LoadAsync(ATrack());

        await engine.SeekAsync(TimeSpan.FromSeconds(15));

        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(15)));
    }

    [Test]
    public void The_State_Change_Reaches_The_Listener()
    {
        using var engine = new PipeWirePlaybackEngine();
        List<PlaybackSnapshot> seen = [];
        engine.StateChanged += (_, snapshot) => seen.Add(snapshot);

        engine.LoadAsync(ATrack()).GetAwaiter().GetResult();

        Assert.That(seen, Is.Not.Empty);
        Assert.That(seen[^1].Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [Test]
    public void Disposing_Twice_Is_Harmless()
    {
        var engine = new PipeWirePlaybackEngine();

        engine.Dispose();
        Assert.DoesNotThrow(engine.Dispose);
    }

    [Test]
    public void FormatPod_And_ReadFormats_RoundTrip_The_Negotiated_Audio_Format()
    {
        AudioFormat expected = new(48000, 2, SpaAudioFormat.F32Le);
        SpaPod formatObj = PipeWireStream.FormatPod(expected, SpaParams.EnumFormat);
        byte[] encoded = formatObj.Write();
        SpaPod decoded = SpaPod.Read(encoded);

        Assert.That(decoded.ObjectType, Is.EqualTo(SpaObjectType.Format));
        Assert.That(decoded.ObjectId, Is.EqualTo(SpaParams.EnumFormat));

        SpaPod wrapper = SpaPod.Struct(decoded);
        List<AudioFormat> formats = PipeWireStream.ReadFormats(wrapper);
        Assert.That(formats, Has.Count.EqualTo(1));
        Assert.That(formats[0].SampleRate, Is.EqualTo(48000u));
        Assert.That(formats[0].Channels, Is.EqualTo(2u));
        Assert.That(formats[0].FramesPerSecond, Is.EqualTo(48000));
    }
}
