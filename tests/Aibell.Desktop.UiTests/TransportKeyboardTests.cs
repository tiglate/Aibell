using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Library;
using Aibell.Application.Playback;
using Aibell.Application.Radio;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Domain.Radio;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Radio;
using Aibell.Infrastructure.Settings;
using Aibell.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class TransportKeyboardTests
{
    private string directory = null!;
    private SqliteTrackRepository repository = null!;
    private JsonApplicationSettingsStore settings = null!;
    private LibraryMonitoringService monitoring = null!;
    private TransportTestPlaybackEngine engine = null!;
    private MainWindowViewModel model = null!;
    private MainWindow window = null!;
    private Track first = null!;
    private Track second = null!;

    private async Task OpenAsync(bool radio = false)
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-transport-keyboard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        repository = new(Path.Combine(directory, "library.db"));
        settings = new(Path.Combine(directory, "settings.json"));
        first = new(
            Guid.NewGuid(),
            new Uri(Path.Combine(directory, "first.flac")),
            "First Track",
            "Synthetic Artist",
            "Synthetic Album",
            TimeSpan.FromMinutes(2));
        second = new(
            Guid.NewGuid(),
            new Uri(Path.Combine(directory, "second.flac")),
            "Second Track",
            "Synthetic Artist",
            "Synthetic Album",
            TimeSpan.FromSeconds(90));
        foreach (Track track in new[] { first, second })
        {
            await repository.UpsertAsync(track, directory);
        }

        LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), repository);
        monitoring = new(repository, importer, NullLogger<LibraryMonitoringService>.Instance);
        engine = new();
        model = new(
            importer,
            repository,
            repository,
            repository,
            repository,
            settings,
            monitoring,
            engine,
            radioRepository: radio ? repository : null,
            radioPlaylistFiles: radio ? new RadioPlaylistImportService(new RadioPlaylistFileStore(), repository) : null);
        window = new(model);
        window.Show();
        await UntilAsync(() => !model.IsBusy && model.Tracks.Count == 2);
        model.SelectedTrack = model.Tracks.Single(row => row.Source.Id == first.Id);
    }

    [TearDown]
    public async Task TearDown()
    {
        engine?.SeekGate?.TrySetResult();
        engine?.VolumeGate?.TrySetResult();
        if (window is not null)
        {
            window.Close();
            await UntilAsync(() => !window.IsVisible);
        }

        model?.Dispose();
        monitoring?.Dispose();
        settings?.Dispose();
        if (repository is not null)
        {
            await repository.DisposeAsync();
        }

        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task TransportKeyboard_SeekArrowsPageHomeEnd_AdjustWithinTrackBoundsAndUpdateAutomationName()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);

        Grid seekSurface = window.FindControl<Grid>("PositionSeekSurface")!;
        ProgressBar seekProgress = window.FindControl<ProgressBar>("PositionProgress")!;
        Border seekFocusRing = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PositionSeekFocusRing");
        AutomationPeer seekPeer = ControlAutomationPeer.CreatePeerForElement(seekSurface)!;

        Assert.That(seekSurface.Focus(NavigationMethod.Tab), Is.True);
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(seekSurface.IsFocused, Is.True);
            Assert.That(seekFocusRing.IsVisible, Is.True);
            Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, 0:00 of 2:00"));
            Assert.That(seekPeer.GetHelpText(), Does.Contain("5 seconds"));
        });

        // Lower bound caps at 0:00.
        PressKey(PhysicalKey.ArrowLeft);
        PressKey(PhysicalKey.ArrowDown);
        PressKey(PhysicalKey.PageDown);
        await PumpAsync();
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
        Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, 0:00 of 2:00"));

        // Small and page increments forward and backward.
        PressKey(PhysicalKey.ArrowRight);
        await UntilAsync(() => engine.Snapshot.Position == TimeSpan.FromSeconds(5));
        Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, 0:05 of 2:00"));

        PressKey(PhysicalKey.ArrowUp);
        await UntilAsync(() => engine.Snapshot.Position == TimeSpan.FromSeconds(10));
        Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, 0:10 of 2:00"));

        PressKey(PhysicalKey.PageUp);
        await UntilAsync(() => engine.Snapshot.Position == TimeSpan.FromSeconds(25));
        Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, 0:25 of 2:00"));
        Assert.That(seekProgress.Value, Is.EqualTo(25));

        PressKey(PhysicalKey.ArrowLeft);
        await UntilAsync(() => engine.Snapshot.Position == TimeSpan.FromSeconds(20));

        PressKey(PhysicalKey.ArrowDown);
        await UntilAsync(() => engine.Snapshot.Position == TimeSpan.FromSeconds(15));

        PressKey(PhysicalKey.PageDown);
        await UntilAsync(() => engine.Snapshot.Position == TimeSpan.Zero);
        Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, 0:00 of 2:00"));

        // End jumps to duration and caps further forward keys at duration.
        PressKey(PhysicalKey.End);
        await UntilAsync(() => engine.Snapshot.Position == TimeSpan.FromMinutes(2));
        Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, 2:00 of 2:00"));

        PressKey(PhysicalKey.ArrowRight);
        PressKey(PhysicalKey.ArrowUp);
        PressKey(PhysicalKey.PageUp);
        await PumpAsync();
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromMinutes(2)));

        // Home returns to start.
        PressKey(PhysicalKey.Home);
        await UntilAsync(() => engine.Snapshot.Position == TimeSpan.Zero);
        Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, 0:00 of 2:00"));
    }

    [AvaloniaTest]
    public async Task TransportKeyboard_VolumeArrowsPageHomeEnd_ClampPersistAndPreserveMuteSemantics()
    {
        await OpenAsync();
        Grid volumeSurface = window.FindControl<Grid>("VolumeSurface")!;
        ProgressBar volumeProgress = window.FindControl<ProgressBar>("VolumeProgress")!;
        Border volumeFocusRing = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "VolumeFocusRing");
        AutomationPeer volumePeer = ControlAutomationPeer.CreatePeerForElement(volumeSurface)!;

        Assert.That(volumeSurface.Focus(NavigationMethod.Tab), Is.True);
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(volumeSurface.IsFocused, Is.True);
            Assert.That(volumeFocusRing.IsVisible, Is.True);
            Assert.That(volumePeer.GetName(), Is.EqualTo("Volume, 80%"));
            Assert.That(volumePeer.GetHelpText(), Does.Contain("5%"));
        });

        PressKey(PhysicalKey.ArrowLeft);
        await UntilAsync(() => model.Volume == 0.75);
        Assert.That(volumePeer.GetName(), Is.EqualTo("Volume, 75%"));

        PressKey(PhysicalKey.ArrowDown);
        await UntilAsync(() => model.Volume == 0.70);

        PressKey(PhysicalKey.PageDown);
        await UntilAsync(() => model.Volume == 0.60);

        PressKey(PhysicalKey.ArrowRight);
        await UntilAsync(() => model.Volume == 0.65);

        PressKey(PhysicalKey.ArrowUp);
        await UntilAsync(() => model.Volume == 0.70);

        PressKey(PhysicalKey.PageUp);
        await UntilAsync(() => model.Volume == 0.80);

        // Upper cap at 100%.
        PressKey(PhysicalKey.End);
        await UntilAsync(() => model.Volume == 1.0);
        Assert.That(volumePeer.GetName(), Is.EqualTo("Volume, 100%"));
        PressKey(PhysicalKey.ArrowRight);
        PressKey(PhysicalKey.ArrowUp);
        PressKey(PhysicalKey.PageUp);
        await PumpAsync();
        Assert.That(model.Volume, Is.EqualTo(1.0));

        // Lower cap at 0%.
        PressKey(PhysicalKey.Home);
        await UntilAsync(() => model.Volume == 0.0);
        Assert.That(volumePeer.GetName(), Is.EqualTo("Volume, 0%"));
        PressKey(PhysicalKey.ArrowLeft);
        PressKey(PhysicalKey.ArrowDown);
        PressKey(PhysicalKey.PageDown);
        await PumpAsync();
        Assert.That(model.Volume, Is.EqualTo(0.0));

        // Raise to 35%, start playback, mute via keyboard, and adjust volume while muted.
        PressKey(PhysicalKey.PageUp);
        PressKey(PhysicalKey.PageUp);
        PressKey(PhysicalKey.PageUp);
        PressKey(PhysicalKey.ArrowRight);
        await UntilAsync(() => model.Volume == 0.35);
        Assert.That(volumeProgress.Value, Is.EqualTo(0.35));

        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanMute);

        Button muteButton = window.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationPeerFor(button).GetName() is "Mute" or "Unmute");
        muteButton.Focus(NavigationMethod.Tab);
        PressKey(PhysicalKey.Space);
        await UntilAsync(() => engine.Snapshot.IsMuted);
        Dispatcher.UIThread.RunJobs();
        Assert.That(AutomationPeerFor(muteButton).GetName(), Is.EqualTo("Unmute"));
        Assert.That(volumePeer.GetName(), Is.EqualTo("Volume, 35% (muted)"));

        Assert.That(volumeSurface.Focus(NavigationMethod.Tab), Is.True);
        PressKey(PhysicalKey.ArrowRight);
        await UntilAsync(() => model.Volume == 0.40 && engine.Snapshot.Volume == 0.40);
        await model.FlushPreferencesAsync();
        var saved = await settings.LoadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.IsMuted, Is.True);
            Assert.That(volumePeer.GetName(), Is.EqualTo("Volume, 40% (muted)"));
            Assert.That(saved.Volume, Is.EqualTo(0.40));
            Assert.That(saved.IsMuted, Is.True);
        });
    }

    [AvaloniaTest]
    public async Task TransportKeyboard_DisabledStoppedAndLiveRadioSeek_IgnoreAdjustmentsAndReleaseFocus()
    {
        await OpenAsync(radio: true);
        Grid seekSurface = window.FindControl<Grid>("PositionSeekSurface")!;
        Grid volumeSurface = window.FindControl<Grid>("VolumeSurface")!;
        Border seekFocusRing = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PositionSeekFocusRing");
        AutomationPeer seekPeer = ControlAutomationPeer.CreatePeerForElement(seekSurface)!;

        // Stopped before playback: seek surface is disabled and cannot seek.
        Assert.Multiple(() =>
        {
            Assert.That(seekSurface.IsEnabled, Is.False);
            Assert.That(seekFocusRing.IsVisible, Is.False);
            Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, unavailable"));
        });
        PressKey(PhysicalKey.ArrowRight);
        await PumpAsync();
        Assert.That(engine.SeekCount, Is.Zero);

        // Start local track playback, focus the seek surface, and verify seeking works while paused too.
        await model.TogglePlaybackAsync();
        await UntilAsync(() => model.CanSeekPlayback && seekSurface.IsEnabled);
        Assert.That(seekSurface.Focus(NavigationMethod.Tab), Is.True);
        Dispatcher.UIThread.RunJobs();
        Assert.That(seekSurface.IsFocused, Is.True);
        Assert.That(seekFocusRing.IsVisible, Is.True);

        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Paused);
        PressKey(PhysicalKey.ArrowRight);
        await UntilAsync(() => engine.Snapshot.Position == TimeSpan.FromSeconds(5));

        // Loading a zero-duration track disables seek and releases focus from the seek surface.
        Track zeroDuration = new(
            Guid.NewGuid(),
            new Uri(Path.Combine(directory, "zero.flac")),
            "Zero Duration",
            "Synthetic Artist",
            "Synthetic Album",
            TimeSpan.Zero);
        await engine.LoadAsync(zeroDuration);
        await UntilAsync(() => !model.CanSeekPlayback && !seekSurface.IsEnabled);
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(seekSurface.IsKeyboardFocusWithin, Is.False);
            Assert.That(seekFocusRing.IsVisible, Is.False);
            Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, unavailable"));
        });

        // Resume local track playback, focus the seek surface, then switch to live Radio playback.
        model.SelectedTrack = model.Tracks.Single(row => row.Source.Id == first.Id);
        await model.TogglePlaybackAsync();
        await UntilAsync(() => model.CanSeekPlayback && seekSurface.IsEnabled);
        Assert.That(seekSurface.Focus(NavigationMethod.Tab), Is.True);
        Dispatcher.UIThread.RunJobs();
        Assert.That(seekSurface.IsFocused, Is.True);
        Assert.That(seekFocusRing.IsVisible, Is.True);

        RadioStation station = new(Guid.NewGuid(), "Live Station", new Uri("https://radio.example.invalid/stream"));
        await model.SaveRadioStationAsync(station, add: true);
        await model.PlayRadioStationAsync(station.Id);
        await UntilAsync(() => model.IsRadioPlayback);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(model.CanSeekPlayback, Is.False);
            Assert.That(model.ShowSeekBar, Is.False);
            Assert.That(seekSurface.IsEnabled, Is.False);
            Assert.That(seekSurface.IsVisible, Is.False);
            Assert.That(seekSurface.IsKeyboardFocusWithin, Is.False);
            Assert.That(seekFocusRing.IsVisible, Is.False);
            Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, LIVE"));
        });

        int seeksBeforeRadioKeys = engine.SeekCount;
        PressKey(PhysicalKey.ArrowRight);
        PressKey(PhysicalKey.PageUp);
        PressKey(PhysicalKey.End);
        await PumpAsync();
        Assert.That(engine.SeekCount, Is.EqualTo(seeksBeforeRadioKeys));

        // Volume surface remains keyboard-adjustable during live radio playback.
        Assert.That(volumeSurface.Focus(NavigationMethod.Tab), Is.True);
        PressKey(PhysicalKey.ArrowDown);
        await UntilAsync(() => model.Volume == 0.75 && engine.Snapshot.Volume == 0.75);
    }

    [AvaloniaTest]
    public async Task TransportKeyboard_FocusAndShortcutGuards_DoNotInterfereWithSearchListsOrPointer()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);
        await model.SeekAsync(30);
        await UntilAsync(() => engine.Snapshot.Position == TimeSpan.FromSeconds(30));

        Grid seekSurface = window.FindControl<Grid>("PositionSeekSurface")!;
        Grid volumeSurface = window.FindControl<Grid>("VolumeSurface")!;
        Border seekFocusRing = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PositionSeekFocusRing");
        TextBox search = window.FindControl<TextBox>("SearchBox")!;
        ListBox trackList = window.FindControl<ListBox>("TrackList")!;

        // Keys inside SearchBox must not seek, adjust volume, or toggle playback.
        search.Focus(NavigationMethod.Tab);
        window.KeyTextInput("First");
        PressKey(PhysicalKey.ArrowLeft);
        PressKey(PhysicalKey.ArrowRight);
        PressKey(PhysicalKey.Home);
        PressKey(PhysicalKey.End);
        PressKey(PhysicalKey.PageUp);
        PressKey(PhysicalKey.PageDown);
        PressKey(PhysicalKey.Space);
        await PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(model.Volume, Is.EqualTo(0.80));
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        });
        PressKey(PhysicalKey.Escape);
        await UntilAsync(() => model.SearchQuery.Length == 0 && model.Tracks.Count == 2);

        // Keys inside TrackList keep list navigation, Space pause/play, and Alt+Right track skip intact.
        trackList.UpdateLayout();
        ListBoxItem firstRow = trackList.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(item => ReferenceEquals(item.DataContext, model.SelectedTrack));
        Assert.That(firstRow.Focus(NavigationMethod.Directional), Is.True);
        PressKey(PhysicalKey.ArrowDown);
        await PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(model.Volume, Is.EqualTo(0.80));
        });

        // Space and Alt+Right on focused seek/volume surfaces do not trigger list transport or skip.
        Assert.That(seekSurface.Focus(NavigationMethod.Tab), Is.True);
        Dispatcher.UIThread.RunJobs();
        Assert.That(seekFocusRing.IsVisible, Is.True);
        PressKey(PhysicalKey.Space);
        PressKey(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        await PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
            Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(first.Id));
            Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(30)));
        });

        // Pointer drag on seek surface suppresses the keyboard focus ring while dragging.
        Point seekCenter = seekSurface.TranslatePoint(
            new Point(seekSurface.Bounds.Width / 2, seekSurface.Bounds.Height / 2),
            window)!.Value;
        window.MouseDown(seekCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.That(seekFocusRing.IsVisible, Is.False);
        window.MouseUp(seekCenter, MouseButton.Left);
        await PumpAsync();

        // Ctrl+F from a focused range surface still focuses SearchBox.
        Assert.That(volumeSurface.Focus(NavigationMethod.Tab), Is.True);
        PressKey(PhysicalKey.F, RawInputModifiers.Control);
        Assert.That(search.IsFocused, Is.True);
    }

    [AvaloniaTest]
    public async Task TransportKeyboard_RepeatedSeekWhileEngineIsPending_AppliesEachIncrementInOrder()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => model.CanSeekPlayback);
        Grid seekSurface = window.FindControl<Grid>("PositionSeekSurface")!;
        Assert.That(seekSurface.Focus(NavigationMethod.Tab), Is.True);
        engine.SeekGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PressKey(PhysicalKey.ArrowRight);
        await UntilAsync(() => engine.SeekWaiting);
        PressKey(PhysicalKey.ArrowRight);
        engine.SeekGate.SetResult();
        await UntilAsync(() => engine.CompletedSeeks == 2);

        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(10)));
    }

    [AvaloniaTest]
    public async Task TransportKeyboard_CloseWithQueuedSeek_DrainsActiveSeekAndDiscardsQueuedIncrement()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => model.CanSeekPlayback);
        Grid seekSurface = window.FindControl<Grid>("PositionSeekSurface")!;
        Assert.That(seekSurface.Focus(NavigationMethod.Tab), Is.True);
        engine.SeekGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PressKey(PhysicalKey.ArrowRight);
        await UntilAsync(() => engine.SeekWaiting);
        PressKey(PhysicalKey.ArrowRight);
        window.Close();
        Assert.That(window.IsVisible, Is.True);
        engine.SeekGate.SetResult();
        await UntilAsync(() => !window.IsVisible);

        Assert.Multiple(() =>
        {
            Assert.That(engine.CompletedSeeks, Is.EqualTo(1));
            Assert.That(engine.SeekCount, Is.EqualTo(1));
            Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(5)));
        });
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task TransportKeyboard_CloseDuringPendingVolumeOrSeek_DrainsOperationAndRejectsLaterKeys(bool testSeek)
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);

        if (testSeek)
        {
            Grid seekSurface = window.FindControl<Grid>("PositionSeekSurface")!;
            Assert.That(seekSurface.Focus(NavigationMethod.Tab), Is.True);
            engine.SeekGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

            PressKey(PhysicalKey.ArrowRight);
            await UntilAsync(() => engine.SeekWaiting);

            PressKey(PhysicalKey.Q, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.That(window.IsVisible, Is.True, "Close must wait for the in-flight keyboard seek to drain.");
                PressKey(PhysicalKey.ArrowRight);
                await PumpAsync();
                Assert.That(engine.SeekCount, Is.EqualTo(1), "Keys after close starts must be ignored.");
            }
            finally
            {
                engine.SeekGate.TrySetResult();
            }

            await UntilAsync(() => !window.IsVisible);
            Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(5)));
            return;
        }

        Grid volumeSurface = window.FindControl<Grid>("VolumeSurface")!;
        Assert.That(volumeSurface.Focus(NavigationMethod.Tab), Is.True);
        engine.VolumeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        PressKey(PhysicalKey.ArrowRight);
        await UntilAsync(() => engine.VolumeWaiting);

        PressKey(PhysicalKey.Q, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.That(window.IsVisible, Is.True, "Close must wait for the in-flight keyboard volume change to drain.");
            PressKey(PhysicalKey.ArrowRight);
            await PumpAsync();
            Assert.That(model.Volume, Is.EqualTo(0.85), "Keys after close starts must be ignored.");
        }
        finally
        {
            engine.VolumeGate.TrySetResult();
        }

        await UntilAsync(() => !window.IsVisible);
        Assert.That((await settings.LoadAsync()).Volume, Is.EqualTo(0.85));
    }

    private void PressKey(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }

    private static AutomationPeer AutomationPeerFor(Control control) =>
        ControlAutomationPeer.CreatePeerForElement(control)!;

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task PumpAsync()
    {
        for (int index = 0; index < 10; index++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private sealed class TransportTestPlaybackEngine : IPlaybackEngine
    {
        public PlaybackSnapshot Snapshot { get; private set; } = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8);
        public int SeekCount { get; private set; }
        public int CompletedSeeks { get; private set; }
        public TaskCompletionSource? SeekGate { get; set; }
        public bool SeekWaiting { get; private set; }
        public TaskCompletionSource? VolumeGate { get; set; }
        public bool VolumeWaiting { get; private set; }
        public event EventHandler<PlaybackSnapshot>? StateChanged;

        public Task LoadAsync(Track track, CancellationToken cancellationToken = default) =>
            Publish(Snapshot with { Track = track, Status = PlaybackStatus.Stopped, Position = TimeSpan.Zero });

        public Task PlayAsync(CancellationToken cancellationToken = default) =>
            Publish(Snapshot with { Status = PlaybackStatus.Playing });

        public Task PauseAsync(CancellationToken cancellationToken = default) =>
            Publish(Snapshot with { Status = PlaybackStatus.Paused });

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            Publish(Snapshot with { Status = PlaybackStatus.Stopped, Position = TimeSpan.Zero });

        public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            SeekCount++;
            if (SeekGate is not null)
            {
                SeekWaiting = true;
                await SeekGate.Task.WaitAsync(cancellationToken);
            }

            await Publish(Snapshot with { Position = position });
            CompletedSeeks++;
        }

        public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
        {
            if (VolumeGate is not null)
            {
                VolumeWaiting = true;
                await VolumeGate.Task.WaitAsync(cancellationToken);
            }

            await Publish(Snapshot with { Volume = volume });
        }

        public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default) =>
            Publish(Snapshot with { IsMuted = isMuted });

        private Task Publish(PlaybackSnapshot snapshot)
        {
            Snapshot = snapshot;
            StateChanged?.Invoke(this, snapshot);
            return Task.CompletedTask;
        }
    }
}
