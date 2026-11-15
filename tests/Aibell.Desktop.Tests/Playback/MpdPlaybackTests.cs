using System.ComponentModel;
using Aibell.Application.Playback;
using Aibell.Desktop.Services;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Mpd.Client;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.Playback;

[TestFixture]
public sealed class MpdPlaybackTests
{
    private sealed class FakeTransport : IMpdTransport
    {
        private bool connected = true;
        private MpdState state = MpdState.Stopped;
        private TimeSpan elapsed;
        private int? songId;
        private int? volume = 50;

        public event PropertyChangedEventHandler? PropertyChanged;

        public List<string> Calls { get; } = [];
        public int NextId { get; set; } = 100;
        public InvalidOperationException? AddFailure { get; set; }
        public InvalidOperationException? RemoveFailure { get; set; }
        public byte[]? Cover { get; set; }

        public bool IsConnected { get => connected; set { connected = value; Changed(); } }
        public MpdState State { get => state; set { state = value; Changed(); } }
        public TimeSpan Elapsed { get => elapsed; set { elapsed = value; Changed(); } }
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public int? ServerVolume { get => volume; set { volume = value; Changed(); } }
        public int? CurrentSongId { get => songId; set { songId = value; Changed(); } }

        private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

        public Task<int> AddSongAsync(string songUri, CancellationToken cancellationToken)
        {
            Calls.Add($"add {songUri}");
            return AddFailure is { } failure ? Task.FromException<int>(failure) : Task.FromResult(NextId++);
        }

        public Task PlaySongAsync(int songId, CancellationToken cancellationToken)
        {
            Calls.Add($"play {songId}");
            CurrentSongId = songId;
            State = MpdState.Playing;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken) { Calls.Add("resume"); State = MpdState.Playing; return Task.CompletedTask; }

        public Task PauseAsync(CancellationToken cancellationToken) { Calls.Add("pause"); State = MpdState.Paused; return Task.CompletedTask; }

        public Task StopAsync(CancellationToken cancellationToken) { Calls.Add("stop"); State = MpdState.Stopped; return Task.CompletedTask; }

        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) { Calls.Add($"seek {position.TotalSeconds}"); Elapsed = position; return Task.CompletedTask; }

        public Task SetServerVolumeAsync(int volume, CancellationToken cancellationToken) { Calls.Add($"vol {volume}"); ServerVolume = volume; return Task.CompletedTask; }

        public Task RemoveSongAsync(int songId, CancellationToken cancellationToken)
        {
            Calls.Add($"remove {songId}");
            return RemoveFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }

        public Task<byte[]?> GetCoverAsync(string songUri, CancellationToken cancellationToken) => Task.FromResult(Cover);
    }

    private sealed class FakeLocal : IPlaybackEngine, IEqualizerControl, IPlaybackRateControl, IAsyncDisposable
    {
        private PlaybackSnapshot snapshot = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8);

        public event EventHandler<PlaybackSnapshot>? StateChanged;

        public List<string> Calls { get; } = [];
        public PlaybackSnapshot Snapshot => snapshot;
        public EqualizerSettings Equalizer { get; private set; } = EqualizerSettings.Flat;
        public double Rate { get; private set; } = 1;
        public bool Disposed { get; private set; }

        public void Raise(PlaybackStatus status) { snapshot = snapshot with { Status = status }; StateChanged?.Invoke(this, snapshot); }

        public Task LoadAsync(Track track, CancellationToken cancellationToken = default) { Calls.Add($"load {track.Title}"); snapshot = snapshot with { Track = track }; return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken cancellationToken = default) { Calls.Add("play"); Raise(PlaybackStatus.Playing); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { Calls.Add("pause"); Raise(PlaybackStatus.Paused); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { Calls.Add("stop"); Raise(PlaybackStatus.Stopped); return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) { Calls.Add($"seek {position.TotalSeconds}"); return Task.CompletedTask; }
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) { Calls.Add($"vol {volume}"); snapshot = snapshot with { Volume = volume }; return Task.CompletedTask; }
        public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default) { Calls.Add($"mute {isMuted}"); snapshot = snapshot with { IsMuted = isMuted }; return Task.CompletedTask; }
        public Task SetEqualizerAsync(EqualizerSettings settings, CancellationToken cancellationToken = default) { Equalizer = settings; return Task.CompletedTask; }
        public Task SetRateAsync(double rate, CancellationToken cancellationToken = default) { Rate = rate; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static Track Mpd(string path = "a/song.flac") =>
        MpdTracks.FromSong(new MpdSong(path, new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["Title"] = [Path.GetFileNameWithoutExtension(path)] }, TimeSpan.FromMinutes(3)));

    private static Track Local(string title = "Local") => new(Guid.NewGuid(), new Uri("file:///music/local.flac"), title, "A", "B", TimeSpan.FromMinutes(2));

    private static (MpdPlaybackEngine Engine, FakeTransport Transport) Engine()
    {
        MpdPlaybackEngine engine = new();
        FakeTransport transport = new();
        engine.Attach(transport);
        return (engine, transport);
    }

    [Test]
    public void Tracks_RoundTripTheirPath()
    {
        MpdSong song = new("Dir One/Sub #2/Ünï.flac", new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = ["T"],
            ["Artist"] = ["Ar"],
            ["Album"] = ["Al"],
            ["AlbumArtist"] = ["AA"],
            ["Genre"] = ["G"],
            ["Track"] = ["3/12"],
            ["Date"] = ["1999-05-01"],
            ["Disc"] = ["2"],
        }, TimeSpan.FromSeconds(61));
        Track track = MpdTracks.FromSong(song);
        Assert.That(MpdTracks.TryGetSongUri(track.Location, out string path), Is.True);
        Assert.That(path, Is.EqualTo("Dir One/Sub #2/Ünï.flac"));
        Assert.That((track.Title, track.Artist, track.Album, track.AlbumArtist, track.Genre, track.TrackNumber, track.Year, track.DiscNumber, track.Duration, track.Source),
            Is.EqualTo(("T", "Ar", "Al", "AA", "G", 3, 1999, 2, TimeSpan.FromSeconds(61), "MPD")));
        Assert.That(MpdTracks.FromSong(song).Id, Is.EqualTo(track.Id), "the same song is always the same track");
        Assert.That(MpdTracks.FromSong(song with { Uri = "other.flac" }).Id, Is.Not.EqualTo(track.Id));
        Track bare = MpdTracks.FromSong(new MpdSong("x/y.mp3", new Dictionary<string, IReadOnlyList<string>>()));
        Assert.That((bare.Title, bare.Artist, bare.TrackNumber, bare.Year), Is.EqualTo(("y.mp3", "", 0, 0)));
        Assert.That(MpdTracks.TryGetSongUri(new Uri("file:///music/a.flac"), out _), Is.False);
        Assert.That(MpdTracks.TryGetSongUri(null, out _), Is.False);
        Assert.That(MpdTracks.TryGetSongUri(new Uri("mpd:///"), out _), Is.False);
        Assert.Throws<ArgumentNullException>(() => MpdTracks.FromSong(null!));
        Assert.Throws<ArgumentNullException>(() => MpdTracks.Location(null!));
    }

    [Test]
    public async Task Engine_RefusesWhatItCannotPlay()
    {
        MpdPlaybackEngine detached = new();
        await Assert.ThatAsync(() => detached.LoadAsync(Mpd()), Throws.InvalidOperationException.With.Message.Contains("Connect to an MPD server first"));
        (MpdPlaybackEngine engine, FakeTransport transport) = Engine();
        await Assert.ThatAsync(() => engine.LoadAsync(Local()), Throws.ArgumentException);
        await Assert.ThatAsync(() => engine.LoadAsync(null!), Throws.ArgumentNullException);
        transport.IsConnected = false;
        await Assert.ThatAsync(() => engine.LoadAsync(Mpd()), Throws.InvalidOperationException);
        Assert.That(engine.IsActive, Is.False);
        Assert.Throws<ArgumentNullException>(() => engine.Attach(null!));
        await engine.PlayAsync();
        await engine.PauseAsync();
        await engine.StopAsync();
        await engine.SeekAsync(TimeSpan.FromSeconds(1));
        await engine.SetVolumeAsync(0.5);
        await engine.SetMutedAsync(true);
        Assert.That(transport.Calls, Is.Empty, "with nothing loaded the engine sends nothing");
    }

    [Test]
    public async Task Engine_LoadsThenPlaysPausesAndResumes()
    {
        (MpdPlaybackEngine engine, FakeTransport transport) = Engine();
        List<PlaybackSnapshot> seen = [];
        engine.StateChanged += (_, snapshot) => seen.Add(snapshot);
        Track track = Mpd();
        await engine.LoadAsync(track);
        Assert.That(transport.Calls, Is.EqualTo(new[] { "add a/song.flac" }));
        Assert.That(engine.IsActive, Is.True);
        Assert.That((engine.Snapshot.Status, engine.Snapshot.Track), Is.EqualTo((PlaybackStatus.Stopped, track)));
        await engine.PlayAsync();
        Assert.That(transport.Calls[^1], Is.EqualTo("play 100"));
        transport.Elapsed = TimeSpan.FromSeconds(12);
        Assert.That((engine.Snapshot.Status, engine.Snapshot.Position, engine.Snapshot.Volume), Is.EqualTo((PlaybackStatus.Playing, TimeSpan.FromSeconds(12), 0.5)));
        await engine.PlayAsync();
        Assert.That(transport.Calls.Count(call => call.StartsWith("play", StringComparison.Ordinal)), Is.EqualTo(1), "already playing: nothing to send");
        await engine.PauseAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Paused));
        await engine.PlayAsync();
        Assert.That(transport.Calls[^1], Is.EqualTo("resume"));
        await engine.PauseAsync();
        await engine.PauseAsync();
        Assert.That(seen.Select(item => item.Status), Does.Contain(PlaybackStatus.Playing).And.Contain(PlaybackStatus.Paused));
    }

    [Test]
    public async Task Engine_ReportsEndedWhenTheSongFinishesAndStoppedWhenTheUserStops()
    {
        (MpdPlaybackEngine engine, FakeTransport transport) = Engine();
        Track track = Mpd();
        await engine.LoadAsync(track);
        await engine.PlayAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        transport.State = MpdState.Stopped;
        Assert.That((engine.Snapshot.Status, engine.Snapshot.Position), Is.EqualTo((PlaybackStatus.Ended, track.Duration)), "nobody asked it to stop, so the song finished");
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Ended), "and it stays ended");

        await engine.PlayAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        await engine.StopAsync();
        Assert.That(transport.Calls[^1], Is.EqualTo("stop"));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        await engine.PlayAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing), "play after stop starts the same song again");
        await engine.StopAsync();
        await engine.StopAsync();
    }

    [Test]
    public async Task Engine_StepsAsideWhenAnotherClientTakesOver()
    {
        (MpdPlaybackEngine engine, FakeTransport transport) = Engine();
        await engine.LoadAsync(Mpd());
        await engine.PlayAsync();
        transport.CurrentSongId = 999;
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
        await engine.PauseAsync();
        Assert.That(transport.Calls, Has.None.EqualTo("pause"), "it does not pause someone else's song");
        await engine.StopAsync();
        Assert.That(transport.Calls, Has.None.EqualTo("stop"));
    }

    [Test]
    public async Task Engine_SeeksNowOrAfterStarting()
    {
        (MpdPlaybackEngine engine, FakeTransport transport) = Engine();
        await engine.LoadAsync(Mpd());
        await engine.SeekAsync(TimeSpan.FromSeconds(30));
        Assert.That(transport.Calls, Has.None.StartsWith("seek"), "not playing yet: the position is kept for later");
        await engine.PlayAsync();
        Assert.That(transport.Calls[^2..], Is.EqualTo(new[] { "play 100", "seek 30" }));
        await engine.SeekAsync(TimeSpan.FromSeconds(90));
        Assert.That(transport.Calls[^1], Is.EqualTo("seek 90"));
    }

    [Test]
    public async Task Engine_VolumeAndMute()
    {
        (MpdPlaybackEngine engine, FakeTransport transport) = Engine();
        await engine.LoadAsync(Mpd());
        await engine.PlayAsync();
        await engine.SetVolumeAsync(0.666);
        Assert.That(transport.ServerVolume, Is.EqualTo(67));
        await engine.SetVolumeAsync(5);
        Assert.That(transport.ServerVolume, Is.EqualTo(100));
        await engine.SetMutedAsync(true);
        Assert.That((transport.ServerVolume, engine.Snapshot.IsMuted), Is.EqualTo(((int?)0, true)));
        await engine.SetMutedAsync(true);
        await engine.SetVolumeAsync(0.3);
        Assert.That(transport.ServerVolume, Is.EqualTo(0), "muted: the new volume waits for unmute");
        await engine.SetMutedAsync(false);
        Assert.That((transport.ServerVolume, engine.Snapshot.IsMuted), Is.EqualTo(((int?)30, false)));
        await engine.SetMutedAsync(false);
        transport.ServerVolume = null;
        Assert.That(engine.Snapshot.Volume, Is.Zero);
    }

    [Test]
    public async Task Engine_LeavesTheServersVolumeAloneWhenThePlayerReappliesItsOwnAtEveryTrack()
    {
        (MpdPlaybackEngine engine, FakeTransport transport) = Engine();
        transport.ServerVolume = 100;
        await engine.LoadAsync(Mpd());
        // The player's sequence for every track: load, apply its volume and mute state, then play.
        await engine.SetVolumeAsync(0.71);
        await engine.SetMutedAsync(true);
        await engine.PlayAsync();
        Assert.That(transport.Calls, Has.None.StartsWith("vol"), "the server's volume was not touched by the track change");
        Assert.That(transport.ServerVolume, Is.EqualTo(100));
        Assert.That(engine.Snapshot.IsMuted, Is.False);
        await engine.SetVolumeAsync(0.5);
        Assert.That(transport.ServerVolume, Is.EqualTo(50), "but when the user moves the volume bar it is applied");

        await engine.LoadAsync(Mpd("next.flac"));
        await engine.SetVolumeAsync(0.9);
        Assert.That(transport.ServerVolume, Is.EqualTo(50), "the next track's load sequence leaves it alone again");
        await engine.StopAsync();
        await engine.SetVolumeAsync(0.9);
        Assert.That(transport.ServerVolume, Is.EqualTo(90));
    }

    [Test]
    public async Task Engine_ReportsALostConnection()
    {
        (MpdPlaybackEngine engine, FakeTransport transport) = Engine();
        List<PlaybackSnapshot> seen = [];
        engine.StateChanged += (_, snapshot) => seen.Add(snapshot);
        await engine.LoadAsync(Mpd());
        await engine.PlayAsync();
        transport.IsConnected = false;
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Faulted));
        Assert.That(engine.Snapshot.Error, Is.EqualTo("The connection to MPD was lost."));
        Assert.That(seen[^1].Status, Is.EqualTo(PlaybackStatus.Faulted));
        await engine.StopAsync();
        await engine.PauseAsync();
    }

    [Test]
    public async Task Engine_LoadingAnotherSongStopsAndRemovesTheFirst()
    {
        (MpdPlaybackEngine engine, FakeTransport transport) = Engine();
        await engine.LoadAsync(Mpd("one.flac"));
        await engine.PlayAsync();
        transport.Calls.Clear();
        await engine.LoadAsync(Mpd("two.flac"));
        Assert.That(transport.Calls, Is.EqualTo(new[] { "stop", "remove 100", "add two.flac" }));
        await engine.ReleaseAsync();
        Assert.That(transport.Calls[^1], Is.EqualTo("remove 101"));
        Assert.That(engine.IsActive, Is.False);
        Assert.That(engine.Snapshot.Track, Is.Null);
        await engine.ReleaseAsync();

        FakeTransport failing = new() { AddFailure = new InvalidOperationException("No such song") };
        MpdPlaybackEngine other = new();
        other.Attach(failing);
        await Assert.ThatAsync(() => other.LoadAsync(Mpd()), Throws.InvalidOperationException);
        Assert.That(other.IsActive, Is.False);
    }

    [Test]
    public async Task Engine_ReleaseToleratesAServerThatWentAway()
    {
        (MpdPlaybackEngine engine, FakeTransport transport) = Engine();
        await engine.LoadAsync(Mpd());
        transport.RemoveFailure = new InvalidOperationException("gone");
        await engine.ReleaseAsync();
        Assert.That(engine.IsActive, Is.False);
        engine.Dispose();
        engine.Dispose();
    }

    [Test]
    public async Task Router_SendsTracksToTheRightEngineAndNeverPlaysBoth()
    {
        FakeLocal local = new();
        (MpdPlaybackEngine mpd, FakeTransport transport) = Engine();
        MpdPlaybackRouter router = new(local, mpd);
        List<PlaybackStatus> seen = [];
        router.StateChanged += (_, snapshot) => seen.Add(snapshot.Status);

        await router.LoadAsync(Local("one"));
        await router.PlayAsync();
        Assert.That(local.Calls, Is.EqualTo(new[] { "load one", "play" }));
        Assert.That(router.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));

        await router.LoadAsync(Mpd());
        Assert.That(local.Calls[^1], Is.EqualTo("stop"), "the local player stops before the server is used");
        Assert.That(transport.Calls, Is.EqualTo(new[] { "add a/song.flac" }));
        await router.PlayAsync();
        Assert.That(router.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        Assert.That(router.Snapshot.Volume, Is.EqualTo(0.5), "with a server song loaded the volume bar shows the server's volume");
        local.Raise(PlaybackStatus.Paused);
        Assert.That(seen[^1], Is.Not.EqualTo(PlaybackStatus.Paused), "events of the idle engine are not forwarded");
        await router.PauseAsync();
        Assert.That(transport.Calls[^1], Is.EqualTo("pause"));
        await router.SeekAsync(TimeSpan.FromSeconds(5));
        await router.StopAsync();
        Assert.That(transport.Calls[^1], Is.EqualTo("stop"));

        transport.Calls.Clear();
        local.Calls.Clear();
        await router.LoadAsync(Local("two"));
        Assert.That(transport.Calls, Does.Contain("remove 100"), "going back to a file gives the server back");
        Assert.That(local.Calls, Is.EqualTo(new[] { "load two" }));
        await router.PlayAsync();
        Assert.That(local.Calls[^1], Is.EqualTo("play"));
        Assert.That(router.Snapshot.Track?.Title, Is.EqualTo("two"));
        Assert.That(seen, Does.Contain(PlaybackStatus.Playing));
        Assert.That(router.Mpd, Is.SameAs(mpd));
    }

    [Test]
    public async Task Router_TheVolumeBarControlsTheLocalPlayerUntilAServerSongIsLoadedOrMpdIsSelected()
    {
        FakeLocal local = new();
        (MpdPlaybackEngine mpd, FakeTransport transport) = Engine();
        MpdPlaybackRouter router = new(local, mpd);
        await router.SetVolumeAsync(0.4);
        await router.SetMutedAsync(true);
        Assert.That(local.Calls, Is.EqualTo(new[] { "vol 0.4", "mute True" }));
        Assert.That(transport.Calls, Is.Empty);
        Assert.That(router.ControlsServerVolume, Is.False);
        Assert.That(router.Snapshot.Volume, Is.EqualTo(0.4));
        await router.SetMutedAsync(false);

        // Playing a server song: the bar now shows and sets the server's volume, and the local player is left alone.
        local.Calls.Clear();
        await router.LoadAsync(Mpd());
        await router.SetVolumeAsync(0.9);
        await router.SetMutedAsync(false);
        await router.PlayAsync();
        Assert.That(transport.ServerVolume, Is.EqualTo(50), "the player's own load-time volume never reaches the server");
        Assert.That(router.ControlsServerVolume, Is.True);
        Assert.That(router.Snapshot.Volume, Is.EqualTo(0.5), "the bar shows the server's volume");
        await router.SetVolumeAsync(0.25);
        Assert.That(transport.ServerVolume, Is.EqualTo(25));
        await router.SetMutedAsync(true);
        Assert.That((transport.ServerVolume, router.Snapshot.IsMuted), Is.EqualTo(((int?)0, true)));
        await router.SetMutedAsync(false);
        Assert.That(transport.ServerVolume, Is.EqualTo(25));
        Assert.That(local.Calls, Has.None.StartsWith("vol").And.None.StartsWith("mute"), "the local player's volume was not touched");

        // Back to a file: the local player keeps the level it had; the player's load-time volume call (the server's number) is dropped.
        await router.LoadAsync(Local("two"));
        await router.SetVolumeAsync(0.25);
        await router.SetMutedAsync(false);
        await router.PlayAsync();
        Assert.That(local.Calls, Has.None.StartsWith("vol"), "the server's level is not handed to the local player");
        Assert.That(router.ControlsServerVolume, Is.False);
        Assert.That(router.Snapshot.Volume, Is.EqualTo(0.4), "the bar is the local player's again");
        await router.SetVolumeAsync(0.6);
        Assert.That(local.Calls, Does.Contain("vol 0.6"));
        Assert.That(transport.ServerVolume, Is.EqualTo(25));
    }

    [Test]
    public async Task Router_WhileMpdIsSelectedTheVolumeBarIsTheServersEvenWithNothingPlaying()
    {
        FakeLocal local = new();
        (MpdPlaybackEngine mpd, FakeTransport transport) = Engine();
        MpdPlaybackRouter router = new(local, mpd);
        List<PlaybackSnapshot> seen = [];
        router.StateChanged += (_, snapshot) => seen.Add(snapshot);
        transport.ServerVolume = 80;
        seen.Clear();

        router.ServerSelected = true;
        router.ServerSelected = true;
        Assert.That(router.ControlsServerVolume, Is.True);
        Assert.That(seen.Single().Volume, Is.EqualTo(0.8), "selecting MPD makes the bar show the server's volume");
        await router.SetVolumeAsync(0.3);
        await router.SetMutedAsync(true);
        Assert.That((transport.ServerVolume, router.Snapshot.IsMuted), Is.EqualTo(((int?)0, true)));
        await router.SetMutedAsync(false);
        Assert.That(transport.ServerVolume, Is.EqualTo(30));
        Assert.That(local.Calls, Is.Empty);

        transport.ServerVolume = 55;
        Assert.That(seen[^1].Volume, Is.EqualTo(0.55), "a change made on the server moves the bar");

        transport.IsConnected = false;
        Assert.That(router.ControlsServerVolume, Is.False, "without a connection the bar is the local player's");
        Assert.That(seen[^1].Volume, Is.EqualTo(0.8), "and shows its level again");
        await router.SetVolumeAsync(0.2);
        Assert.That(local.Calls, Is.EqualTo(new[] { "vol 0.2" }));

        transport.IsConnected = true;
        router.ServerSelected = false;
        Assert.That(router.ControlsServerVolume, Is.False);
        Assert.That(seen[^1].Volume, Is.EqualTo(0.2), "deselecting MPD gives the bar back to the local player, at the level it has now");
    }

    [Test]
    public async Task Router_ForwardsEqualizerAndRateToTheLocalPlayer()
    {
        FakeLocal local = new();
        (MpdPlaybackEngine mpd, _) = Engine();
        MpdPlaybackRouter router = new(local, mpd);
        EqualizerSettings settings = EqualizerSettings.Flat with { IsEnabled = true };
        await router.SetEqualizerAsync(settings);
        await router.SetRateAsync(1.5);
        Assert.That((router.Equalizer, router.Rate), Is.EqualTo((settings, 1.5)));
    }

    [Test]
    public async Task Router_ALoadThatFailsLeavesTheLocalPlayerInCharge()
    {
        FakeLocal local = new();
        MpdPlaybackEngine mpd = new();
        MpdPlaybackRouter router = new(local, mpd);
        await Assert.ThatAsync(() => router.LoadAsync(Mpd()), Throws.InvalidOperationException);
        await router.LoadAsync(Local());
        await router.PlayAsync();
        Assert.That(router.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        Assert.Throws<ArgumentNullException>(() => _ = new MpdPlaybackRouter(null!, mpd));
        Assert.Throws<ArgumentNullException>(() => _ = new MpdPlaybackRouter(local, null!));
        await Assert.ThatAsync(() => router.LoadAsync(null!), Throws.ArgumentNullException);
    }

    [Test]
    public async Task Router_WorksWithAnEngineThatHasNoEqualizerOrRate()
    {
        PlainEngine plain = new();
        MpdPlaybackRouter router = new(plain, new MpdPlaybackEngine());
        await router.SetEqualizerAsync(EqualizerSettings.Flat);
        await router.SetRateAsync(2);
        Assert.That((router.Equalizer, router.Rate), Is.EqualTo((EqualizerSettings.Flat, 1.0)));
        await router.DisposeAsync();
        Assert.That(plain.Disposed, Is.True);
    }

    [Test]
    public async Task Router_DisposeReleasesTheServerAndTheLocalEngine()
    {
        FakeLocal local = new();
        (MpdPlaybackEngine mpd, FakeTransport transport) = Engine();
        MpdPlaybackRouter router = new(local, mpd);
        await router.LoadAsync(Mpd());
        await router.DisposeAsync();
        Assert.That(transport.Calls, Does.Contain("remove 100"));
        Assert.That(local.Disposed, Is.True);
        MpdPlaybackRouter disposable = new(new DisposableEngine(), new MpdPlaybackEngine());
        await disposable.DisposeAsync();
    }

    private sealed class PlainEngine : IPlaybackEngine, IDisposable
    {
        public bool Disposed { get; private set; }
        public PlaybackSnapshot Snapshot => new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 1);
        public event EventHandler<PlaybackSnapshot>? StateChanged { add { } remove { } }
        public Task LoadAsync(Track track, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }

    private sealed class DisposableEngine : IPlaybackEngine
    {
        public PlaybackSnapshot Snapshot => new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 1);
        public event EventHandler<PlaybackSnapshot>? StateChanged { add { } remove { } }
        public Task LoadAsync(Track track, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Test]
    public async Task ArtworkProvider_AsksTheServerForMpdTracksAndCachesTheResult()
    {
        string cache = Directory.CreateTempSubdirectory("aibell-mpd-art-").FullName;
        try
        {
            FakeTransport transport = new() { Cover = [1, 2, 3] };
            Uri inner = new("file:///inner.jpg");
            List<Uri> asked = [];
            MpdArtworkProvider provider = new(new LambdaArtwork(location => { asked.Add(location); return inner; }), () => transport, cache);
            Uri? art = await provider.GetAsync(Mpd().Location);
            Assert.That(art, Is.Not.Null);
            Assert.That(File.ReadAllBytes(art!.LocalPath), Is.EqualTo(new byte[] { 1, 2, 3 }));
            transport.Cover = null;
            Assert.That(await provider.GetAsync(Mpd().Location), Is.EqualTo(art), "the second time comes from the cache");
            Assert.That(await provider.GetAsync(Mpd("other.flac").Location), Is.Null, "no cover");
            Assert.That(await provider.GetAsync(new Uri("file:///music/a.flac")), Is.EqualTo(inner));
            Assert.That(asked, Has.Count.EqualTo(1));
            transport.IsConnected = false;
            Assert.That(await provider.GetAsync(Mpd("third.flac").Location), Is.Null);
            MpdArtworkProvider detached = new(new LambdaArtwork(_ => null), () => null, cache);
            Assert.That(await detached.GetAsync(Mpd("third.flac").Location), Is.Null);
            await Assert.ThatAsync(() => provider.GetAsync(null!), Throws.ArgumentNullException);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    private sealed class LambdaArtwork(Func<Uri, Uri?> find) : Aibell.Application.Library.IArtworkProvider
    {
        public Task<Uri?> GetAsync(Uri mediaLocation, CancellationToken cancellationToken = default) => Task.FromResult(find(mediaLocation));
    }
}
