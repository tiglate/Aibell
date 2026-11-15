using Aibell.Desktop.TestSupport;
using Aibell.Desktop.ViewModels;
using Aibell.Mpd.Client;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.ViewModels;

[TestFixture]
public sealed class MpdViewModelTests
{
    private static readonly TimeSpan[] NoPause = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    private sealed record Reported(Exception Exception, string Friendly, string Context);

    private static (MpdViewModel Model, FakeMpdClient Client, FakeMpdConnector Connector, List<MpdConnectionSettings> Saved, List<Reported> Errors) Create(
        MpdConnectionSettings? settings = null, TimeSpan? keepAlive = null, TimeSpan? clock = null)
    {
        FakeMpdClient client = new();
        FakeMpdConnector connector = new(client);
        List<MpdConnectionSettings> saved = [];
        List<Reported> errors = [];
        MpdViewModel model = new(connector, settings, saved.Add, null, NoPause, keepAlive ?? TimeSpan.FromHours(1), clock ?? TimeSpan.Zero,
            (exception, friendly, context) => errors.Add(new Reported(exception, friendly, context)));
        return (model, client, connector, saved, errors);
    }

    [Test]
    public async Task Connect_LoadsPlaylistsOutputsStatsAndState()
    {
        (MpdViewModel model, FakeMpdClient client, FakeMpdConnector connector, _, List<Reported> errors) = Create();
        int libraryChanges = 0;
        model.LibraryChanged += (_, _) => libraryChanges++;
        Assert.That(model.StatusMessage, Is.EqualTo("Not connected to an MPD server."));
        Assert.That(model.EndpointText, Is.EqualTo("auto.local:6600"));
        Assert.That(model.CanConnect, Is.True);
        await model.ConnectAsync();
        await connector.Watching.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(model.IsConnected, Is.True);
        Assert.That(model.StatusMessage, Is.EqualTo("Connected to auto.local:6600 (protocol 0.23.0)."));
        Assert.That((model.State, model.CurrentSongId, model.ServerVolume), Is.EqualTo((MpdState.Playing, (int?)1, (int?)50)));
        Assert.That((model.Elapsed, model.Duration), Is.EqualTo((TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100))));
        Assert.That(model.Playlists.Select(row => row.Name), Is.EqualTo(new[] { "alpha", "Zed" }), "sorted by name without regard to case");
        Assert.That(model.Outputs.Single().Name, Is.EqualTo("Speakers"));
        Assert.That(model.Outputs.Single().Plugin, Is.EqualTo("alsa"));
        Assert.That(model.StatsText, Is.EqualTo("Songs: 4 · Albums: 2 · Artists: 3"));
        Assert.That(model.HasPlaylists && model.HasOutputs, Is.True);
        Assert.That(libraryChanges, Is.EqualTo(1), "the music view is told the library is ready");
        Assert.That(model.CanConnect, Is.False);
        Assert.That(model.CanDisconnect && model.CanControl, Is.True);
        await model.ConnectAsync();
        Assert.That(connector.ConnectCalls, Is.EqualTo(1), "already connected");
        Assert.That(errors, Is.Empty);
        Assert.That(client.Calls, Does.Contain("outputs"));
    }

    [Test]
    public async Task Connect_ReportsFailuresInTheStatusLineAndWithFriendlyDetails()
    {
        (MpdViewModel model, _, FakeMpdConnector connector, _, List<Reported> errors) = Create();
        connector.ConnectFailure = new IOException("refused");
        await model.ConnectAsync();
        Assert.That(model.IsConnected, Is.False);
        Assert.That(model.IsConnecting, Is.False);
        Assert.That(model.StatusMessage, Is.EqualTo("Could not connect to auto.local:6600: refused"));
        Reported report = errors.Single();
        Assert.That(report.Friendly, Is.EqualTo("Aibell could not reach the MPD server at auto.local:6600. Check that MPD is running and that the address and password are right."));
        Assert.That(report.Exception.Message, Is.EqualTo("refused"));
        Assert.That(report.Context, Does.Contain("MPD server: auto.local:6600").And.Contain("Protocol version: not connected"));
        await model.ConnectAsync();
        Assert.That(errors, Has.Count.EqualTo(1), "the same problem is not shown twice in a row");
        connector.ConnectFailure = new OperationCanceledException();
        await model.ConnectAsync();
        Assert.That(model.StatusMessage, Does.Contain("did not answer in time"));
        connector.ConnectFailure = new MpdProtocolException(4, 0, "password", "incorrect password");
        await model.ConnectAsync();
        Assert.That(model.StatusMessage, Does.Contain("incorrect password"));
        Assert.That(errors.Last().Context, Does.Contain("MPD error 4 in command 'password'"));
        Assert.That(model.CanConnect, Is.True);
    }

    [Test]
    public async Task APasswordIsNeverPartOfTheDetails()
    {
        (MpdViewModel model, _, FakeMpdConnector connector, _, List<Reported> errors) = Create();
        model.ApplyConnection(new MpdConnectionSettings(false, "music.lan", 6601), "hunter2");
        connector.ConnectFailure = new IOException("refused");
        await model.ConnectAsync();
        Assert.That(errors.Single().Context + errors.Single().Friendly + model.StatusMessage + model.EndpointText, Does.Not.Contain("hunter2"));
    }

    [Test]
    public async Task Disconnect_ClearsTheViewAndClosesTheClient()
    {
        (MpdViewModel model, FakeMpdClient client, _, _, _) = Create();
        await model.ConnectAsync();
        int libraryChanges = 0;
        model.LibraryChanged += (_, _) => libraryChanges++;
        await model.DisconnectAsync();
        Assert.That(model.IsConnected, Is.False);
        Assert.That(model.StatusMessage, Is.EqualTo("Not connected to an MPD server."));
        Assert.That(model.Playlists, Is.Empty);
        Assert.That(model.Outputs, Is.Empty);
        Assert.That(model.StatsText, Is.Empty);
        Assert.That((model.State, model.CurrentSongId, model.ServerVolume), Is.EqualTo((MpdState.Stopped, (int?)null, (int?)null)));
        Assert.That(libraryChanges, Is.EqualTo(1), "the music view is told the library is gone");
        Assert.That(client.Calls, Does.Contain("dispose"));
        client.Calls.Clear();
        await model.RefreshAsync();
        Assert.That(client.Calls, Is.Empty, "nothing is sent while disconnected");
    }

    [Test]
    public async Task TheLibraryAndPlaylistsAreServedToTheMusicView()
    {
        (MpdViewModel model, FakeMpdClient client, _, _, _) = Create();
        await Assert.ThatAsync(() => model.GetAllSongsAsync(CancellationToken.None), Throws.InvalidOperationException);
        await model.ConnectAsync();
        Assert.That((await model.GetAllSongsAsync(CancellationToken.None)).Select(song => song.Uri), Is.EqualTo(client.Library.Select(song => song.Uri)));
        Assert.That((await model.GetPlaylistSongsAsync("alpha", CancellationToken.None)).Single().DisplayTitle, Is.EqualTo("In list"));
    }

    [Test]
    public async Task Playlists_SelectRenameDeleteAndFill()
    {
        (MpdViewModel model, FakeMpdClient client, _, _, _) = Create();
        await model.ConnectAsync();
        int changes = 0;
        model.PlaylistsChanged += (_, _) => changes++;
        Assert.That(model.CanUsePlaylist, Is.False);
        model.SelectPlaylist("alpha");
        Assert.That(model.SelectedPlaylist?.Name, Is.EqualTo("alpha"));
        Assert.That(model.CanUsePlaylist, Is.True);
        model.SelectPlaylist("nope");
        Assert.That(model.SelectedPlaylist, Is.Null);
        model.SelectPlaylist(null);
        model.SelectPlaylist("alpha");

        await model.RenamePlaylistAsync(model.SelectedPlaylist, "beta");
        Assert.That(model.SelectedPlaylist?.Name, Is.EqualTo("beta"));
        await model.RenamePlaylistAsync(model.SelectedPlaylist, "  beta ");
        await model.RenamePlaylistAsync(model.SelectedPlaylist, "");
        await model.RenamePlaylistAsync(null, "x");
        Assert.That(client.Calls, Does.Contain("rename alpha beta"));
        Assert.That(changes, Is.GreaterThan(0));

        client.Calls.Clear();
        await model.AddToPlaylistAsync(" Mix ", ["a/one.flac", "b/blue.flac"]);
        Assert.That(client.Calls.Where(call => call.StartsWith("playlistadd", StringComparison.Ordinal)), Is.EqualTo(new[] { "playlistadd Mix a/one.flac", "playlistadd Mix b/blue.flac" }));
        Assert.That(model.StatusMessage, Is.EqualTo("Playlist “Mix”: songs added: 2."));
        // A song is never added twice: what the playlist already holds, and repeats within the request, are left out.
        client.Calls.Clear();
        await model.AddToPlaylistAsync("Zed", ["p.mp3", "new.mp3", "new.mp3"]);
        Assert.That(client.Calls.Where(call => call.StartsWith("playlistadd", StringComparison.Ordinal)), Is.EqualTo(new[] { "playlistadd Zed new.mp3" }));
        Assert.That(model.StatusMessage, Is.EqualTo("Playlist “Zed”: songs added: 1."));
        client.Calls.Clear();
        await model.AddToPlaylistAsync("Zed", ["p.mp3"]);
        Assert.That(client.Calls, Has.None.StartWith("playlistadd"));
        Assert.That(model.StatusMessage, Is.EqualTo("“Zed” already has all of these songs: nothing was added."));
        client.Calls.Clear();
        await model.AddToPlaylistAsync("   ", ["x"]);
        await model.AddToPlaylistAsync("Mix", []);
        Assert.That(client.Calls.Count(call => call.StartsWith("playlistadd", StringComparison.Ordinal)), Is.Zero);

        await model.DeletePlaylistAsync(model.SelectedPlaylist);
        await model.DeletePlaylistAsync(null);
        Assert.That(model.Playlists.Select(row => row.Name), Is.EqualTo(new[] { "Zed" }));
        Assert.That(model.SelectedPlaylist, Is.Null);
    }

    [Test]
    public async Task RemovingFromAPlaylist_TakesOutOneEntryOfThatSong()
    {
        (MpdViewModel model, FakeMpdClient client, _, _, _) = Create();
        await model.ConnectAsync();
        client.Calls.Clear();
        await model.RemoveFromPlaylistAsync("alpha", "p.mp3", CancellationToken.None);
        Assert.That(client.Calls.Where(call => call.StartsWith("playlistdelete", StringComparison.Ordinal)), Is.EqualTo(new[] { "playlistdelete alpha 0" }));
        client.Calls.Clear();
        await model.RemoveFromPlaylistAsync("alpha", "not-there.mp3", CancellationToken.None);
        Assert.That(client.Calls, Has.None.StartWith("playlistdelete"));
    }

    [Test]
    public async Task ClosingTheApplication_StopsTheMusicItStartedOnTheServer()
    {
        (MpdViewModel model, _, _, _, _) = Create();
        int released = 0;
        model.ReleasePlaybackAsync = _ => { released++; return Task.CompletedTask; };
        await model.ShutdownAsync();
        Assert.That(released, Is.Zero, "not connected: nothing to stop");

        (MpdViewModel connected, _, _, _, _) = Create();
        connected.ReleasePlaybackAsync = _ => { released++; return Task.CompletedTask; };
        await connected.ConnectAsync();
        await connected.ShutdownAsync();
        await connected.ShutdownAsync();
        Assert.That(released, Is.EqualTo(1), "released once, before the connection is closed");

        (MpdViewModel failing, _, _, _, _) = Create();
        failing.ReleasePlaybackAsync = _ => throw new InvalidOperationException("gone");
        await failing.ConnectAsync();
        await failing.ShutdownAsync();
        Assert.That(failing.IsConnected, Is.False, "a server that cannot be reached must not keep the application from closing");
    }

    [Test]
    public async Task Outputs_CanBeToggledAndTheDatabaseUpdated()
    {
        (MpdViewModel model, FakeMpdClient client, _, _, _) = Create();
        await model.ConnectAsync();
        await model.SetOutputEnabledAsync(model.Outputs[0], false);
        await model.SetOutputEnabledAsync(null, false);
        Assert.That(model.Outputs[0].IsEnabled, Is.False);
        Assert.That(client.Calls, Does.Contain("output 0 False"));
        await model.UpdateDatabaseAsync();
        Assert.That(model.IsUpdatingDatabase, Is.True);
        Assert.That(model.StatusMessage, Is.EqualTo("The server is updating its database…"));
        Assert.That(client.Calls, Does.Contain("update"));
    }

    [Test]
    public async Task Changes_FromTheServerRefreshTheMatchingViews()
    {
        (MpdViewModel model, FakeMpdClient client, FakeMpdConnector connector, _, _) = Create();
        await model.ConnectAsync();
        await connector.Watching.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int libraryChanges = 0;
        model.LibraryChanged += (_, _) => libraryChanges++;
        client.Lists = [new MpdStoredPlaylist("New", null)];
        client.OutputList = [new MpdOutput(1, "Other", false, null)];
        client.Status = client.Status with { Volume = 20, State = MpdState.Paused };
        await connector.OnChange!(["stored_playlist", "output", "mixer", "player"]);
        Assert.That(model.Playlists.Single().Name, Is.EqualTo("New"));
        Assert.That(model.Outputs.Single().Name, Is.EqualTo("Other"));
        Assert.That((model.ServerVolume, model.State), Is.EqualTo(((int?)20, MpdState.Paused)));
        Assert.That(libraryChanges, Is.Zero, "the music is unchanged");
        await connector.OnChange!(["database"]);
        Assert.That(libraryChanges, Is.EqualTo(1), "the database changed, so the music view must fetch it again");
        await connector.OnChange!(["unknown"]);
    }

    [Test]
    public async Task ARefusalFromMpd_IsShownWithFriendlyDetailsAndTheConnectionStays()
    {
        (MpdViewModel model, FakeMpdClient client, _, _, List<Reported> errors) = Create();
        await model.ConnectAsync();
        client.Fail = new MpdProtocolException(50, 0, "update", "No such directory");
        await model.UpdateDatabaseAsync();
        Assert.That(model.StatusMessage, Is.EqualTo("MPD refused the request: No such directory"));
        Assert.That(model.IsConnected, Is.True);
        Assert.That(errors.Single().Friendly, Is.EqualTo("The MPD server could not do that: No such directory"));
        Assert.That(errors.Single().Context, Does.Contain("MPD error 50 in command 'update'").And.Contain("Protocol version: 0.23.0"));
        Assert.That(errors.Single().Exception, Is.InstanceOf<MpdProtocolException>());
    }

    [Test]
    public async Task ABrokenConnection_IsReplacedAndTheActionRunsAgain()
    {
        (MpdViewModel model, FakeMpdClient client, FakeMpdConnector connector, _, List<Reported> errors) = Create();
        await model.ConnectAsync();
        connector.OnConnect = () => client.Fail = null;
        client.Fail = new IOException("Broken pipe");
        await model.UpdateDatabaseAsync();
        Assert.That(connector.ConnectCalls, Is.EqualTo(2));
        Assert.That(model.IsConnected, Is.True);
        Assert.That(model.IsReconnecting, Is.False);
        Assert.That(model.StatusMessage, Is.EqualTo("The server is updating its database…"), "the action that failed ran again on the new connection");
        Assert.That(client.Calls.Count(call => call == "update"), Is.EqualTo(2));
        Assert.That(errors, Is.Empty, "a connection that was repaired is nothing to bother the user with");
    }

    [Test]
    public async Task ReconnectingGivesUpAfterAFewAttemptsAndExplainsWhy()
    {
        (MpdViewModel model, FakeMpdClient client, FakeMpdConnector connector, _, List<Reported> errors) = Create();
        await model.ConnectAsync();
        connector.FailConnectsRemaining = 100;
        client.Fail = new IOException("Broken pipe");
        await model.UpdateDatabaseAsync();
        Assert.That(connector.ConnectCalls, Is.EqualTo(1 + 3), "the first connection and three attempts, not more");
        Assert.That(model.IsConnected, Is.False);
        Assert.That(model.StatusMessage, Does.StartWith("The connection to MPD was lost"));
        Assert.That(model.CanConnect, Is.True);
        Assert.That(errors.Single().Friendly, Is.EqualTo("The connection to the MPD server was lost and could not be restored. Press Connect to try again."));
        Assert.That(errors.Single().Exception, Is.InstanceOf<IOException>(), "the technical exception stays available behind Details");
    }

    [Test]
    public async Task ARetryThatBreaksAgain_ClosesTheConnection()
    {
        (MpdViewModel model, FakeMpdClient client, FakeMpdConnector connector, _, _) = Create();
        await model.ConnectAsync();
        client.Fail = new IOException("Broken pipe");
        connector.OnConnect = () => { };
        await model.UpdateDatabaseAsync();
        Assert.That(model.IsConnected, Is.False, "the new connection broke as well, so the connection is closed");
        Assert.That(model.StatusMessage, Does.StartWith("The connection to MPD was lost"));
    }

    [Test]
    public async Task TheConnectionIsPingedWhileIdle()
    {
        (MpdViewModel model, FakeMpdClient client, _, _, _) = Create(keepAlive: TimeSpan.FromMilliseconds(30));
        await model.ConnectAsync();
        for (int attempt = 0; attempt < 100 && client.Calls.Count(call => call == "ping") < 2; attempt++) await Task.Delay(20);
        Assert.That(client.Calls.Count(call => call == "ping"), Is.GreaterThanOrEqualTo(2));
        await model.ShutdownAsync();
    }

    [Test]
    public async Task AFailedPing_ReconnectsInTheBackground()
    {
        (MpdViewModel model, FakeMpdClient client, FakeMpdConnector connector, _, _) = Create(keepAlive: TimeSpan.FromMilliseconds(30));
        await model.ConnectAsync();
        connector.OnConnect = () => client.Fail = null;
        client.Fail = new IOException("Broken pipe");
        for (int attempt = 0; attempt < 200 && connector.ConnectCalls < 2; attempt++) await Task.Delay(20);
        Assert.That(connector.ConnectCalls, Is.GreaterThanOrEqualTo(2));
        for (int attempt = 0; attempt < 100 && model.IsReconnecting; attempt++) await Task.Delay(20);
        Assert.That(model.IsConnected, Is.True);
        await model.ShutdownAsync();
    }

    [Test]
    public async Task AFailedWatcher_ReconnectsInsteadOfClosing()
    {
        (MpdViewModel model, _, FakeMpdConnector connector, _, _) = Create();
        connector.WatchFailure = new IOException("closed");
        connector.OnConnect = () => { if (connector.ConnectCalls >= 2) connector.WatchFailure = null; };
        await model.ConnectAsync();
        for (int attempt = 0; attempt < 200 && connector.ConnectCalls < 2; attempt++) await Task.Delay(20);
        Assert.That(connector.ConnectCalls, Is.EqualTo(2));
        for (int attempt = 0; attempt < 100 && model.IsReconnecting; attempt++) await Task.Delay(20);
        Assert.That(model.IsConnected, Is.True);
        Assert.That(model.StatusMessage, Is.EqualTo("Reconnected to MPD."));
        await model.ShutdownAsync();
    }

    [Test]
    public async Task TheClock_AdvancesWhilePlayingAndStopsAtTheEnd()
    {
        (MpdViewModel model, FakeMpdClient client, _, _, _) = Create();
        await model.ConnectAsync();
        model.AdvanceClock(TimeSpan.FromSeconds(5));
        Assert.That(model.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(15)));
        model.AdvanceClock(TimeSpan.FromSeconds(500));
        Assert.That(model.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(100)));
        client.Status = client.Status with { State = MpdState.Stopped, Elapsed = null, Duration = null };
        await model.RefreshAsync();
        Assert.That((model.State, model.CurrentSongId, model.Elapsed), Is.EqualTo((MpdState.Stopped, (int?)null, TimeSpan.Zero)));
        model.AdvanceClock(TimeSpan.FromSeconds(5));
        Assert.That(model.Elapsed, Is.EqualTo(TimeSpan.Zero));
        await model.ShutdownAsync();

        (MpdViewModel ticking, _, _, _, _) = Create(clock: TimeSpan.FromMilliseconds(20));
        await ticking.ConnectAsync();
        TimeSpan before = ticking.Elapsed;
        for (int attempt = 0; attempt < 100 && ticking.Elapsed == before; attempt++) await Task.Delay(20);
        Assert.That(ticking.Elapsed, Is.GreaterThan(before), "the position moves along on its own while a song plays");
        await ticking.ShutdownAsync();
    }

    [Test]
    public async Task Transport_AddsPlaysAndControlsOnTheServer()
    {
        (MpdViewModel model, FakeMpdClient client, _, _, _) = Create();
        await model.ConnectAsync();
        client.Calls.Clear();
        int id = await model.AddSongAsync("a/b.flac", CancellationToken.None);
        Assert.That(id, Is.EqualTo(42));
        Assert.That(client.Calls, Is.EqualTo(new[] { "addid a/b.flac" }), "adding does not play or change any setting");

        client.Calls.Clear();
        client.Status = client.Status with { SingleMode = MpdSingleMode.Off };
        await model.PlaySongAsync(7, CancellationToken.None);
        Assert.That(client.Calls.Where(call => call.StartsWith("single", StringComparison.Ordinal) || call.StartsWith("playid", StringComparison.Ordinal)), Is.EqualTo(new[] { "single OneShot", "playid 7" }));
        client.Calls.Clear();
        await model.PauseAsync(CancellationToken.None);
        await model.ResumeAsync(CancellationToken.None);
        await model.SeekAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await model.SetServerVolumeAsync(40, CancellationToken.None);
        await model.StopAsync(CancellationToken.None);
        await model.RemoveSongAsync(7, CancellationToken.None);
        Assert.That(client.Calls.Where(call => !call.StartsWith("status", StringComparison.Ordinal) && !call.StartsWith("currentsong", StringComparison.Ordinal) && !call.StartsWith("single", StringComparison.Ordinal)),
            Is.EqualTo(new[] { "pause True", "pause False", "seek 30", "vol 40", "stop", "deleteid 7" }));
        client.Picture = new MpdPicture([1, 2], "image/png");
        Assert.That(await model.GetCoverAsync("a.flac", CancellationToken.None), Is.EqualTo(new byte[] { 1, 2 }));
        client.Picture = null;
        Assert.That(await model.GetCoverAsync("a.flac", CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Transport_PutsTheSingleSettingBackWhenTheServerStops()
    {
        (MpdViewModel model, FakeMpdClient client, _, _, _) = Create();
        await model.ConnectAsync();
        client.Status = client.Status with { SingleMode = MpdSingleMode.Off };
        await model.PlaySongAsync(7, CancellationToken.None);
        client.Calls.Clear();
        client.Status = client.Status with { State = MpdState.Stopped, SingleMode = MpdSingleMode.OneShot };
        await model.StopAsync(CancellationToken.None);
        Assert.That(client.Calls, Does.Contain("single Off"), "the server stopped, so what we changed is undone");
        client.Calls.Clear();
        await model.RefreshAsync();
        Assert.That(client.Calls, Has.None.EqualTo("single Off"), "and only once");

        client.Status = client.Status with { State = MpdState.Playing, SingleMode = MpdSingleMode.On };
        await model.PlaySongAsync(8, CancellationToken.None);
        client.Calls.Clear();
        client.Status = client.Status with { State = MpdState.Stopped };
        await model.RefreshAsync();
        Assert.That(client.Calls, Has.None.StartsWith("single"), "a mode the user chose is never touched");
    }

    [Test]
    public async Task Transport_ReportsFailuresAsFriendlyInvalidOperations()
    {
        (MpdViewModel model, FakeMpdClient client, _, _, _) = Create();
        await Assert.ThatAsync(() => model.PauseAsync(CancellationToken.None), Throws.InvalidOperationException.With.Message.Contains("Connect to an MPD server first"));
        await model.ConnectAsync();
        client.Fail = new MpdProtocolException(50, 0, "playid", "No such song");
        await Assert.ThatAsync(() => model.PlaySongAsync(1, CancellationToken.None),
            Throws.InvalidOperationException.With.Message.EqualTo("The MPD server could not do that: No such song").And.InnerException.InstanceOf<MpdProtocolException>());
        client.Fail = null;
        client.FailWhen = call => call.StartsWith("deleteid", StringComparison.Ordinal) ? new MpdProtocolException(50, 0, "deleteid", "No such song") : null;
        await model.RemoveSongAsync(1, CancellationToken.None);
        client.FailWhen = null;
        Assert.That(model.IsConnected, Is.True);
        client.Fail = new IOException("pipe");
        await Assert.ThatAsync(() => model.PauseAsync(CancellationToken.None),
            Throws.InvalidOperationException.With.Message.EqualTo("The connection to the MPD server was lost and could not be restored. Press Connect to try again.").And.InnerException.InstanceOf<IOException>());
        Assert.That(model.IsConnected, Is.False);
    }

    [Test]
    public async Task ApplyConnection_SavesSettingsButNotThePasswordAndOnlyWhileDisconnected()
    {
        (MpdViewModel model, _, FakeMpdConnector connector, List<MpdConnectionSettings> saved, _) = Create();
        model.ApplyConnection(new MpdConnectionSettings(false, "music.lan", 6601), "secret");
        Assert.That(saved.Single(), Is.EqualTo(new MpdConnectionSettings(false, "music.lan", 6601)));
        Assert.That(model.Settings.Host, Is.EqualTo("music.lan"));
        Assert.That(model.EndpointText, Is.EqualTo("music.lan:6601"));
        Assert.That(model.Password, Is.EqualTo("secret"));
        await model.ConnectAsync();
        Assert.That(connector.LastEndpoint, Is.EqualTo(new MpdEndpoint("music.lan", 6601, "secret")));
        model.ApplyConnection(MpdConnectionSettings.Default, "");
        Assert.That(saved, Has.Count.EqualTo(1), "settings cannot change while connected");
        Assert.Throws<ArgumentNullException>(() => model.ApplyConnection(null!, ""));
        Assert.Throws<ArgumentNullException>(() => _ = new MpdViewModel(null!));
    }

    [Test]
    public async Task Shutdown_IsIdempotentAndBlocksFurtherConnections()
    {
        (MpdViewModel model, _, _, _, _) = Create();
        await model.ConnectAsync();
        await model.ShutdownAsync();
        await model.ShutdownAsync();
        Assert.That(model.IsConnected, Is.False);
        model.Dispose();
        model.Dispose();
        Assert.That(model.CanConnect, Is.False);
        await model.ConnectAsync();
        Assert.That(model.IsConnected, Is.False);
        Assert.That(new MpdPlaylistRow(new MpdStoredPlaylist("P", null)).Name, Is.EqualTo("P"));
    }
}
