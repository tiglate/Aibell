using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Playback;
using Aibell.Desktop.Services;
using Aibell.Desktop.TestSupport;
using Aibell.Desktop.ViewModels;
using Aibell.Mpd.Client;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    private static MpdViewModel NewMpd(FakeMpdConnector connector, Action<Exception, string, string>? report = null) =>
        new(connector, null, null, null, [TimeSpan.Zero], TimeSpan.FromHours(1), TimeSpan.Zero, report);

    [AvaloniaTest]
    public async Task Mpd_SidebarEntryShowsTheServerInTheMusicViewWithoutConnecting()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-mpd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        FakeMpdConnector connector = new(new FakeMpdClient());
        Open(mpdViewModelFactory: () => NewMpd(connector));
        await UntilAsync(() => !viewModel.IsBusy);
        Button mpd = window.FindControl<Button>("OpenMpdButton")!, music = window.FindControl<Button>("SelectMusicButton")!;
        Assert.That(mpd.Tag, Is.Not.Null, "the MPD entry carries its vector icon like the others");
        Assert.That(window.FindControl<MpdPanel>("MpdPane")!.IsEffectivelyVisible, Is.False, "the MPD group only exists while MPD is selected");

        Activate(mpd);
        await UntilAsync(() => viewModel.IsMpdView && !viewModel.IsBusy);
        Assert.That(mpd.Classes.Contains("selected"), Is.True);
        Assert.That(music.Classes.Contains("selected"), Is.False);
        Assert.That(viewModel.IsColumnBrowserVisible, Is.True, "the Music view's genre, artist and album lists are the ones showing the server");
        Assert.That(window.FindControl<MpdPanel>("MpdPane")!.IsEffectivelyVisible, Is.True);
        Assert.That(connector.LastEndpoint, Is.Null, "selecting MPD must not contact any server");
        Assert.That(viewModel.Tracks, Is.Empty);

        Activate(music);
        await UntilAsync(() => !viewModel.IsMpdView);
        Assert.That(mpd.Classes.Contains("selected"), Is.False);
        Assert.That(music.Classes.Contains("selected"), Is.True);
        Assert.That(window.FindControl<MpdPanel>("MpdPane")!.IsEffectivelyVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task Mpd_ConnectFillsTheMusicViewAndTheSidebarPlaylists()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-mpd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        FakeMpdClient client = new();
        FakeMpdConnector connector = new(client);
        MpdViewModel? model = null;
        Open(mpdViewModelFactory: () => model = NewMpd(connector));
        await UntilAsync(() => !viewModel.IsBusy);
        Activate(window.FindControl<Button>("OpenMpdButton")!);
        await UntilAsync(() => viewModel.IsMpdView && model is not null);
        MpdPanel panel = window.FindControl<MpdPanel>("MpdPane")!;
        Button connect = panel.GetVisualDescendants().OfType<Button>().First(button => button.Name == "MpdConnectButton");
        Assert.That(connect.IsEnabled, Is.True);
        Assert.That(panel.GetVisualDescendants().OfType<TextBlock>().First(block => block.Name == "MpdStatus").Text, Does.Contain("Not connected"));

        Activate(connect);
        await UntilAsync(() => model!.IsConnected && viewModel.Tracks.Count == 4);
        Dispatcher.UIThread.RunJobs();
        Assert.That(viewModel.Genres.Select(item => item.Name), Is.EqualTo(new string?[] { null, "", "Jazz", "Rock" }), "All, no genre, then by name");
        Assert.That(viewModel.Artists.Select(item => item.Name), Does.Contain("Band A").And.Contain("Band B"));
        Assert.That(viewModel.Albums.Select(item => item.Title), Does.Contain("First").And.Contain("Second"));
        Assert.That(viewModel.Tracks.Select(row => row.Source.Title), Is.EqualTo(new[] { "Loose", "One", "Two", "Blue" }));
        Assert.That(panel.GetVisualDescendants().OfType<Button>().First(button => button.Name == "MpdDisconnectButton").IsEnabled, Is.True);
        Assert.That(panel.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Songs: 4 · Albums: 2 · Artists: 3"), Is.True);

        // Choosing a genre narrows the track list exactly like in the local library.
        await viewModel.SelectGenreAsync(viewModel.Genres.First(item => item.Name == "Jazz"));
        Assert.That(viewModel.Tracks.Select(row => row.Source.Title), Is.EqualTo(new[] { "Blue" }));
        await viewModel.SelectGenreAsync(viewModel.Genres[0]);

        // The server's playlists replace the local ones in the Playlists section.
        ListBox sidebar = window.FindControl<ListBox>("MpdPlaylistList")!;
        Assert.That(sidebar.IsEffectivelyVisible, Is.True);
        Assert.That(window.FindControl<ListBox>("PlaylistList")!.IsEffectivelyVisible, Is.False);
        Assert.That(sidebar.Items.Cast<PlaylistFilterViewModel>().Select(item => item.Name), Is.EqualTo(new[] { "alpha", "Zed" }));
        sidebar.SelectedIndex = 0;
        await UntilAsync(() => viewModel.SelectedMpdPlaylistName == "alpha" && viewModel.Tracks.Count == 1);
        Assert.That(viewModel.Tracks.Single().Source.Title, Is.EqualTo("In list"));
        Assert.That(viewModel.IsColumnBrowserVisible, Is.False, "a playlist shows just its songs, like a local playlist");

        // Songs can be taken out of a server playlist (the menu item shows only inside a playlist, and not while searching).
        viewModel.SelectedTrack = viewModel.Tracks.Single();
        Assert.That(viewModel.CanRemoveFromPlaylist, Is.True);
        Assert.That(viewModel.Tracks.Single().IsPlaylistTrack, Is.True);
        client.Calls.Clear();
        await viewModel.RemoveSelectedTrackFromPlaylistAsync();
        Assert.That(client.Calls, Does.Contain("playlistdelete alpha 0"));
        Assert.That(viewModel.StatusMessage, Is.EqualTo("Removed In list from alpha."));

        Activate(window.FindControl<Button>("OpenMpdButton")!);
        await UntilAsync(() => viewModel.SelectedMpdPlaylistName is null && viewModel.Tracks.Count == 4);
        Assert.That(viewModel.CanRemoveFromPlaylist, Is.False, "the whole library is not a playlist");
        Assert.That(viewModel.IsColumnBrowserVisible, Is.True);

        // Leaving MPD brings the local playlists back and the local library with them.
        Activate(window.FindControl<Button>("SelectMusicButton")!);
        await UntilAsync(() => !viewModel.IsMpdView && !viewModel.Tracks.Any(row => row.Source.Source == "MPD"));
        Assert.That(window.FindControl<ListBox>("PlaylistList")!.IsEffectivelyVisible, Is.True);
        Assert.That(sidebar.IsEffectivelyVisible, Is.False);

        await model!.DisconnectAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.That(sidebar.ItemCount, Is.Zero);
    }

    [AvaloniaTest]
    public async Task Mpd_PlayingATrackFromTheMusicViewPlaysOnTheServerThroughTheSharedTransport()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-mpd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        FakeMpdClient client = new();
        FakeMpdConnector connector = new(client);
        MpdPlaybackEngine mpdEngine = new();
        MpdViewModel? model = null;
        Open(mpdViewModelFactory: () =>
            {
                model = NewMpd(connector);
                mpdEngine.Attach(model);
                return model;
            },
            engineWrapper: local => new MpdPlaybackRouter(local, mpdEngine));
        await UntilAsync(() => !viewModel.IsBusy);
        Activate(window.FindControl<Button>("OpenMpdButton")!);
        await UntilAsync(() => model is not null);
        await model!.ConnectAsync();
        await UntilAsync(() => viewModel.Tracks.Count == 4);

        viewModel.SelectedTrack = viewModel.Tracks.First(row => row.Source.Title == "One");
        client.Calls.Clear();
        await viewModel.TogglePlaybackAsync();
        await UntilAsync(() => client.Calls.Contains("playid 42"));
        Assert.That(client.Calls, Does.Contain("addid a/one.flac"), "the song was added to the server's queue behind anything already there");
        Assert.That(engine.Calls, Does.Not.Contain("Load"), "the local player is not used for a server song");
        await UntilAsync(() => viewModel.NowPlaying.Contains("One", StringComparison.Ordinal));
    }

    [AvaloniaTest]
    public async Task Mpd_WhileSelectedTheVolumeBarIsTheServersAndTheLocalVolumeIsLeftAlone()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-mpd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        FakeMpdClient client = new();
        FakeMpdConnector connector = new(client);
        MpdPlaybackEngine mpdEngine = new();
        MpdViewModel? model = null;
        Open(mpdViewModelFactory: () =>
            {
                model = NewMpd(connector);
                mpdEngine.Attach(model);
                return model;
            },
            engineWrapper: local => new MpdPlaybackRouter(local, mpdEngine));
        await UntilAsync(() => !viewModel.IsBusy);
        double local = viewModel.Volume;
        Assert.That(local, Is.EqualTo(0.8), "the local player's volume");

        Activate(window.FindControl<Button>("OpenMpdButton")!);
        await UntilAsync(() => model is not null);
        await model!.ConnectAsync();
        await UntilAsync(() => viewModel.Tracks.Count == 4);
        await UntilAsync(() => Math.Abs(viewModel.Volume - 0.5) < 0.001);
        Assert.That(viewModel.Volume, Is.EqualTo(0.5), "with MPD selected the volume bar shows the server's volume (50)");

        client.Calls.Clear();
        await viewModel.ChangeVolumeAsync(0.3);
        Assert.That(client.Calls, Does.Contain("vol 30"), "and moving it sets the server's volume");
        Assert.That(engine.Calls, Has.None.EqualTo("Volume"), "the local player's volume is not touched");
        Assert.That(viewModel.GetPreferences().Volume, Is.EqualTo(local), "nor is the volume that is saved for the local player");

        await viewModel.ToggleMuteAsync();
        Assert.That(client.Calls, Does.Contain("vol 0"));
        Assert.That(viewModel.GetPreferences().Muted, Is.False, "muting the server is not saved as the local mute");
        await viewModel.ToggleMuteAsync();
        Assert.That(client.Calls.Last(call => call.StartsWith("vol", StringComparison.Ordinal)), Is.EqualTo("vol 30"));

        // Selecting Music again gives the bar back to the local player.
        Activate(window.FindControl<Button>("SelectMusicButton")!);
        await UntilAsync(() => !viewModel.IsMpdView && Math.Abs(viewModel.Volume - local) < 0.001);
        Assert.That(viewModel.Volume, Is.EqualTo(local));
        client.Calls.Clear();
        await viewModel.ChangeVolumeAsync(0.6);
        Assert.That(client.Calls, Has.None.StartsWith("vol"), "the server is left alone");
        Assert.That(viewModel.GetPreferences().Volume, Is.EqualTo(0.6));
    }

    [AvaloniaTest]
    public async Task Mpd_ProblemsOpenTheErrorWindowWithAFriendlyMessageAndTechnicalDetails()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-mpd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        FakeMpdClient client = new();
        FakeMpdConnector connector = new(client);
        MpdViewModel? model = null;
        Open(mpdViewModelFactory: () => model = NewMpd(connector, (exception, friendly, context) =>
            reporter!.Report(exception, "mpd", context, friendly)));
        reporter = new Aibell.Desktop.Services.ApplicationErrorReporter(Path.Combine(directory, "logs"));
        viewModel.ConfigureErrorReporting(reporter, null);
        List<ErrorReport> reports = [];
        viewModel.ErrorReportAvailable += reports.Add;
        await UntilAsync(() => !viewModel.IsBusy);
        Activate(window.FindControl<Button>("OpenMpdButton")!);
        await UntilAsync(() => model is not null);

        connector.ConnectFailure = new IOException("Connection refused");
        await model!.ConnectAsync();
        await UntilAsync(() => reports.Count == 1);
        Assert.That(reports[0].Message, Does.StartWith("Aibell could not reach the MPD server at "));
        Assert.That(reports[0].Details, Does.Contain("IOException").And.Contain("Connection refused").And.Contain("MPD server:"));
        Assert.That(reports[0].Source, Is.EqualTo("mpd"));

        connector.ConnectFailure = null;
        client.FailWhen = call => call.StartsWith("find", StringComparison.Ordinal) ? new MpdProtocolException(52, 0, "find", "Unsupported filter") : null;
        await model.ConnectAsync();
        await UntilAsync(() => reports.Count == 2);
        Assert.That(reports[1].Message, Is.EqualTo("Aibell could not read the music library from the MPD server."));
        Assert.That(reports[1].Details, Does.Contain("MpdProtocolException").And.Contain("Unsupported filter"));
        Assert.That(viewModel.StatusMessage, Is.EqualTo("Aibell could not read the music library from the MPD server."));
    }

    private Aibell.Desktop.Services.ApplicationErrorReporter? reporter;

    private static async Task UntilAsync(Func<bool> condition, int seconds)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10, deadline.Token);
        }
    }

    /// <summary>
    /// The whole path on the real server (needs <c>AIBELL_MPD_REAL_TESTS=1</c>): select MPD, connect, see the library in the Music view,
    /// play a song through the shared transport, and leave the server as it was found.
    /// </summary>
    [AvaloniaTest]
    public async Task Mpd_RealServer_ShowsTheLibraryAndPlaysThroughTheSharedTransport()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_MPD_REAL_TESTS") != "1") Assert.Ignore("Set AIBELL_MPD_REAL_TESTS=1 to use the real MPD server.");
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-mpd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        MpdConnector real = new();
        MpdEndpoint endpoint = real.Locate(MpdConnectionSettings.Default, null);
        await using IMpdClient probe = await real.ConnectAsync(endpoint, CancellationToken.None);
        MpdStatus before = await probe.GetStatusAsync();
        int queueBefore = (await probe.GetQueueAsync()).Count;
        int songs = (await probe.GetStatsAsync()).Songs;

        MpdPlaybackEngine mpdEngine = new();
        MpdViewModel? model = null;
        Open(mpdViewModelFactory: () =>
            {
                model = new MpdViewModel(real, MpdConnectionSettings.Default);
                mpdEngine.Attach(model);
                return model;
            },
            engineWrapper: local => new MpdPlaybackRouter(local, mpdEngine));
        try
        {
            await UntilAsync(() => !viewModel.IsBusy);
            Activate(window.FindControl<Button>("OpenMpdButton")!);
            await UntilAsync(() => model is not null);
            await model!.ConnectAsync();
            Assert.That(model.IsConnected, Is.True, model.StatusMessage);
            await UntilAsync(() => viewModel.Tracks.Count == songs && songs > 0, 20);
            Assert.That(viewModel.Genres.Count, Is.GreaterThan(1));
            Assert.That(viewModel.Tracks.All(row => MpdTracks.TryGetSongUri(row.Source.Location, out _)), Is.True);

            viewModel.SelectedTrack = viewModel.Tracks.First(row => row.Source.Duration > TimeSpan.FromSeconds(30));
            await viewModel.TogglePlaybackAsync();
            await UntilAsync(() => mpdEngine.Snapshot.Status == PlaybackStatus.Playing, 15);
            await UntilAsync(() => viewModel.IsPlaybackPlaying, 15);
            await UntilAsync(() => mpdEngine.Snapshot.Position > TimeSpan.FromSeconds(1), 15);
            Assert.That(engine.Calls, Does.Not.Contain("Load"), "nothing played on the local player");
            await viewModel.StopPlaybackAsync();
            await UntilAsync(() => mpdEngine.Snapshot.Status == PlaybackStatus.Stopped, 15);
        }
        finally
        {
            await mpdEngine.ReleaseAsync();
            if (model is not null) await model.ShutdownAsync();
        }
        await using IMpdClient after = await real.ConnectAsync(endpoint, CancellationToken.None);
        MpdStatus finished = await after.GetStatusAsync();
        Assert.That((await after.GetQueueAsync()).Count, Is.EqualTo(queueBefore), "the user's queue is as it was");
        Assert.That(finished.SingleMode, Is.EqualTo(before.SingleMode));
    }
}
