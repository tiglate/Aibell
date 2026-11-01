using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Playback.Alsa;
using NUnit.Framework;

namespace Aibell.Playback.Alsa.Tests;

/// <summary>
/// Checks the ALSA player's behaviour. The sound device is only opened when a track is played, so
/// the transport can be exercised on a machine without one.
/// </summary>
[TestFixture]
public sealed class AlsaPlaybackEngineTests
{
    private static Track ATrack() =>
        new(Guid.NewGuid(), new Uri("file:///tmp/aibell-test-track.wav"), "Test", "Artist", "Album", TimeSpan.FromMinutes(3));

    [Test]
    public void A_New_Engine_Is_Stopped()
    {
        using var engine = new AlsaPlaybackEngine();

        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        Assert.That(engine.Snapshot.Track, Is.Null);
    }

    [Test]
    public void Loading_A_Track_Keeps_It_Stopped()
    {
        using var engine = new AlsaPlaybackEngine();
        Track track = ATrack();

        engine.LoadAsync(track).GetAwaiter().GetResult();

        Assert.That(engine.Snapshot.Track, Is.EqualTo(track));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [Test]
    public async Task Playing_Without_A_Track_Is_An_Error()
    {
        using var engine = new AlsaPlaybackEngine();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await engine.PlayAsync());
    }

    [Test]
    public void The_Volume_Is_Kept()
    {
        using var engine = new AlsaPlaybackEngine();

        engine.SetVolumeAsync(0.25).GetAwaiter().GetResult();

        Assert.That(engine.Snapshot.Volume, Is.EqualTo(0.25));
    }

    [Test]
    public async Task A_Volume_Outside_The_Range_Is_Brought_Back_Into_It()
    {
        using var engine = new AlsaPlaybackEngine();

        await engine.SetVolumeAsync(2);

        Assert.That(engine.Snapshot.Volume, Is.EqualTo(1));
    }

    [Test]
    public async Task A_Volume_That_Is_Not_A_Number_Is_An_Error()
    {
        using var engine = new AlsaPlaybackEngine();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await engine.SetVolumeAsync(double.NaN));
    }

    [Test]
    public void The_Mute_Is_Kept()
    {
        using var engine = new AlsaPlaybackEngine();

        engine.SetMutedAsync(true).GetAwaiter().GetResult();

        Assert.That(engine.Snapshot.IsMuted, Is.True);
    }

    [Test]
    public void The_Playback_Rate_Is_Kept()
    {
        using var engine = new AlsaPlaybackEngine();

        engine.SetRateAsync(1.5).GetAwaiter().GetResult();

        Assert.That(engine.Rate, Is.EqualTo(1.5));
    }

    [Test]
    public async Task A_Playback_Rate_Outside_The_Range_Is_An_Error()
    {
        using var engine = new AlsaPlaybackEngine();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await engine.SetRateAsync(0.1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await engine.SetRateAsync(10));
    }

    [Test]
    public void The_Equalizer_Is_Kept()
    {
        using var engine = new AlsaPlaybackEngine();
        EqualizerSettings settings = EqualizerSettings.Flat with { IsEnabled = true, Preamp = 3 };

        engine.SetEqualizerAsync(settings).GetAwaiter().GetResult();

        Assert.That(engine.Equalizer.IsEnabled, Is.True);
    }

    [Test]
    public async Task Stopping_Without_Playing_Is_Harmless()
    {
        using var engine = new AlsaPlaybackEngine();

        await Assert.DoesNotThrowAsync(async () => await engine.StopAsync());
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [Test]
    public async Task Seeking_Without_A_Track_Is_Harmless()
    {
        using var engine = new AlsaPlaybackEngine();

        await Assert.DoesNotThrowAsync(async () => await engine.SeekAsync(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void The_State_Change_Reaches_The_Listener()
    {
        using var engine = new AlsaPlaybackEngine();
        List<PlaybackSnapshot> seen = [];
        engine.StateChanged += (_, snapshot) => seen.Add(snapshot);

        engine.LoadAsync(ATrack()).GetAwaiter().GetResult();

        Assert.That(seen, Is.Not.Empty);
        Assert.That(seen[^1].Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [Test]
    public void Disposing_Twice_Is_Harmless()
    {
        var engine = new AlsaPlaybackEngine();

        engine.Dispose();
        Assert.DoesNotThrow(engine.Dispose);
    }
}
