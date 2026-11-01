using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Devices;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class DeviceSyncViewTests
{
    private static readonly RemovableVolume Stick = new("/media/u/STICK", "STICK", "/media/u/STICK", 4_000_000, 3_000_000, "vfat");
    private static readonly Track One = new(Guid.NewGuid(), new Uri("file:///music/a.mp3"), "One", "Band", "Album", TimeSpan.FromMinutes(3));
    private static readonly Track Two = new(Guid.NewGuid(), new Uri("file:///music/b.mp3"), "Two", "Band", "Album", TimeSpan.FromMinutes(3), TrackNumber: 2);
    private readonly List<(Window Window, DeviceSyncViewModel Model)> windows = [];

    [TearDown]
    public async Task CloseWindows()
    {
        foreach ((Window window, DeviceSyncViewModel model) in windows) { await model.ShutdownAsync(); model.Dispose(); window.Close(); }
        windows.Clear();
    }

    [AvaloniaTest]
    public async Task Open_NoDrive_ExplainsAndDisablesPreview()
    {
        (Window window, _) = await OpenAsync([], new FakeSync());

        Assert.That(Text("SyncStatus", window), Does.Contain("No removable drive"));
        Assert.That(Enabled("PreviewSync", window), Is.False);
        Assert.That(Enabled("StartSync", window), Is.False);
    }

    [AvaloniaTest]
    public async Task Preview_ThenCopy_RunsExactlyTheShownPlanAndReportsTheResult()
    {
        FakeSync sync = new();
        (Window window, DeviceSyncViewModel model) = await OpenAsync([Stick], sync);

        Click("PreviewSync", window);
        await UntilAsync(() => model.CanSync);

        Assert.That(sync.Planned, Has.Count.EqualTo(1));
        Assert.That(Text("SyncSummary", window), Does.Contain("2 file(s) to copy").And.Contain("1 already on the device"));
        Assert.That(Text("SyncStatus", window), Does.Contain("Press Copy to device"));

        Click("StartSync", window);
        await UntilAsync(() => sync.Executed.Count == 1 && !model.IsBusy);

        Assert.That(sync.Executed.Single(), Is.SameAs(sync.Planned.Single()));
        Assert.That(Text("SyncSummary", window), Does.Contain("2 file(s) copied").And.Contain("Aibell.m3u8"));
        Assert.That(Text("SyncStatus", window), Does.StartWith("Finished"));
        Assert.That(Enabled("StartSync", window), Is.False, "the preview is used up by the sync");
    }

    [AvaloniaTest]
    public async Task ChangingAnOptionOrSource_ThrowsTheOldPreviewAway()
    {
        (Window window, DeviceSyncViewModel model) = await OpenAsync([Stick], new FakeSync());
        Click("PreviewSync", window);
        await UntilAsync(() => model.CanSync);

        model.ReplaceDifferent = true;
        Assert.That(model.CanSync, Is.False);
        Assert.That(model.Summary, Is.Empty);

        Click("PreviewSync", window);
        await UntilAsync(() => model.CanSync);
        model.SelectedSource = model.Sources[0];
        Assert.That(model.CanSync, Is.False);
    }

    [AvaloniaTest]
    public async Task Preview_DeviceTooSmall_ShowsHowMuchIsMissingAndDoesNotAllowCopying()
    {
        FakeSync sync = new() { Free = 100 };
        (Window window, DeviceSyncViewModel model) = await OpenAsync([Stick with { FreeBytes = 100 }], sync);

        Click("PreviewSync", window);
        await UntilAsync(() => model.Summary.Length > 0);

        Assert.That(Text("SyncStatus", window), Does.Contain("not have enough free space"));
        Assert.That(Text("SyncSummary", window), Does.Contain("more space is needed"));
        Assert.That(Enabled("StartSync", window), Is.False);
    }

    [AvaloniaTest]
    public async Task Preview_NothingSelected_SaysSoWithoutPlanning()
    {
        FakeSync sync = new();
        (Window window, DeviceSyncViewModel model) = await OpenAsync([Stick], sync, selected: []);
        model.SelectedSource = model.Sources[0];

        Click("PreviewSync", window);
        await UntilAsync(() => Text("SyncStatus", window).Contains("no tracks", StringComparison.OrdinalIgnoreCase));

        Assert.That(sync.Planned, Is.Empty);
        Assert.That(Enabled("StartSync", window), Is.False);
    }

    [AvaloniaTest]
    public async Task Preview_WholeLibrary_UsesTheLibrarySource()
    {
        FakeSync sync = new();
        (Window window, DeviceSyncViewModel model) = await OpenAsync([Stick], sync);
        model.SelectedSource = model.Sources[2];

        Click("PreviewSync", window);
        await UntilAsync(() => model.CanSync);

        Assert.That(sync.PlannedTrackCounts, Is.EqualTo(Items(2)), "the library has two tracks while the view has one");
    }

    [AvaloniaTest]
    public async Task Copy_CanBeCancelled_AndTheWindowSaysFilesStay()
    {
        FakeSync sync = new() { Hold = true };
        (Window window, DeviceSyncViewModel model) = await OpenAsync([Stick], sync);
        Click("PreviewSync", window);
        await UntilAsync(() => model.CanSync);

        Click("StartSync", window);
        await UntilAsync(() => sync.Started);
        Assert.That(window.GetVisualDescendants().OfType<ProgressBar>().Single(bar => bar.Name == "SyncProgress").IsVisible, Is.True);
        Click("CancelSync", window);
        await UntilAsync(() => !model.IsBusy);

        Assert.That(Text("SyncStatus", window), Does.Contain("Cancelled"));
    }

    [AvaloniaTest]
    public async Task Shutdown_WhileCopying_CancelsTheCopy()
    {
        FakeSync sync = new() { Hold = true };
        (Window window, DeviceSyncViewModel model) = await OpenAsync([Stick], sync);
        Click("PreviewSync", window);
        await UntilAsync(() => model.CanSync);
        Click("StartSync", window);
        await UntilAsync(() => sync.Started);

        await model.ShutdownAsync();
        await UntilAsync(() => sync.Cancelled);

        Assert.That(sync.Cancelled, Is.True);
    }

    [AvaloniaTest]
    public async Task Copy_FailureOfOneFile_IsListedInTheSummary()
    {
        FakeSync sync = new() { Failures = ["Music/Band/Album/01 - One.mp3: disk error"] };
        (Window window, DeviceSyncViewModel model) = await OpenAsync([Stick], sync);
        Click("PreviewSync", window);
        await UntilAsync(() => model.CanSync);

        Click("StartSync", window);
        await UntilAsync(() => !model.IsBusy && sync.Executed.Count == 1);

        Assert.That(Text("SyncStatus", window), Does.Contain("1 file(s) could not be copied"));
        Assert.That(Text("SyncSummary", window), Does.Contain("Problem: Music/Band/Album/01 - One.mp3: disk error"));
    }

    [AvaloniaTest]
    public async Task View_FollowsTheUiLanguage()
    {
        System.Globalization.CultureInfo saved = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
            (Window window, _) = await OpenAsync([Stick], new FakeSync());

            Assert.That(window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PreviewSync").Content, Is.EqualTo("Prévia"));
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = saved; }
    }

    private async Task<(Window, DeviceSyncViewModel)> OpenAsync(RemovableVolume[] volumes, FakeSync sync, Track[]? selected = null)
    {
        DeviceSyncViewModel model = new(new FakeVolumes(volumes), sync, new FakeSource(selected ?? [One]));
        DeviceSyncView view = new() { DataContext = model };
        Window window = new() { Width = 640, Height = 560, Content = view };
        windows.Add((window, model));
        window.Show();
        _ = model.LoadAsync();
        await UntilAsync(() => !model.IsBusy && model.StatusMessage.Length > 0);
        Dispatcher.UIThread.RunJobs();
        return (window, model);
    }

    private static string Text(string name, Window window) => window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Name == name).Text ?? "";

    private static bool Enabled(string name, Window window) => window.GetVisualDescendants().OfType<Button>().First(button => button.Name == name).IsEnabled;

    private static void Click(string name, Window window)
    {
        Dispatcher.UIThread.RunJobs();
        Button button = window.GetVisualDescendants().OfType<Button>().First(candidate => candidate.Name == name);
        Assert.That(button.IsEnabled && button.IsVisible, Is.True, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }

    private sealed class FakeVolumes(RemovableVolume[] volumes) : IRemovableVolumeService
    {
        public IReadOnlyList<RemovableVolume> List() => volumes;
    }

    private sealed class FakeSource(Track[] selected) : IDeviceTrackSource
    {
        public IReadOnlyList<Track> SelectedTracks() => selected;
        public IReadOnlyList<Track> VisibleTracks() => [One];
        public Task<IReadOnlyList<Track>> AllTracksAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Track>>([One, Two]);
    }

    private sealed class FakeSync : IDeviceSyncService
    {
        public long Free { get; init; } = 3_000_000;
        public bool Hold { get; init; }
        public string[] Failures { get; init; } = [];
        public List<DeviceSyncPlan> Planned { get; } = [];
        public List<int> PlannedTrackCounts { get; } = [];
        public List<DeviceSyncPlan> Executed { get; } = [];
        public bool Started { get; private set; }
        public bool Cancelled { get; private set; }

        public DeviceSyncPlan Plan(RemovableVolume volume, IReadOnlyList<Track> tracks, DeviceSyncOptions options)
        {
            PlannedTrackCounts.Add(tracks.Count);
            DeviceSyncPlan plan = new(volume with { FreeBytes = Free }, "/media/u/STICK/Music", options,
            [
                new SyncItem(One, "/music/a.mp3", "Music/Band/Album/01 - One.mp3", 1000, SyncAction.Copy),
                new SyncItem(Two, "/music/b.mp3", "Music/Band/Album/02 - Two.mp3", 2000, SyncAction.Copy),
                new SyncItem(One, "/music/c.mp3", "Music/Band/Album/03 - Three.mp3", 500, SyncAction.UpToDate)
            ], []);
            Planned.Add(plan);
            return plan;
        }

        public async Task<DeviceSyncResult> ExecuteAsync(DeviceSyncPlan plan, IProgress<DeviceSyncProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Started = true;
            Executed.Add(plan);
            progress?.Report(new DeviceSyncProgress(0, 2, 0, 3000, "Music/Band/Album/01 - One.mp3"));
            if (Hold)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return new DeviceSyncResult(2, 0, 1, 0, Failures, "/media/u/STICK/Music/Aibell.m3u8", false);
        }
    }

    private static T[] Items<T>(params T[] items) => items;
}
