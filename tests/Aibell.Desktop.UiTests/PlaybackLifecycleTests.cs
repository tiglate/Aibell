using Avalonia.Automation.Peers;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Aibell.Application.Library;
using Aibell.Application.Playback;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Settings;
using Aibell.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class PlaybackLifecycleTests
{
    private string directory = null!;
    private SqliteTrackRepository repository = null!;
    private BlockingPlaybackRepository tracks = null!;
    private JsonApplicationSettingsStore settings = null!;
    private LibraryMonitoringService monitoring = null!;
    private EventPlaybackEngine engine = null!;
    private MainWindowViewModel model = null!;
    private MainWindow window = null!;
    private Track first = null!, second = null!, third = null!;

    private async Task OpenAsync()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-playback-lifecycle-{Guid.NewGuid():N}");
        repository = new(Path.Combine(directory, "library.db"));
        settings = new(Path.Combine(directory, "settings.json"));
        first = Track("First"); second = Track("Second"); third = Track("Third");
        foreach (Track track in new[] { first, second, third }) await repository.UpsertAsync(track, directory);
        tracks = new(repository);
        LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), tracks);
        monitoring = new(repository, importer, NullLogger<LibraryMonitoringService>.Instance);
        engine = new();
        model = new(importer, tracks, repository, repository, repository, settings,
            monitoring, engine);
        window = new(model); window.Show();
        await UntilAsync(() => !model.IsBusy && model.Tracks.Count == 3);
        model.SelectedTrack = model.Tracks.Single(row => row.Source.Id == first.Id);
    }

    [AvaloniaTest]
    public async Task NaturalEnd_DuringStartStatistics_AdvancesOnceAfterTheWriteCompletes()
    {
        await OpenAsync(); tracks.BlockStarts = true;
        Task start = model.TogglePlaybackAsync();
        await UntilAsync(() => tracks.IsBlocked);
        engine.End(); Dispatcher.UIThread.RunJobs();
        tracks.Release.TrySetResult(); await start;
        await UntilAsync(() => engine.Snapshot.Track?.Id == second.Id);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id, second.Id }));
            Assert.That(model.IsPlaybackPlaying, Is.True);
        });
        await UntilAsync(() => model.Tracks.Single(row => row.Source.Id == second.Id).Source.PlayCount == 1);
        Assert.That((await repository.ListAsync()).Where(track => track.Id != third.Id).Select(track => track.PlayCount), Is.All.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task NaturalEnd_StaleCallbackAfterManualChange_DoesNotSkipTheNewTrack()
    {
        await OpenAsync(); await model.TogglePlaybackAsync();
        PlaybackSnapshot oldEnd = engine.Snapshot with { Status = PlaybackStatus.Ended, Position = first.Duration };
        model.SelectedTrack = model.Tracks.Single(row => row.Source.Id == second.Id);
        await model.TogglePlaybackAsync();
        engine.Notify(oldEnd); await PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(second.Id));
            Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id, second.Id }));
            Assert.That(model.IsPlaybackPlaying, Is.True);
            Assert.That(model.NowPlaying, Does.StartWith("Second"));
        });
    }

    [AvaloniaTest]
    public async Task Close_DuringAutomaticAdvance_DrainsStartStatisticsBeforeClosing()
    {
        await OpenAsync(); await model.TogglePlaybackAsync();
        tracks.BlockStarts = true; engine.End();
        await UntilAsync(() => tracks.IsBlocked);
        window.Close(); Dispatcher.UIThread.RunJobs();
        try { Assert.That(window.IsVisible, Is.True, "The catalog write must drain before the window closes."); }
        finally { tracks.Release.TrySetResult(); }
        await UntilAsync(() => !window.IsVisible);
        Assert.That((await repository.ListAsync()).Single(track => track.Id == second.Id).PlayCount, Is.EqualTo(1));
        Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id, second.Id }));
    }

    [AvaloniaTest]
    public async Task Close_DuringManualStart_DrainsStatisticsAndRejectsNewPlaybackCommands()
    {
        await OpenAsync(); tracks.BlockStarts = true;
        Task start = model.TogglePlaybackAsync();
        await UntilAsync(() => tracks.IsBlocked);
        window.Close(); Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.That(window.IsVisible, Is.True);
            model.SelectedTrack = model.Tracks.Single(row => row.Source.Id == third.Id);
            await model.TogglePlaybackAsync(); await model.MoveNextAsync();
            await model.ExecuteMediaSessionCommandAsync(new(MediaSessionAction.PlayPause));
            Assert.That(model.GetMediaSessionState().CanPlay, Is.False);
            Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id }));
        }
        finally { tracks.Release.TrySetResult(); }
        await start; await UntilAsync(() => !window.IsVisible);
        Assert.That((await repository.ListAsync()).Single(track => track.Id == first.Id).PlayCount, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task NaturalEnd_DeferredDuringStatisticsThenExplicitStop_DoesNotResumePlayback()
    {
        await OpenAsync(); tracks.BlockStarts = true;
        Task start = model.TogglePlaybackAsync();
        await UntilAsync(() => tracks.IsBlocked);
        engine.End(); Dispatcher.UIThread.RunJobs();
        await model.StopPlaybackAsync();
        tracks.Release.TrySetResult(); await start; await PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id }));
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
            Assert.That(model.IsPlaybackPlaying, Is.False);
        });
    }

    [AvaloniaTest]
    public async Task NaturalEnd_DuplicateCallbacks_ConsumesOnlyOneQueuedRequest()
    {
        await OpenAsync(); await model.TogglePlaybackAsync();
        model.QueueTracks([third, first]);
        engine.End(); engine.Notify(engine.Snapshot);
        await UntilAsync(() => engine.Snapshot.Track?.Id == third.Id);
        await model.WaitForPlaybackOperationsAsync(); await PumpAsync();
        Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id, third.Id }));
        Assert.That(model.QueueItems, Has.Count.EqualTo(2));
        engine.End(); await UntilAsync(() => engine.Loads.Count == 3);
        await model.WaitForPlaybackOperationsAsync();
        Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id, third.Id, first.Id }));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Navigation_WhileSkipHistoryIsBlocked_DropsRepeatedOrOppositeRequests(bool previous)
    {
        await OpenAsync(); await model.TogglePlaybackAsync();
        tracks.BlockSkips = true;
        Task firstRequest = model.MoveNextAsync(); await UntilAsync(() => tracks.IsSkipBlocked);
        Task repeatedRequest = previous ? model.MovePreviousAsync() : model.MoveNextAsync();
        try { Assert.That(tracks.SkipAttempts, Is.EqualTo(1), "Navigation must remain exclusive while history is saved."); }
        finally { tracks.ReleaseSkips.TrySetResult(); }
        await Task.WhenAll(firstRequest, repeatedRequest); await model.WaitForPlaybackOperationsAsync();
        Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id, second.Id }));
        Assert.That((await repository.ListAsync()).Single(track => track.Id == first.Id).SkipCount, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Navigation_AtQueueBoundaries_DoesNotRecordASkipWithoutChangingTracks()
    {
        await OpenAsync(); await model.TogglePlaybackAsync();
        await model.MovePreviousAsync();
        Assert.That(tracks.SkipAttempts, Is.Zero);
        await model.MoveNextAsync(); await model.MoveNextAsync();
        Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(third.Id));
        int skips = tracks.SkipAttempts;
        await model.MoveNextAsync();
        Assert.That(tracks.SkipAttempts, Is.EqualTo(skips));
        Assert.That((await repository.ListAsync()).Single(track => track.Id == third.Id).SkipCount, Is.Zero);
    }

    [AvaloniaTest]
    public async Task Navigation_StopDuringPendingHistory_PreventsTheDeferredNextTrackFromStarting()
    {
        await OpenAsync(); await model.TogglePlaybackAsync();
        tracks.BlockSkips = true;
        Task next = model.MoveNextAsync(); await UntilAsync(() => tracks.IsSkipBlocked);
        await model.StopPlaybackAsync(); tracks.ReleaseSkips.TrySetResult(); await next; await PumpAsync();
        Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id }));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [AvaloniaTest]
    public async Task Navigation_CloseDuringPendingHistory_DrainsTheWriteWithoutStartingAnotherTrack()
    {
        await OpenAsync(); await model.TogglePlaybackAsync();
        tracks.BlockSkips = true;
        Task next = model.MoveNextAsync(); await UntilAsync(() => tracks.IsSkipBlocked);
        window.Close(); Dispatcher.UIThread.RunJobs();
        try { Assert.That(window.IsVisible, Is.True); }
        finally { tracks.ReleaseSkips.TrySetResult(); }
        await next; await UntilAsync(() => !window.IsVisible);
        Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id }));
        Assert.That((await repository.ListAsync()).Single(track => track.Id == first.Id).SkipCount, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Navigation_CancelledHistory_ReleasesTheTransitionForTheNextRequest()
    {
        await OpenAsync(); await model.TogglePlaybackAsync();
        tracks.BlockSkips = true;
        using CancellationTokenSource cancellation = new();
        Task cancelled = model.MoveNextAsync(cancellation.Token); await UntilAsync(() => tracks.IsSkipBlocked);
        cancellation.Cancel();
        Exception? failure = null;
        try { await cancelled; } catch (Exception exception) { failure = exception; }
        Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
        Assert.That((await repository.ListAsync()).Single(track => track.Id == first.Id).SkipCount, Is.Zero);
        Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id }));
        tracks.ReleaseSkips.TrySetResult(); await model.MoveNextAsync();
        Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id, second.Id }));
        Assert.That((await repository.ListAsync()).Single(track => track.Id == first.Id).SkipCount, Is.EqualTo(1));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task NextButton_FailedOrCancelledHistory_StaysUsableWithoutUnhandledDispatcherErrors(bool cancel)
    {
        await OpenAsync(); await model.TogglePlaybackAsync();
        tracks.BlockSkips = true;
        tracks.SkipFailure = cancel ? new OperationCanceledException() : new IOException("Synthetic history failure");
        List<Exception> unhandled = [];
        void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs args)
        { unhandled.Add(args.Exception); args.Handled = true; }
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            Button next = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(Avalonia.Automation.AutomationProperties.GetName(button), "Next track"));
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => tracks.IsSkipBlocked);
            tracks.ReleaseSkips.TrySetResult(); await model.WaitForPlaybackOperationsAsync(); await PumpAsync();
            Assert.That(unhandled, Is.Empty);
            Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id }));
            Assert.That((await repository.ListAsync()).Single(track => track.Id == first.Id).SkipCount, Is.Zero);
            if (!cancel) Assert.That(model.StatusMessage, Does.Contain("Synthetic history failure"));
            tracks.BlockSkips = false; tracks.SkipFailure = null;
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => engine.Snapshot.Track?.Id == second.Id);
            Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id, second.Id }));
        }
        finally { tracks.ReleaseSkips.TrySetResult(); Dispatcher.UIThread.UnhandledException -= OnUnhandled; }
    }

    [AvaloniaTest]
    public async Task NextButton_CallbackAfterClose_DoesNotReadDisposedLifetimeOrStartPlayback()
    {
        await OpenAsync(); await model.TogglePlaybackAsync();
        Button next = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(Avalonia.Automation.AutomationProperties.GetName(button), "Next track"));
        window.Close(); await UntilAsync(() => !window.IsVisible);
        List<Exception> unhandled = [];
        void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs args)
        { unhandled.Add(args.Exception); args.Handled = true; }
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await PumpAsync();
            Assert.That(unhandled, Is.Empty);
            Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id }));
        }
        finally { Dispatcher.UIThread.UnhandledException -= OnUnhandled; }
    }

    [AvaloniaTest]
    public async Task Keyboard_FindAndEscape_FocusSelectAndClearLibrarySearch()
    {
        await OpenAsync();
        TextBox search = window.FindControl<TextBox>("SearchBox")!;
        search.Text = "First"; await UntilAsync(() => model.Tracks.Count == 1);
        FocusTrackRow();
        PressShortcut(PhysicalKey.F, RawInputModifiers.Control);
        Assert.That(search.IsFocused, Is.True);
        Assert.That(search.SelectedText, Is.EqualTo("First"));
        PressShortcut(PhysicalKey.Escape, RawInputModifiers.None);
        await UntilAsync(() => model.SearchQuery.Length == 0 && model.Tracks.Count == 3);
        Assert.That(search.IsFocused, Is.True);
    }

    [AvaloniaTest]
    public async Task Keyboard_SpaceInTrackListTogglesPlayback_ButPreservesTextAndButtonInput()
    {
        await OpenAsync(); FocusTrackRow();
        PressShortcut(PhysicalKey.Space, RawInputModifiers.None);
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing);
        await model.WaitForPlaybackOperationsAsync(); Dispatcher.UIThread.RunJobs();
        Assert.That(window.FindControl<ListBox>("TrackList")!.IsKeyboardFocusWithin, Is.True,
            $"Focus after start: {window.FocusManager?.GetFocusedElement()}");
        PressShortcut(PhysicalKey.Space, RawInputModifiers.None);
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Paused);
        TextBox search = window.FindControl<TextBox>("SearchBox")!; search.Focus();
        PressShortcut(PhysicalKey.Space, RawInputModifiers.None); await PumpAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Paused));
        Button play = window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("play"));
        play.Focus(); PressShortcut(PhysicalKey.Space, RawInputModifiers.None);
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing);
        Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id }));
    }

    [AvaloniaTest]
    public async Task Keyboard_AltArrowsNavigateOnce_AndDoNotInterruptTextEditing()
    {
        await OpenAsync(); await model.TogglePlaybackAsync(); FocusTrackRow();
        PressShortcut(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        await UntilAsync(() => engine.Snapshot.Track?.Id == second.Id);
        PressShortcut(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        await UntilAsync(() => engine.Loads.Count == 3);
        window.FindControl<TextBox>("SearchBox")!.Focus();
        PressShortcut(PhysicalKey.ArrowRight, RawInputModifiers.Alt); await PumpAsync();
        Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id, second.Id, first.Id }));
        Assert.That((await repository.ListAsync()).Where(track => track.Id != third.Id).Select(track => track.SkipCount), Is.All.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Keyboard_QuitDuringPendingNavigation_DrainsHistoryWithoutStartingAnotherTrack()
    {
        await OpenAsync(); await model.TogglePlaybackAsync(); tracks.BlockSkips = true;
        FocusTrackRow();
        PressShortcut(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        await UntilAsync(() => tracks.IsSkipBlocked);
        PressShortcut(PhysicalKey.Q, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
        try { Assert.That(window.IsVisible, Is.True); }
        finally { tracks.ReleaseSkips.TrySetResult(); }
        await UntilAsync(() => !window.IsVisible);
        Assert.That(engine.Loads, Is.EqualTo(new[] { first.Id }));
        Assert.That((await repository.ListAsync()).Single(track => track.Id == first.Id).SkipCount, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Keyboard_DelayedPlaybackRefresh_DoesNotStealFocusChosenWhileWaiting()
    {
        await OpenAsync(); tracks.BlockStarts = true; FocusTrackRow();
        PressShortcut(PhysicalKey.Space, RawInputModifiers.None);
        await UntilAsync(() => tracks.IsBlocked);
        TextBox search = window.FindControl<TextBox>("SearchBox")!; search.Focus();
        tracks.Release.TrySetResult(); await model.WaitForPlaybackOperationsAsync(); await PumpAsync();
        Assert.That(search.IsFocused, Is.True);
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        PressShortcut(PhysicalKey.Space, RawInputModifiers.None); await PumpAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
    }

    [AvaloniaTest]
    public async Task Automation_TransportNames_ExposeTheCurrentActionAndLabelLibraryRegions()
    {
        await OpenAsync();
        Button play = window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("play"));
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(play)!;
        Assert.That(peer.GetName(), Is.EqualTo("Play"));
        await model.TogglePlaybackAsync(); Dispatcher.UIThread.RunJobs();
        Assert.That(peer.GetName(), Is.EqualTo("Pause"));
        await model.TogglePlaybackAsync(); Dispatcher.UIThread.RunJobs();
        Assert.That(peer.GetName(), Is.EqualTo("Play"));
        foreach ((string name, string label) in new[] { ("SearchBox", "Search library"), ("TrackList", "Library tracks"),
            ("GenreList", "Genres"), ("ArtistList", "Artists"), ("AlbumList", "Albums"), ("PlayQueueList", "Play Queue") })
            Assert.That(ControlAutomationPeer.CreatePeerForElement(window.FindControl<Control>(name)!)!.GetName(), Is.EqualTo(label));
    }

    [TearDown]
    public async Task TearDown()
    {
        tracks?.Release.TrySetResult(); tracks?.ReleaseSkips.TrySetResult();
        if (window is not null) { window.Close(); await UntilAsync(() => !window.IsVisible); }
        model?.Dispose(); monitoring?.Dispose(); settings?.Dispose();
        if (repository is not null) await repository.DisposeAsync();
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private Track Track(string title) => new(Guid.NewGuid(), new Uri(Path.Combine(directory, title + ".flac")),
        title, "Synthetic Artist", "Synthetic Album", TimeSpan.FromSeconds(1));

    private void PressShortcut(PhysicalKey key, RawInputModifiers modifiers)
    {
        window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers);
    }

    private void FocusTrackRow()
    {
        ListBox list = window.FindControl<ListBox>("TrackList")!;
        list.UpdateLayout();
        ListBoxItem row = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(item => ReferenceEquals(item.DataContext, model.SelectedTrack));
        Assert.That(row.Focus(), Is.True);
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

    private static async Task PumpAsync()
    {
        for (int index = 0; index < 10; index++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class EventPlaybackEngine : IPlaybackEngine
    {
        public PlaybackSnapshot Snapshot { get; private set; } = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8);
        public List<Guid> Loads { get; } = [];
        public event EventHandler<PlaybackSnapshot>? StateChanged;
        public void Notify(PlaybackSnapshot snapshot) => StateChanged?.Invoke(this, snapshot);
        private Task Publish(PlaybackSnapshot snapshot) { Snapshot = snapshot; Notify(snapshot); return Task.CompletedTask; }
        public void End() => Publish(Snapshot with { Status = PlaybackStatus.Ended, Position = Snapshot.Track!.Duration });
        public Task LoadAsync(Track track, CancellationToken cancellationToken = default)
        { Loads.Add(track.Id); return Publish(Snapshot with { Track = track, Status = PlaybackStatus.Stopped, Position = TimeSpan.Zero }); }
        public Task PlayAsync(CancellationToken cancellationToken = default) => Publish(Snapshot with { Status = PlaybackStatus.Playing });
        public Task PauseAsync(CancellationToken cancellationToken = default) => Publish(Snapshot with { Status = PlaybackStatus.Paused });
        public Task StopAsync(CancellationToken cancellationToken = default) => Publish(Snapshot with { Status = PlaybackStatus.Stopped, Position = TimeSpan.Zero });
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Publish(Snapshot with { Position = position });
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Publish(Snapshot with { Volume = volume });
        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) => Publish(Snapshot with { IsMuted = muted });
    }
}
