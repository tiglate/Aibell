using System.Collections.Concurrent;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Library;
using Aibell.Application.Playback;
using Aibell.Application.Radio;
using Aibell.Desktop.Controls;
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
public sealed partial class TransportRangeAutomationTests
{
    private string directory = null!;
    private SqliteTrackRepository repository = null!;
    private JsonApplicationSettingsStore settings = null!;
    private LibraryMonitoringService monitoring = null!;
    private RangeTestPlaybackEngine engine = null!;
    private MainWindowViewModel model = null!;
    private MainWindow window = null!;
    private Track first = null!;
    private Track second = null!;

    private async Task OpenAsync(bool radio = false)
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-transport-range-{Guid.NewGuid():N}");
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
    public async Task TransportRange_PeerIdentityAndControlType_ExposesSliderRangeValueProviderAndPreservesVisualChildren()
    {
        await OpenAsync();

        TransportRangeSurface seekSurface = window.FindControl<TransportRangeSurface>("PositionSeekSurface")!;
        TransportRangeSurface volumeSurface = window.FindControl<TransportRangeSurface>("VolumeSurface")!;
        ProgressBar seekProgress = window.FindControl<ProgressBar>("PositionProgress")!;
        ProgressBar volumeProgress = window.FindControl<ProgressBar>("VolumeProgress")!;
        Border seekFocusRing = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PositionSeekFocusRing");
        Border volumeFocusRing = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "VolumeFocusRing");

        AutomationPeer seekPeer = ControlAutomationPeer.CreatePeerForElement(seekSurface);
        AutomationPeer seekPeerAgain = ControlAutomationPeer.FromElement(seekSurface)!;
        AutomationPeer volumePeer = ControlAutomationPeer.CreatePeerForElement(volumeSurface);
        AutomationPeer volumePeerAgain = ControlAutomationPeer.FromElement(volumeSurface)!;

        IRangeValueProvider? seekRange = seekPeer.GetProvider<IRangeValueProvider>();
        IRangeValueProvider? volumeRange = volumePeer.GetProvider<IRangeValueProvider>();

        Assert.Multiple(() =>
        {
            Assert.That(seekPeer, Is.InstanceOf<TransportRangeAutomationPeer>());
            Assert.That(seekPeerAgain, Is.SameAs(seekPeer));
            Assert.That(volumePeer, Is.InstanceOf<TransportRangeAutomationPeer>());
            Assert.That(volumePeerAgain, Is.SameAs(volumePeer));
            Assert.That(seekPeer.GetAutomationControlType(), Is.EqualTo(AutomationControlType.Slider));
            Assert.That(volumePeer.GetAutomationControlType(), Is.EqualTo(AutomationControlType.Slider));
            Assert.That(seekPeer.GetClassName(), Is.EqualTo(nameof(TransportRangeSurface)));
            Assert.That(volumePeer.GetClassName(), Is.EqualTo(nameof(TransportRangeSurface)));
            Assert.That(seekRange, Is.SameAs(seekPeer));
            Assert.That(volumeRange, Is.SameAs(volumePeer));
            Assert.That(seekProgress.IsHitTestVisible, Is.False);
            Assert.That(volumeProgress.IsHitTestVisible, Is.False);
        });

        await model.TogglePlaybackAsync();
        await UntilAsync(() => model.CanSeekPlayback);

        Assert.That(seekSurface.Focus(NavigationMethod.Tab), Is.True);
        Dispatcher.UIThread.RunJobs();
        Assert.That(seekFocusRing.IsVisible, Is.True);

        Assert.That(volumeSurface.Focus(NavigationMethod.Tab), Is.True);
        Dispatcher.UIThread.RunJobs();
        Assert.That(volumeFocusRing.IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task TransportRange_SeekSurface_ExposesSecondsRangeStepReadOnlyAndUpdatesThroughEngine()
    {
        await OpenAsync(radio: true);

        TransportRangeSurface seekSurface = window.FindControl<TransportRangeSurface>("PositionSeekSurface")!;
        ProgressBar seekProgress = window.FindControl<ProgressBar>("PositionProgress")!;
        AutomationPeer seekPeer = ControlAutomationPeer.CreatePeerForElement(seekSurface);
        IRangeValueProvider seekRange = seekPeer.GetProvider<IRangeValueProvider>()!;

        List<AutomationProperty> changedProperties = [];
        seekPeer.PropertyChanged += (_, args) => changedProperties.Add(args.Property);

        Assert.Multiple(() =>
        {
            Assert.That(seekRange.Minimum, Is.EqualTo(0d));
            Assert.That(seekRange.Maximum, Is.EqualTo(0d));
            Assert.That(seekRange.Value, Is.EqualTo(0d));
            Assert.That(seekRange.SmallChange, Is.EqualTo(5d));
            Assert.That(seekRange.LargeChange, Is.EqualTo(15d));
            Assert.That(seekRange.IsReadOnly, Is.True);
            Assert.That(seekPeer.IsEnabled(), Is.False);
            Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, unavailable"));
            Assert.That(seekPeer.GetHelpText(), Does.Contain("5 seconds").And.Contain("15 seconds"));
        });

        // Stopped surface rejects SetValue.
        seekRange.SetValue(15d);
        await PumpAsync();
        Assert.That(engine.SeekCount, Is.EqualTo(0));

        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);

        Assert.Multiple(() =>
        {
            Assert.That(seekRange.Minimum, Is.EqualTo(0d));
            Assert.That(seekRange.Maximum, Is.EqualTo(120d));
            Assert.That(seekRange.Value, Is.EqualTo(0d));
            Assert.That(seekRange.SmallChange, Is.EqualTo(5d));
            Assert.That(seekRange.LargeChange, Is.EqualTo(15d));
            Assert.That(seekRange.IsReadOnly, Is.False);
            Assert.That(seekPeer.IsEnabled(), Is.True);
            Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, 0:00 of 2:00"));
            Assert.That(changedProperties, Does.Contain(RangeValuePatternIdentifiers.MaximumProperty));
            Assert.That(changedProperties, Does.Contain(RangeValuePatternIdentifiers.IsReadOnlyProperty));
        });

        changedProperties.Clear();
        seekRange.SetValue(42.5d);
        await UntilAsync(() => engine.CompletedSeeks == 1);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(42.5)));
            Assert.That(seekRange.Value, Is.EqualTo(42.5d).Within(0.001));
            Assert.That(seekProgress.Value, Is.EqualTo(42.5d).Within(0.001));
            Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, 0:42 of 2:00"));
            Assert.That(changedProperties, Does.Contain(RangeValuePatternIdentifiers.ValueProperty));
            Assert.That(changedProperties, Does.Contain(AutomationElementIdentifiers.NameProperty));
        });

        // Unknown / zero-duration track marks seek read-only and unavailable.
        Track unknownDuration = new(
            Guid.NewGuid(),
            new Uri(Path.Combine(directory, "unknown.flac")),
            "Unknown Length",
            "Synthetic Artist",
            "Synthetic Album",
            TimeSpan.Zero);
        await engine.LoadAsync(unknownDuration);
        await engine.PlayAsync();
        await UntilAsync(() => !model.CanSeekPlayback && !seekSurface.IsEnabled);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(model.CanSeekPlayback, Is.False);
            Assert.That(seekRange.IsReadOnly, Is.True);
            Assert.That(seekPeer.IsEnabled(), Is.False);
            Assert.That(seekRange.Maximum, Is.EqualTo(0d));
            Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, unavailable"));
        });

        seekRange.SetValue(10d);
        await PumpAsync();
        Assert.That(engine.SeekCount, Is.EqualTo(1));

        // Live radio stream also disables seek and marks it read-only.
        RadioStation station = new(
            Guid.NewGuid(),
            "Nightwave",
            new Uri("https://radio.example.invalid/live.aac"),
            Genre: "Synth");
        await model.SaveRadioStationAsync(station, add: true);
        await model.PlayRadioStationAsync(station.Id);
        await UntilAsync(() => model.IsRadioPlayback && !model.CanSeekPlayback);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(seekRange.IsReadOnly, Is.True);
            Assert.That(seekPeer.IsEnabled(), Is.False);
            Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, LIVE"));
        });

        seekRange.SetValue(10d);
        await PumpAsync();
        Assert.That(engine.SeekCount, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task TransportRange_HlsRadioStation_IsLiveWithStopAndNoSeek()
    {
        await OpenAsync(radio: true);
        TransportRangeSurface seekSurface = window.FindControl<TransportRangeSurface>("PositionSeekSurface")!;
        AutomationPeer seekPeer = ControlAutomationPeer.CreatePeerForElement(seekSurface);
        IRangeValueProvider seekRange = seekPeer.GetProvider<IRangeValueProvider>()!;
        RadioStation station = new(Guid.NewGuid(), "HLS Live", new Uri("https://radio.example.invalid/live/master.m3u8"));

        await model.SaveRadioStationAsync(station, add: true);
        await model.PlayRadioStationAsync(station.Id);
        await UntilAsync(() => model.IsRadioPlayback && !model.CanSeekPlayback);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            // The HLS address reaches the engine unchanged; the transport treats it like any other live station.
            Assert.That(engine.Snapshot.Track!.Location, Is.EqualTo(station.Stream));
            Assert.That(model.DurationText, Is.EqualTo("LIVE"));
            Assert.That(model.ShowRadioStop, Is.True);
            Assert.That(model.ShowPlaybackPause, Is.False);
            Assert.That(model.ShowSeekBar, Is.False);
            Assert.That(seekRange.IsReadOnly, Is.True);
            Assert.That(seekPeer.GetName(), Is.EqualTo("Seek position, LIVE"));
        });
    }

    [AvaloniaTest]
    public async Task TransportRange_VolumeSurface_WorksWithoutLoadedTrackPersistsVolumeAndPreservesMuteAndRadio()
    {
        await OpenAsync(radio: true);

        TransportRangeSurface volumeSurface = window.FindControl<TransportRangeSurface>("VolumeSurface")!;
        ProgressBar volumeProgress = window.FindControl<ProgressBar>("VolumeProgress")!;
        AutomationPeer volumePeer = ControlAutomationPeer.CreatePeerForElement(volumeSurface);
        IRangeValueProvider volumeRange = volumePeer.GetProvider<IRangeValueProvider>()!;

        List<AutomationProperty> changedProperties = [];
        volumePeer.PropertyChanged += (_, args) => changedProperties.Add(args.Property);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Track, Is.Null);
            Assert.That(volumeRange.Minimum, Is.EqualTo(0d));
            Assert.That(volumeRange.Maximum, Is.EqualTo(1d));
            Assert.That(volumeRange.Value, Is.EqualTo(0.8d).Within(0.0001));
            Assert.That(volumeRange.SmallChange, Is.EqualTo(0.05d).Within(0.0001));
            Assert.That(volumeRange.LargeChange, Is.EqualTo(0.10d).Within(0.0001));
            Assert.That(volumeRange.IsReadOnly, Is.False);
            Assert.That(volumePeer.IsEnabled(), Is.True);
            Assert.That(volumePeer.GetName(), Is.EqualTo("Volume, 80%"));
            Assert.That(volumePeer.GetHelpText(), Does.Contain("5%").And.Contain("10%"));
        });

        volumeRange.SetValue(0.35d);
        await UntilAsync(() => Math.Abs(model.Volume - 0.35d) < 0.0001);
        await model.FlushPreferencesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(volumeRange.Value, Is.EqualTo(0.35d).Within(0.0001));
            Assert.That(volumeProgress.Value, Is.EqualTo(0.35d).Within(0.0001));
            Assert.That(volumePeer.GetName(), Is.EqualTo("Volume, 35%"));
            Assert.That(changedProperties, Does.Contain(RangeValuePatternIdentifiers.ValueProperty));
            Assert.That(changedProperties, Does.Contain(AutomationElementIdentifiers.NameProperty));
        });
        Assert.That((await settings.LoadAsync()).Volume, Is.EqualTo(0.35d).Within(0.0001));

        // Start playback so mute is active, mute, and verify mute state is preserved across range SetValue updates.
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanMute);
        await model.ToggleMuteAsync();
        await UntilAsync(() => engine.Snapshot.IsMuted);
        Dispatcher.UIThread.RunJobs();
        Assert.That(volumePeer.GetName(), Is.EqualTo("Volume, 35% (muted)"));

        volumeRange.SetValue(0.60d);
        await UntilAsync(() => Math.Abs(engine.Snapshot.Volume - 0.60d) < 0.0001);
        await model.FlushPreferencesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.IsMuted, Is.True);
            Assert.That(volumeRange.Value, Is.EqualTo(0.60d).Within(0.0001));
            Assert.That(volumePeer.GetName(), Is.EqualTo("Volume, 60% (muted)"));
        });
        Assert.That((await settings.LoadAsync()).Volume, Is.EqualTo(0.60d).Within(0.0001));

        // Volume remains writable during live radio playback.
        RadioStation station = new(
            Guid.NewGuid(),
            "Nightwave",
            new Uri("https://radio.example.invalid/live.aac"),
            Genre: "Synth");
        await model.SaveRadioStationAsync(station, add: true);
        await model.PlayRadioStationAsync(station.Id);
        await UntilAsync(() => model.IsRadioPlayback);

        volumeRange.SetValue(0.25d);
        await UntilAsync(() => Math.Abs(engine.Snapshot.Volume - 0.25d) < 0.0001);
        await PumpAsync();

        Assert.Multiple(() =>
        {
            Assert.That(volumeRange.IsReadOnly, Is.False);
            Assert.That(volumeRange.Value, Is.EqualTo(0.25d).Within(0.0001));
        });
    }

    [AvaloniaTest]
    public async Task TransportRange_InvalidAndUnsafeValues_AreRejectedWithoutClampingAndUnsafeStateDisablesSurface()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);

        TransportRangeSurface seekSurface = window.FindControl<TransportRangeSurface>("PositionSeekSurface")!;
        TransportRangeSurface volumeSurface = window.FindControl<TransportRangeSurface>("VolumeSurface")!;
        AutomationPeer seekPeer = ControlAutomationPeer.CreatePeerForElement(seekSurface);
        AutomationPeer volumePeer = ControlAutomationPeer.CreatePeerForElement(volumeSurface);
        IRangeValueProvider seekRange = seekPeer.GetProvider<IRangeValueProvider>()!;
        IRangeValueProvider volumeRange = volumePeer.GetProvider<IRangeValueProvider>()!;

        int baselineVolumeCalls = engine.VolumeCount;
        seekRange.SetValue(30d);
        await UntilAsync(() => engine.CompletedSeeks == 1);
        Dispatcher.UIThread.RunJobs();
        Assert.That(seekRange.Value, Is.EqualTo(30d).Within(0.001));

        double[] invalidSeekValues =
        [
            double.NaN,
            double.PositiveInfinity,
            double.NegativeInfinity,
            -0.001d,
            -10d,
            120.001d,
            500d
        ];
        foreach (double invalid in invalidSeekValues)
        {
            seekRange.SetValue(invalid);
        }

        double[] invalidVolumeValues =
        [
            double.NaN,
            double.PositiveInfinity,
            double.NegativeInfinity,
            -0.001d,
            -1d,
            1.001d,
            5d
        ];
        foreach (double invalid in invalidVolumeValues)
        {
            volumeRange.SetValue(invalid);
        }

        await PumpAsync();

        Assert.Multiple(() =>
        {
            Assert.That(engine.SeekCount, Is.EqualTo(1));
            Assert.That(seekRange.Value, Is.EqualTo(30d).Within(0.001));
            Assert.That(engine.VolumeCount, Is.EqualTo(baselineVolumeCalls));
            Assert.That(volumeRange.Value, Is.EqualTo(0.8d).Within(0.0001));
        });

        // Unsafe state pushed to UpdateRangeState explicitly disables the surface rather than clamping.
        seekSurface.UpdateRangeState(
            minimum: 0d,
            maximum: 120d,
            value: double.NaN,
            smallChange: 5d,
            largeChange: 15d,
            isInteractive: true);
        Assert.Multiple(() =>
        {
            Assert.That(seekRange.IsReadOnly, Is.True);
            Assert.That(seekPeer.IsEnabled(), Is.False);
            Assert.That(seekRange.Value, Is.EqualTo(0d));
        });

        seekRange.SetValue(45d);
        await PumpAsync();
        Assert.That(engine.SeekCount, Is.EqualTo(1));

        // Unsafe non-finite volume snapshot from engine disables volume surface explicitly.
        await engine.PublishRawSnapshotAsync(engine.Snapshot with { Volume = double.PositiveInfinity });
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(volumeRange.IsReadOnly, Is.True);
            Assert.That(volumePeer.IsEnabled(), Is.False);
            Assert.That(volumeRange.Value, Is.EqualTo(0d));
        });

        volumeRange.SetValue(0.5d);
        await PumpAsync();
        Assert.That(engine.VolumeCount, Is.EqualTo(baselineVolumeCalls));
    }

    [AvaloniaTest]
    public async Task TransportRange_NonOptimisticCompletionAndEngineFailure_DoNotLeakFaultsOrClaimSuccess()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);

        TransportRangeSurface seekSurface = window.FindControl<TransportRangeSurface>("PositionSeekSurface")!;
        TransportRangeSurface volumeSurface = window.FindControl<TransportRangeSurface>("VolumeSurface")!;
        ProgressBar seekProgress = window.FindControl<ProgressBar>("PositionProgress")!;
        ProgressBar volumeProgress = window.FindControl<ProgressBar>("VolumeProgress")!;
        IRangeValueProvider seekRange = ControlAutomationPeer.CreatePeerForElement(seekSurface).GetProvider<IRangeValueProvider>()!;
        IRangeValueProvider volumeRange = ControlAutomationPeer.CreatePeerForElement(volumeSurface).GetProvider<IRangeValueProvider>()!;

        // 1. Non-optimistic while gate is pending.
        engine.SeekGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        seekRange.SetValue(60d);
        await UntilAsync(() => engine.SeekWaiting);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(seekRange.Value, Is.EqualTo(0d), "Seek range value must not update optimistically before engine completes.");
            Assert.That(seekProgress.Value, Is.EqualTo(0d), "Seek progress bar must not update optimistically before engine completes.");
        });

        engine.SeekGate.TrySetResult();
        await UntilAsync(() => engine.CompletedSeeks == 1);
        Dispatcher.UIThread.RunJobs();
        Assert.That(seekRange.Value, Is.EqualTo(60d).Within(0.001));

        // 2. Engine seek exception does not leak and keeps committed value.
        engine.SeekGate = null;
        engine.NextSeekException = new InvalidOperationException("Simulated seek engine failure.");
        seekRange.SetValue(90d);
        await PumpAsync();

        Assert.Multiple(() =>
        {
            Assert.That(seekRange.Value, Is.EqualTo(60d).Within(0.001));
            Assert.That(seekProgress.Value, Is.EqualTo(60d).Within(0.001));
            Assert.That(model.StatusMessage, Is.EqualTo("Simulated seek engine failure."));
        });

        // 3. Engine volume cancellation and exception do not leak or claim success.
        engine.NextVolumeException = new OperationCanceledException("Simulated volume cancellation.");
        volumeRange.SetValue(0.25d);
        await PumpAsync();
        Assert.That(volumeRange.Value, Is.EqualTo(0.8d).Within(0.0001));

        engine.NextVolumeException = new InvalidOperationException("Simulated volume engine failure.");
        volumeRange.SetValue(0.25d);
        await PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(volumeRange.Value, Is.EqualTo(0.8d).Within(0.0001));
            Assert.That(volumeProgress.Value, Is.EqualTo(0.8d).Within(0.0001));
            Assert.That(model.StatusMessage, Is.EqualTo("Simulated volume engine failure."));
        });
    }

    [AvaloniaTest]
    public async Task TransportRange_RepeatedRequestsTrackChangeAndWindowClose_SerializeOrderDiscardStaleSeekAndDrainOnClose()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);

        TransportRangeSurface seekSurface = window.FindControl<TransportRangeSurface>("PositionSeekSurface")!;
        TransportRangeSurface volumeSurface = window.FindControl<TransportRangeSurface>("VolumeSurface")!;
        IRangeValueProvider seekRange = ControlAutomationPeer.CreatePeerForElement(seekSurface).GetProvider<IRangeValueProvider>()!;
        IRangeValueProvider volumeRange = ControlAutomationPeer.CreatePeerForElement(volumeSurface).GetProvider<IRangeValueProvider>()!;

        // 1. Repeated requests serialize in FIFO order.
        engine.SeekGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        seekRange.SetValue(20d);
        seekRange.SetValue(55d);
        seekRange.SetValue(95d);
        await UntilAsync(() => engine.SeekWaiting);
        Assert.That(engine.SeekCount, Is.EqualTo(1));

        engine.SeekGate.TrySetResult();
        engine.SeekGate = null;
        await UntilAsync(() => engine.CompletedSeeks == 3);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(engine.RequestedSeeks, Is.EqualTo(new[]
            {
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(55),
                TimeSpan.FromSeconds(95)
            }));
            Assert.That(seekRange.Value, Is.EqualTo(95d).Within(0.001));
        });

        // 2. Track change while first seek is blocked discards queued stale seek for previous track.
        engine.SeekGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        seekRange.SetValue(30d);
        seekRange.SetValue(70d);
        await UntilAsync(() => engine.SeekWaiting);

        await engine.LoadAsync(second);
        await engine.PlayAsync();
        engine.SeekGate.TrySetResult();
        engine.SeekGate = null;
        await PumpAsync();

        Assert.Multiple(() =>
        {
            Assert.That(engine.SeekCount, Is.EqualTo(4), "Queued seek for the previous track should be discarded when track changes.");
            Assert.That(seekRange.Maximum, Is.EqualTo(90d));
        });

        // 3. Window close drains active volume operation before flushing settings.
        engine.VolumeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        volumeRange.SetValue(0.92d);
        await UntilAsync(() => engine.VolumeWaiting);

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.That(window.IsVisible, Is.True);

        engine.VolumeGate.TrySetResult();
        await UntilAsync(() => !window.IsVisible);
        Assert.That((await settings.LoadAsync()).Volume, Is.EqualTo(0.92d).Within(0.0001));
    }

    [AvaloniaTest]
    public async Task TransportRange_WorkerThreadAndDisposedOrUnconfiguredSurface_MarshalSafelyAndRejectLateCalls()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);

        TransportRangeSurface seekSurface = window.FindControl<TransportRangeSurface>("PositionSeekSurface")!;
        TransportRangeSurface volumeSurface = window.FindControl<TransportRangeSurface>("VolumeSurface")!;
        IRangeValueProvider seekRange = ControlAutomationPeer.CreatePeerForElement(seekSurface).GetProvider<IRangeValueProvider>()!;
        IRangeValueProvider volumeRange = ControlAutomationPeer.CreatePeerForElement(volumeSurface).GetProvider<IRangeValueProvider>()!;

        // 1. Worker-thread reads and SetValue calls marshal safely to UI thread.
        Task<(double Min, double Max, double Small, double Large, bool ReadOnly)> workerReadTask = Task.Run(() =>
        {
            seekRange.SetValue(48d);
            volumeRange.SetValue(0.45d);
            return (seekRange.Minimum, seekRange.Maximum, seekRange.SmallChange, seekRange.LargeChange, seekRange.IsReadOnly);
        });

        await UntilAsync(() => workerReadTask.IsCompleted && engine.CompletedSeeks == 1 && Math.Abs(engine.Snapshot.Volume - 0.45d) < 0.0001);
        (double min, double max, double small, double large, bool readOnly) = await workerReadTask;
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(min, Is.EqualTo(0d));
            Assert.That(max, Is.EqualTo(120d));
            Assert.That(small, Is.EqualTo(5d));
            Assert.That(large, Is.EqualTo(15d));
            Assert.That(readOnly, Is.False);
            Assert.That(seekRange.Value, Is.EqualTo(48d).Within(0.001));
            Assert.That(volumeRange.Value, Is.EqualTo(0.45d).Within(0.0001));
        });

        // 2. Missing handler (ConfigureRange(null)) marks surface read-only and rejects SetValue.
        seekSurface.ConfigureRange(null);
        Assert.That(seekRange.IsReadOnly, Is.True);
        await Task.Run(() => seekRange.SetValue(75d));
        await PumpAsync();
        Assert.That(engine.SeekCount, Is.EqualTo(1));

        // 3. Closed window / disposed surface rejects late calls from both UI and worker threads.
        window.Close();
        await UntilAsync(() => !window.IsVisible);

        seekRange.SetValue(15d);
        volumeRange.SetValue(0.15d);
        await Task.Run(() =>
        {
            seekRange.SetValue(25d);
            volumeRange.SetValue(0.25d);
        });
        await PumpAsync();

        Assert.Multiple(() =>
        {
            Assert.That(seekRange.IsReadOnly, Is.True);
            Assert.That(volumeRange.IsReadOnly, Is.True);
            Assert.That(engine.SeekCount, Is.EqualTo(1));
            Assert.That(engine.Snapshot.Volume, Is.EqualTo(0.45d).Within(0.0001));
        });
    }

    [AvaloniaTest]
    public async Task TransportRange_WorkerSeekAcceptedForPreviousTrack_IsDiscardedWhenNewTrackLoadsBeforeUiPost()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);

        TransportRangeSurface seekSurface = window.FindControl<TransportRangeSurface>("PositionSeekSurface")!;
        IRangeValueProvider seekRange = ControlAutomationPeer.CreatePeerForElement(seekSurface).GetProvider<IRangeValueProvider>()!;

        // The UI thread is not pumping, so the accepted worker request stays queued until after the track swap.
        RunOnWorkerWithoutPumping(() => seekRange.SetValue(30d));
        await engine.LoadAsync(second);
        await engine.PlayAsync();
        await PumpAsync();

        Assert.Multiple(() =>
        {
            Assert.That(engine.SeekCount, Is.EqualTo(0), "A request accepted for the first track must never seek the second: ");
            Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
            Assert.That(seekRange.Maximum, Is.EqualTo(90d));
        });
    }

    [AvaloniaTest]
    public async Task TransportRange_WorkerVolumeAcceptedBeforeUiPost_IsDrainedIncludingNestedEngineWorkOnClose()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);

        TransportRangeSurface volumeSurface = window.FindControl<TransportRangeSurface>("VolumeSurface")!;
        IRangeValueProvider volumeRange = ControlAutomationPeer.CreatePeerForElement(volumeSurface).GetProvider<IRangeValueProvider>()!;

        engine.VolumeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnWorkerWithoutPumping(() => volumeRange.SetValue(0.33d));

        // The posted request starts on the UI thread and then awaits nested engine work; close must wait for it.
        await UntilAsync(() => engine.VolumeWaiting);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.That(window.IsVisible, Is.True, "Close must wait for the nested asynchronous engine work.");

        engine.VolumeGate.TrySetResult();
        await UntilAsync(() => !window.IsVisible);
        Assert.That((await settings.LoadAsync()).Volume, Is.EqualTo(0.33d).Within(0.0001));
    }

    [AvaloniaTest]
    public async Task TransportRange_WorkerRequestAcceptedThenWindowClosedBeforeUiPost_CompletesCloseWithoutEngineWork()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);

        TransportRangeSurface volumeSurface = window.FindControl<TransportRangeSurface>("VolumeSurface")!;
        IRangeValueProvider volumeRange = ControlAutomationPeer.CreatePeerForElement(volumeSurface).GetProvider<IRangeValueProvider>()!;

        RunOnWorkerWithoutPumping(() => volumeRange.SetValue(0.33d));
        window.Close();
        await UntilAsync(() => !window.IsVisible);

        Assert.That((await settings.LoadAsync()).Volume, Is.EqualTo(engine.Snapshot.Volume).Within(0.0001));
    }

    [AvaloniaTest]
    public async Task TransportRange_AncestorDisabledOrHidden_InvalidatesSnapshotAndRejectsWorkerRequests()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && model.CanSeekPlayback);

        TransportRangeSurface seekSurface = window.FindControl<TransportRangeSurface>("PositionSeekSurface")!;
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(seekSurface);
        IRangeValueProvider seekRange = peer.GetProvider<IRangeValueProvider>()!;
        Control ancestor = (Control)seekSurface.Parent!;
        Assert.That(seekRange.IsReadOnly, Is.False);

        ancestor.IsEnabled = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(peer.IsEnabled(), Is.False);
            Assert.That(seekRange.IsReadOnly, Is.True);
        });
        await Task.Run(() => seekRange.SetValue(40d));
        await PumpAsync();
        Assert.That(engine.SeekCount, Is.EqualTo(0));

        ancestor.IsEnabled = true;
        Dispatcher.UIThread.RunJobs();
        Assert.That(seekRange.IsReadOnly, Is.False);

        ancestor.IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(peer.IsEnabled(), Is.False);
            Assert.That(seekRange.IsReadOnly, Is.True);
        });
        await Task.Run(() => seekRange.SetValue(40d));
        await PumpAsync();
        Assert.That(engine.SeekCount, Is.EqualTo(0));

        ancestor.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        Assert.That(seekRange.IsReadOnly, Is.False);
    }

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

    // Task.Wait would pump the headless dispatcher and run the posted request early.
    private static void RunOnWorkerWithoutPumping(Action action)
    {
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        })
        { IsBackground = true };
        worker.Start();
        Assert.That(worker.Join(TimeSpan.FromSeconds(5)), Is.True, "Worker requests must return without UI pumping.");
        Assert.That(failure, Is.Null);
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

    private sealed class RangeTestPlaybackEngine : IPlaybackEngine
    {
        private readonly ConcurrentQueue<TimeSpan> requestedSeeks = new();

        public PlaybackSnapshot Snapshot { get; private set; } = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8);
        public int SeekCount { get; private set; }
        public int CompletedSeeks { get; private set; }
        public int VolumeCount { get; private set; }
        public IReadOnlyCollection<TimeSpan> RequestedSeeks => requestedSeeks.ToArray();
        public TaskCompletionSource? SeekGate { get; set; }
        public bool SeekWaiting { get; private set; }
        public TaskCompletionSource? VolumeGate { get; set; }
        public bool VolumeWaiting { get; private set; }
        public Exception? NextSeekException { get; set; }
        public Exception? NextVolumeException { get; set; }
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
            requestedSeeks.Enqueue(position);
            if (SeekGate is not null)
            {
                SeekWaiting = true;
                await SeekGate.Task.WaitAsync(cancellationToken);
                SeekWaiting = false;
            }

            if (NextSeekException is not null)
            {
                Exception exception = NextSeekException;
                NextSeekException = null;
                throw exception;
            }

            await Publish(Snapshot with { Position = position });
            CompletedSeeks++;
        }

        public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
        {
            VolumeCount++;
            if (VolumeGate is not null)
            {
                VolumeWaiting = true;
                await VolumeGate.Task.WaitAsync(cancellationToken);
                VolumeWaiting = false;
            }

            if (NextVolumeException is not null)
            {
                Exception exception = NextVolumeException;
                NextVolumeException = null;
                throw exception;
            }

            await Publish(Snapshot with { Volume = volume });
        }

        public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default) =>
            Publish(Snapshot with { IsMuted = isMuted });

        public Task PublishRawSnapshotAsync(PlaybackSnapshot snapshot) =>
            Publish(snapshot);

        private Task Publish(PlaybackSnapshot snapshot)
        {
            Snapshot = snapshot;
            StateChanged?.Invoke(this, snapshot);
            return Task.CompletedTask;
        }
    }
}
