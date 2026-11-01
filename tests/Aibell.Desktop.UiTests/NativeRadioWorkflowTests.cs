using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Aibell.Application.Library;
using Aibell.Application.Radio;
using Aibell.Application.Playback;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Domain.Radio;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Settings;
using Aibell.Infrastructure.Storage;
using Aibell.Infrastructure.Radio;
using Aibell.Playback.LibVLC;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
[NonParallelizable]
[Category("NativePlayback")]
public sealed class NativeRadioWorkflowTests
{
    [AvaloniaTest]
    public async Task DirectHttpRadio_ImportIsOffline_PlayEndReconnectAndStop_UsesOnlyTheOwnedLoopbackServer()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_NATIVE_PLAYBACK_TESTS") != "1")
            Assert.Ignore("Set AIBELL_NATIVE_PLAYBACK_TESTS=1 to validate local HTTP radio playback.");
        string directory = Path.Combine(Path.GetTempPath(), $"aibell-native-radio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] audio = await File.ReadAllBytesAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.mp3"));
            await using LocalAudioHttpServer server = new(audio);
            await using SqliteTrackRepository repository = new(Path.Combine(directory, "library.db"));
            using JsonApplicationSettingsStore settings = new(Path.Combine(directory, "settings.json"));
            LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), repository);
            using LibraryMonitoringService monitoring = new(repository, importer, NullLogger<LibraryMonitoringService>.Instance);
            using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
            using MainWindowViewModel model = new(importer, repository, repository, repository, repository, settings,
                monitoring, engine, radioRepository: repository,
                radioPlaylistFiles: new RadioPlaylistImportService(new RadioPlaylistFileStore(), repository));
            MainWindow window = new(model); window.Show();
            TaskCompletionSource playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource reconnecting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int attempt = 1;
            engine.StateChanged += (_, snapshot) =>
            {
                if (snapshot.Status == PlaybackStatus.Playing)
                {
                    if (Volatile.Read(ref attempt) == 1) playing.TrySetResult(); else reconnecting.TrySetResult();
                }
                if (snapshot.Status == PlaybackStatus.Ended) ended.TrySetResult();
                if (snapshot.Status == PlaybackStatus.Faulted)
                {
                    InvalidOperationException failure = new(snapshot.Error);
                    playing.TrySetException(failure); ended.TrySetException(failure); reconnecting.TrySetException(failure);
                }
            };
            try
            {
                await UntilAsync(() => !model.IsBusy && model.Playlists.Count > 0);
                await model.SavePreferencesAsync(model.GetPreferences() with { Muted = true, Repeat = RepeatMode.One });
                string source = Path.Combine(directory, "owned-station.pls");
                await File.WriteAllTextAsync(source, $"[playlist]\nFile1={server.Address.AbsoluteUri}\nTitle1=Owned loopback fixture\n");
                byte[] original = await File.ReadAllBytesAsync(source);
                var preview = await model.PrepareRadioPlaylistAsync(new Uri(source));
                await model.ImportRadioPlaylistAsync(preview); await model.SelectRadioAsync();
                RadioStation station = model.RadioStations.Single();
                Assert.That(server.Requests, Is.Zero);
                Track pending = new(Guid.NewGuid(), new Uri(Path.Combine(directory, "pending.flac")), "Pending", "Artist", "Album", TimeSpan.FromSeconds(1));
                model.QueueTracks([pending]);
                await model.PlayRadioStationAsync(station.Id);
                await playing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await UntilAsync(() => model.StatusMessage.StartsWith("Radio stream ended", StringComparison.Ordinal));
                await model.WaitForPlaybackOperationsAsync();
                int completedRequests = server.Requests;
                await Task.Delay(150); Dispatcher.UIThread.RunJobs();
                Assert.Multiple(() =>
                {
                    Assert.That(completedRequests, Is.GreaterThan(0)); Assert.That(server.Requests, Is.EqualTo(completedRequests));
                    Assert.That(engine.Snapshot.Track!.Location, Is.EqualTo(server.Address));
                    Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Ended));
                    Assert.That(model.DurationText, Is.EqualTo("LIVE")); Assert.That(model.CanSeekPlayback, Is.False);
                    Assert.That(model.QueueItems, Has.Count.EqualTo(1)); Assert.That(engine.Snapshot.IsMuted, Is.True);
                });
                Assert.That((await repository.ListStationsAsync()).Single().PlayCount, Is.EqualTo(1));
                Assert.That(await repository.ListAsync(), Is.Empty);
                Volatile.Write(ref attempt, 2);
                await model.TogglePlaybackAsync(); await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await model.StopPlaybackAsync();
                Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
                Assert.That((await repository.ListStationsAsync()).Single().PlayCount, Is.EqualTo(2));
                Assert.That(await File.ReadAllBytesAsync(source), Is.EqualTo(original));
            }
            finally { window.Close(); await UntilAsync(() => !window.IsVisible); }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true) { Dispatcher.UIThread.RunJobs(); if (condition()) return; await Task.Delay(10, timeout.Token); }
    }
}
