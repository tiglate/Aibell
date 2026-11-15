using Aibell.Application.Playback;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Mpd.Client;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.Playback;

/// <summary>
/// Plays a song on the MPD server found the way the application finds it (environment, local mpd.conf, user socket), so it needs
/// <c>AIBELL_MPD_REAL_TESTS=1</c> and a server whose music you do not mind hearing. It restores the volume and the single mode and
/// removes the queue entry it added; it does not change anything else on the server.
/// </summary>
[TestFixture]
public sealed class MpdRealServerTests
{
    private static async Task UntilAsync(Func<bool> condition, int seconds = 10)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out.");
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task TheEnginePlaysOnTheRealServerAndSurvivesTheIdleTimeout()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_MPD_REAL_TESTS") != "1") Assert.Ignore("Set AIBELL_MPD_REAL_TESTS=1 to play on the real MPD server.");
        MpdConnector connector = new();
        MpdEndpoint endpoint = connector.Locate(MpdConnectionSettings.Default, null);
        TestContext.Progress.WriteLine($"MPD at {endpoint}");

        await using IMpdClient probe = await connector.ConnectAsync(endpoint, CancellationToken.None);
        MpdStatus before = await probe.GetStatusAsync();
        int queueBefore = (await probe.GetQueueAsync()).Count;
        IReadOnlyList<MpdEntry> top = await probe.ListFolderAsync("");
        MpdSong? song = null;
        if (Environment.GetEnvironmentVariable("AIBELL_MPD_REAL_SONG") is { Length: > 0 } wanted)
            song = (await probe.ListSongsAsync("")).FirstOrDefault(candidate => candidate.Uri == wanted);
        foreach (MpdEntry folder in song is null ? top.Where(entry => entry.Kind == MpdEntryKind.Directory) : [])
        {
            song = (await probe.ListSongsAsync(folder.Path)).FirstOrDefault(candidate => candidate.Duration is { } length && length > TimeSpan.FromSeconds(45));
            if (song is not null) break;
        }
        if (song is null) Assert.Ignore("The server has no song longer than 45 seconds to play.");

        MpdViewModel model = new(connector, MpdConnectionSettings.Default, null, null, MpdViewModel.DefaultRetryDelays, MpdViewModel.DefaultKeepAlive);
        MpdPlaybackEngine engine = new();
        engine.Attach(model);
        try
        {
            await model.ConnectAsync();
            Assert.That(model.IsConnected, Is.True, model.StatusMessage);
            Assert.That(model.Playlists, Is.Not.Null);
            Assert.That(await model.GetAllSongsAsync(CancellationToken.None), Is.Not.Empty);

            Aibell.Domain.Media.Track track = MpdTracks.FromSong(song);
            await engine.LoadAsync(track);
            Assert.That((await probe.GetQueueAsync()).Count, Is.EqualTo(queueBefore + 1), "one entry added behind the user's own queue");
            await engine.PlayAsync();
            await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing);
            TestContext.Progress.WriteLine($"Playing {track.Title} ({track.Duration})");

            await UntilAsync(() => engine.Snapshot.Position > TimeSpan.FromSeconds(1), 15);
            await engine.SeekAsync(TimeSpan.FromSeconds(20));
            await UntilAsync(() => engine.Snapshot.Position >= TimeSpan.FromSeconds(19));
            await engine.PauseAsync();
            await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Paused);
            await engine.PlayAsync();
            await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing);

            // The song finishes by itself near the end: the engine says Ended and the server does not move on to its own queue.
            await engine.SeekAsync(track.Duration - TimeSpan.FromSeconds(3));
            await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Ended, 20);
            Assert.That((await probe.GetStatusAsync()).State, Is.EqualTo(MpdState.Stopped));

            // MPD closes a client that stays silent for 60 seconds; the pings keep ours open.
            await Task.Delay(TimeSpan.FromSeconds(75));
            await model.RefreshAsync();
            Assert.That(model.IsConnected, Is.True, model.StatusMessage);
            Assert.That(model.StatusMessage, Does.Not.Contain("lost"));

            await engine.PlayAsync();
            await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing);
            await engine.StopAsync();
            await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Stopped);
        }
        finally
        {
            await engine.ReleaseAsync();
            await model.ShutdownAsync();
            engine.Dispose();
            model.Dispose();
        }
        // The server closes a client that stayed silent for a minute (the probe did), so check with a fresh one.
        await using IMpdClient after = await connector.ConnectAsync(endpoint, CancellationToken.None);
        MpdStatus finished = await after.GetStatusAsync();
        Assert.That((await after.GetQueueAsync()).Count, Is.EqualTo(queueBefore), "the queue is as the user left it");
        Assert.That(finished.SingleMode, Is.EqualTo(before.SingleMode), "the single setting is as the user left it");
    }
}
