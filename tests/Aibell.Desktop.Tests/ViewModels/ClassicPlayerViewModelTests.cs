using Aibell.Application.Library;
using Aibell.Application.Playback;
using Aibell.Application.Settings;
using Aibell.Desktop.Classic;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Settings;
using Aibell.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.ViewModels;

[TestFixture]
public sealed class ClassicPlayerViewModelTests
{
    private string directory = null!;
    private SqliteTrackRepository repository = null!;
    private JsonApplicationSettingsStore settings = null!;
    private LibraryMonitoringService monitoring = null!;
    private FakeEngine engine = null!;
    private MainWindowViewModel player = null!;
    private ClassicPlayerViewModel classic = null!;
    private Track first = null!;
    private Track second = null!;
    private Track third = null!;

    [SetUp]
    public async Task SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-classic-{Guid.NewGuid():N}");
        repository = new SqliteTrackRepository(Path.Combine(directory, "library.db"));
        settings = new JsonApplicationSettingsStore(Path.Combine(directory, "settings.json"));
        first = Make("First", "Alpha", "b.flac") with { BitRate = 1411, SampleRate = 44100, Channels = 2, Genre = "Rock" };
        second = Make("Second", "Beta", "a.flac") with { Genre = "Folk" };
        third = Make("Third", "", "c.flac");
        foreach (Track track in new[] { first, second, third }) await repository.UpsertAsync(track, directory);
        LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), repository);
        monitoring = new LibraryMonitoringService(repository, importer, NullLogger<LibraryMonitoringService>.Instance);
        engine = new FakeEngine();
        player = new MainWindowViewModel(importer, repository, repository, repository, repository, settings, monitoring, engine);
        classic = new ClassicPlayerViewModel(player, () => new Random(3));
        await player.LoadAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await player.FlushPreferencesAsync();
        player.Dispose();
        monitoring.Dispose();
        settings.Dispose();
        await repository.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    private Track Make(string title, string artist, string file) =>
        new(Guid.NewGuid(), new Uri(Path.Combine(directory, file)), title, artist, "Album", TimeSpan.FromMinutes(3));

    [Test]
    public async Task ThePlaylistIsSavedAndComesBackWithTheCurrentEntry()
    {
        int changes = 0;
        classic.PlaylistChanged += (_, _) => changes++;
        Assert.That(classic.Add([first, second, third]), Is.Zero);
        Assert.That(classic.Add([]), Is.EqualTo(-1));
        await classic.PlayEntryAsync(1);
        await player.FlushPreferencesAsync();
        Assert.That(changes, Is.GreaterThan(0));

        using JsonApplicationSettingsStore reread = new(Path.Combine(directory, "settings.json"));
        ClassicPlayerPreferences saved = (await reread.LoadAsync()).ClassicPlayer!;
        Assert.Multiple(() =>
        {
            Assert.That(saved.Playlist!.Select(entry => entry.Track.Title), Is.EqualTo(new[] { "First", "Second", "Third" }));
            Assert.That(saved.PlaylistCurrent, Is.EqualTo(1));
        });

        LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), repository);
        using MainWindowViewModel again = new(importer, repository, repository, repository, repository, reread, monitoring, new FakeEngine());
        await again.LoadAsync();
        ClassicPlayerViewModel restored = new(again);
        Assert.Multiple(() =>
        {
            Assert.That(restored.Entries, Has.Count.EqualTo(3));
            Assert.That(restored.CurrentIndex, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task PlayingFollowsThePlaylistEvenWithTheSameTrackTwice()
    {
        classic.Add([first, second, first]);
        await classic.PlayEntryAsync(2);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Track, Is.EqualTo(first));
            Assert.That(classic.OwnsPlayback, Is.True);
            Assert.That(classic.CurrentIndex, Is.EqualTo(2));
            Assert.That(classic.MarqueeText, Is.EqualTo("3. Alpha - First (3:00)"));
        });

        await classic.PreviousAsync();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Track, Is.EqualTo(second));
            Assert.That(classic.CurrentIndex, Is.EqualTo(1));
        });

        // Removing an entry above the playing one keeps it current, and Next still walks the edited list.
        classic.Select(0);
        classic.RemoveSelected();
        Assert.That(classic.CurrentIndex, Is.Zero);
        Assert.That(classic.OwnsPlayback, Is.True);
        await classic.NextAsync();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Track, Is.EqualTo(first));
            Assert.That(classic.CurrentIndex, Is.EqualTo(1));
        });

        // At the end without repeat nothing happens; with repeat the list starts over.
        await classic.NextAsync();
        Assert.That(classic.CurrentIndex, Is.EqualTo(1));
        await classic.ToggleRepeatAsync();
        Assert.That(player.CurrentRepeatMode, Is.EqualTo(RepeatMode.All));
        await classic.NextAsync();
        Assert.That(classic.CurrentIndex, Is.Zero);
        await classic.ToggleRepeatAsync();
        Assert.That(player.CurrentRepeatMode, Is.EqualTo(RepeatMode.Off));
    }

    [Test]
    public async Task PlayResumesAPauseAndOtherwiseRestartsTheCurrentEntry()
    {
        await classic.PlayAsync();
        Assert.That(engine.Snapshot.Track, Is.Null, "an empty playlist plays nothing");

        classic.Add([first, second]);
        await classic.PlayAsync();
        Assert.That(engine.Snapshot is { Track: not null, Status: PlaybackStatus.Playing }, Is.True);
        Assert.That(engine.Snapshot.Track, Is.EqualTo(first));

        await classic.PauseAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Paused));
        await classic.PlayAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));

        await classic.StopAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        Assert.That(classic.DisplayTime, Is.Null);
        await classic.NextAsync();
        Assert.That(classic.CurrentIndex, Is.EqualTo(1), "while stopped, Next only moves the current entry");
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [Test]
    public async Task PlayingFromElsewhereLeavesThePlaylistAlone()
    {
        classic.Add([first, second]);
        await classic.PlayEntryAsync(0);
        player.SelectedTrack = player.Tracks.Single(row => row.Source.Id == third.Id);
        await player.PlaySelectedTrackAsync();
        classic.Synchronize();
        Assert.Multiple(() =>
        {
            Assert.That(classic.OwnsPlayback, Is.False);
            Assert.That(classic.CurrentIndex, Is.Zero);
            Assert.That(classic.MarqueeText, Is.EqualTo("Third (3:00)"), "a track from elsewhere has no playlist number");
        });
    }

    [Test]
    public async Task BalanceReachesTheEngineSnapsToTheCentreAndIsSaved()
    {
        Assert.That(player.CanUseBalance, Is.True);
        await player.SetBalanceAsync(0.03, persist: false);
        Assert.That(player.Balance, Is.Zero, "close to the middle snaps to it");
        await player.SetBalanceAsync(-0.456, persist: true);
        await player.SetBalanceAsync(double.NaN, persist: false);
        Assert.That(engine.Balance, Is.Zero);
        await player.SetBalanceAsync(-0.456, persist: true);
        await player.FlushPreferencesAsync();
        Assert.Multiple(() =>
        {
            Assert.That(player.Balance, Is.EqualTo(-0.46));
            Assert.That(engine.Balance, Is.EqualTo(-0.46));
        });
        using JsonApplicationSettingsStore reread = new(Path.Combine(directory, "settings.json"));
        Assert.That((await reread.LoadAsync()).Balance, Is.EqualTo(-0.46));

        LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), repository);
        FakeEngine restartedEngine = new();
        using MainWindowViewModel restarted = new(importer, repository, repository, repository, repository, reread, monitoring, restartedEngine);
        await restarted.LoadAsync();
        Assert.That(restartedEngine.Balance, Is.EqualTo(-0.46), "the saved balance reaches the engine at start");
    }

    [Test]
    public async Task AutoGivesEachTrackItsOwnCurveOrItsGenresPreset()
    {
        IEqualizerHost equalizer = player;
        EqualizerSettings custom = EqualizerSettings.Flat with { IsEnabled = true, Bands = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] };
        await equalizer.ApplyAsync(EqualizerSettings.Flat with { IsEnabled = true }, persist: true);
        int changes = 0;
        equalizer.EqualizerChanged += (_, _) => changes++;
        classic.Add([first, second]);

        await classic.PlayEntryAsync(1);
        Assert.That(equalizer.Current.Bands, Is.All.Zero, "AUTO is off: nothing changes");
        await player.SetEqualizerAutoAsync(true);
        Assert.That(player.IsEqualizerAuto, Is.True);

        await classic.PlayEntryAsync(0);
        double[] rock = EqualizerPresets.All.Single(preset => preset.Id == "Rock").Bands;
        Assert.That(equalizer.Current.Bands, Is.EqualTo(rock), "a rock track without its own curve gets the rock preset");
        Assert.That(changes, Is.GreaterThanOrEqualTo(2), "open equalizer views hear about it");

        await equalizer.ApplyAsync(custom, persist: true);
        await classic.PlayEntryAsync(1);
        Assert.That(equalizer.Current.Bands, Is.EqualTo(custom.Bands), "a folk track has no preset, so the curve stays");
        await equalizer.ApplyAsync(EqualizerSettings.Flat with { IsEnabled = false }, persist: true);
        await classic.PlayEntryAsync(0);
        Assert.Multiple(() =>
        {
            Assert.That(equalizer.Current.Bands, Is.EqualTo(custom.Bands), "the rock track comes back with the curve set while it played");
            Assert.That(equalizer.Current.IsEnabled, Is.False, "the on/off switch is the user's");
        });

        await player.FlushPreferencesAsync();
        using JsonApplicationSettingsStore reread = new(Path.Combine(directory, "settings.json"));
        ApplicationPreferences saved = await reread.LoadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(saved.EqualizerAuto, Is.True);
            Assert.That(saved.EqualizerTrackCurves![first.Location.AbsoluteUri].Bands, Is.EqualTo(custom.Bands));
            Assert.That(saved.EqualizerTrackCurves[second.Location.AbsoluteUri].IsEnabled, Is.False);
        });
        Assert.That(player.AutoCurve(null), Is.Null);
        Assert.That(player.AutoCurve(first with { Location = new Uri("https://radio.example/stream") }), Is.Null, "streams get nothing");
    }

    [Test]
    public void SelectionAndEditing()
    {
        classic.Add([first, second, third, first]);
        classic.Select(1);
        classic.Select(3, toggle: true);
        Assert.That(classic.SelectedTracks(), Is.EqualTo(new[] { second, first }));
        classic.Select(0, range: true);
        Assert.That(classic.SelectedCount, Is.EqualTo(4), "Shift extends from the last click");
        classic.SelectNone();
        classic.Select(1);
        classic.Select(2, range: true);
        classic.InvertSelection();
        Assert.That(classic.SelectedTracks(), Is.EqualTo(new[] { first, first }));
        classic.SelectAll();
        Assert.That(classic.SelectedCount, Is.EqualTo(4));
        classic.Select(1, toggle: true);
        Assert.That(classic.SelectedCount, Is.EqualTo(3));

        classic.RemoveDuplicates();
        Assert.That(classic.Tracks(), Is.EqualTo(new[] { first, second, third }));
        classic.Select(1);
        classic.MoveSelected(5);
        Assert.That(classic.Tracks(), Is.EqualTo(new[] { first, third, second }), "a move stops at the end");
        classic.MoveSelected(-1);
        Assert.That(classic.Tracks(), Is.EqualTo(new[] { first, second, third }));
        classic.Crop();
        Assert.Multiple(() =>
        {
            Assert.That(classic.Tracks(), Is.EqualTo(new[] { second }));
            Assert.That(classic.CurrentIndex, Is.Zero);
        });
        classic.RemoveAll();
        Assert.That(classic.CurrentIndex, Is.EqualTo(-1));
        classic.MoveSelected(1);
        classic.Select(5);
        Assert.That(classic.IsSelected(-1), Is.False);
    }

    [Test]
    public void SortingKeepsTheCurrentEntry()
    {
        classic.Add([first, second, third]);
        classic.Select(2);
        classic.Sort(ClassicPlaylistSort.Title);
        Assert.That(classic.Tracks(), Is.EqualTo(new[] { first, second, third }));
        classic.Sort(ClassicPlaylistSort.FileName);
        Assert.Multiple(() =>
        {
            Assert.That(classic.Tracks(), Is.EqualTo(new[] { second, first, third }));
            Assert.That(classic.CurrentIndex, Is.EqualTo(1), "the first entry was current and still is");
        });
        classic.Sort(ClassicPlaylistSort.Path);
        classic.Sort(ClassicPlaylistSort.Reverse);
        Assert.That(classic.Tracks(), Is.EqualTo(new[] { third, first, second }));
        classic.Sort(ClassicPlaylistSort.Randomize);
        Assert.That(classic.Tracks(), Is.EquivalentTo(new[] { first, second, third }));
        classic.Replace([third]);
        Assert.That(classic.Tracks(), Is.EqualTo(new[] { third }));
        classic.Add([first], insertAt: 0);
        Assert.That(classic.CurrentIndex, Is.EqualTo(1), "an insert above the current entry shifts it");
    }

    [Test]
    public async Task DisplayTextFollowsTheTrackAndTheTimeSetting()
    {
        Assert.That(classic.MarqueeText, Is.EqualTo("Aibell"));
        classic.Add([first, second]);
        Assert.Multiple(() =>
        {
            Assert.That(classic.BitRateText, Is.EqualTo("999"));
            Assert.That(classic.SampleRateText, Is.EqualTo("44"));
            Assert.That(classic.ChannelIndicator, Is.Zero, "nothing playing");
            Assert.That(classic.RunningTimeText, Is.EqualTo("0:00/6:00"));
        });
        await classic.PlayEntryAsync(0);
        engine.Snapshot = engine.Snapshot with { Position = TimeSpan.FromSeconds(70) };
        Assert.That(classic.DisplayTime, Is.EqualTo(TimeSpan.FromSeconds(70)));
        Assert.That(classic.ChannelIndicator, Is.EqualTo(2));
        await classic.ToggleRemainingTimeAsync();
        Assert.That(classic.DisplayTime, Is.EqualTo(TimeSpan.FromSeconds(-110)));
        classic.SelectAll();
        Assert.That(classic.RunningTimeText, Is.EqualTo("6:00/6:00"));
        Assert.That(ClassicPlayerViewModel.FormatDuration(TimeSpan.FromMinutes(75)), Is.EqualTo("1:15:00"));
        Assert.That(ClassicPlayerViewModel.FormatDuration(TimeSpan.FromSeconds(-3)), Is.EqualTo("0:00"));
        Assert.That(ClassicPlayerViewModel.DescribeTrack(third with { Title = "" }), Is.EqualTo("c"));
    }

    [Test]
    public async Task ShuffleGoesThroughTheSharedPlayer()
    {
        await classic.ToggleShuffleAsync();
        Assert.That(player.IsShuffleEnabled, Is.True);
        classic.Add([first, second, third]);
        await classic.PlayEntryAsync(1);
        Assert.That(classic.CurrentIndex, Is.EqualTo(1), "shuffle starts from the chosen entry");
        await classic.NextAsync();
        Assert.That(classic.CurrentIndex, Is.Not.EqualTo(1));
    }

    [Test]
    public void LibrarySearchMatchesEveryWord()
    {
        Track[] tracks = [first, second, third];
        Assert.That(LibraryPickerWindow.Matches(tracks, "alpha FIRST"), Is.EqualTo(new[] { first }));
        Assert.That(LibraryPickerWindow.Matches(tracks, "a.flac"), Is.EqualTo(new[] { second }));
        Assert.That(LibraryPickerWindow.Matches(tracks, "  "), Has.Exactly(3).Items);
        Assert.That(LibraryPickerWindow.Matches(tracks, "nothing"), Is.Empty);
    }

    [Test]
    public void PreferencesAreBroughtBackIntoRange()
    {
        PlayQueueEntry entry = new(Guid.NewGuid(), first);
        ClassicPlayerPreferences normalized = new ClassicPlayerPreferences(SkinId: "../x.wsz", PlaylistExtraRows: 99, PlaylistCurrent: 5, PlaylistExtraColumns: -3,
            Playlist: [entry, entry, new PlayQueueEntry(Guid.Empty, first)]).Normalized();
        Assert.Multiple(() =>
        {
            Assert.That(normalized.SkinId, Is.Null);
            Assert.That(normalized.PlaylistExtraRows, Is.EqualTo(ClassicPlayerPreferences.MaximumExtraRows));
            Assert.That(normalized.PlaylistExtraColumns, Is.Zero);
            Assert.That(new ClassicPlayerPreferences(PlaylistExtraColumns: 999).Normalized().PlaylistExtraColumns, Is.EqualTo(ClassicPlayerPreferences.MaximumExtraColumns));
            Assert.That(normalized.PlaylistCurrent, Is.EqualTo(-1));
            Assert.That(normalized.Playlist, Is.EqualTo(new[] { entry }));
            Assert.That(new ClassicPlayerPreferences(SkinId: "ok.wsz").Normalized().SkinId, Is.EqualTo("ok.wsz"));
        });
    }

    [Test]
    public void TheSpectrumRunsWhileAnyVisualizerWantsIt()
    {
        CountingSpectrum inner = new();
        SharedSpectrumSource shared = new(inner);
        shared.SetSpectrumEnabled(true);
        shared.SetSpectrumEnabled(true);
        shared.SetSpectrumEnabled(false);
        Assert.That(inner.Enabled, Is.True);
        shared.SetSpectrumEnabled(false);
        shared.SetSpectrumEnabled(false);
        Assert.Multiple(() =>
        {
            Assert.That(inner.Enabled, Is.False);
            Assert.That(inner.Changes, Is.EqualTo(2));
            Assert.That(shared.IsSpectrumAvailable, Is.True);
            Assert.That(shared.TryReadSpectrum(new float[4]), Is.True);
            Assert.That(shared.Inner, Is.SameAs(inner));
        });
    }

    private sealed class CountingSpectrum : ISpectrumSource
    {
        public bool Enabled { get; private set; }

        public int Changes { get; private set; }

        public bool IsSpectrumAvailable => true;

        public void SetSpectrumEnabled(bool enabled)
        {
            Enabled = enabled;
            Changes++;
        }

        public bool TryReadSpectrum(Span<float> bands) => true;
    }

    private sealed class FakeEngine : IPlaybackEngine, IBalanceControl
    {
        public double Balance { get; private set; }

        public Task SetBalanceAsync(double balance, CancellationToken cancellationToken = default)
        {
            Balance = balance;
            return Task.CompletedTask;
        }

        public PlaybackSnapshot Snapshot { get; set; } = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8);

        public event EventHandler<PlaybackSnapshot>? StateChanged { add { } remove { } }

        public Task LoadAsync(Track track, CancellationToken cancellationToken = default)
        {
            Snapshot = Snapshot with { Track = track, Status = PlaybackStatus.Stopped, Position = TimeSpan.Zero };
            return Task.CompletedTask;
        }

        public Task PlayAsync(CancellationToken cancellationToken = default) => SetStatus(PlaybackStatus.Playing);

        public Task PauseAsync(CancellationToken cancellationToken = default) => SetStatus(PlaybackStatus.Paused);

        public Task StopAsync(CancellationToken cancellationToken = default) => SetStatus(PlaybackStatus.Stopped);

        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private Task SetStatus(PlaybackStatus status)
        {
            Snapshot = Snapshot with { Status = status };
            return Task.CompletedTask;
        }
    }
}
