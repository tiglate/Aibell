using System.Security.Cryptography;
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
using Aibell.Playback.LibVLC;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
[NonParallelizable]
[Category("NativePlayback")]
public sealed class NativePlaybackWorkflowTests
{
    [AvaloniaTest]
    [TestCase("flac", "mp3")]
    [TestCase("wav", "m4a")]
    public async Task NaturalEnd_WhileCatalogStartIsBlocked_AdvancesThroughTheRealDispatcher(string firstCodec, string secondCodec)
    {
        if (Environment.GetEnvironmentVariable("AIBELL_NATIVE_PLAYBACK_TESTS") != "1")
            Assert.Ignore("Set AIBELL_NATIVE_PLAYBACK_TESTS=1 to validate native desktop auto-advance.");
        string directory = Path.Combine(Path.GetTempPath(), $"aibell-native-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteTrackRepository repository = new(Path.Combine(directory, "library.db"));
            using JsonApplicationSettingsStore settings = new(Path.Combine(directory, "settings.json"));
            Track first = Fixture("First", firstCodec), second = Fixture("Second", secondCodec);
            foreach (Track track in new[] { first, second }) await repository.UpsertAsync(track, directory);
            Dictionary<Uri, byte[]> sourceHashes = new[] { first, second }.ToDictionary(track => track.Location,
                track => SHA256.HashData(File.ReadAllBytes(track.Location.LocalPath)));
            BlockingPlaybackRepository tracks = new(repository) { BlockStarts = true };
            LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), tracks);
            using LibraryMonitoringService monitoring = new(repository, importer, NullLogger<LibraryMonitoringService>.Instance);
            using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
            using MainWindowViewModel model = new(importer, tracks, repository, repository, repository, settings,
                monitoring, engine);
            MainWindow window = new(model);
            TaskCompletionSource firstEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource secondPlaying = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource secondEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int firstEnds = 0, secondEnds = 0;
            engine.StateChanged += (_, snapshot) =>
            {
                if (snapshot.Status == PlaybackStatus.Faulted)
                {
                    InvalidOperationException failure = new(snapshot.Error);
                    firstEnded.TrySetException(failure); secondPlaying.TrySetException(failure); secondEnded.TrySetException(failure);
                }
                if (snapshot.Track?.Id == first.Id && snapshot.Status == PlaybackStatus.Ended)
                { Interlocked.Increment(ref firstEnds); firstEnded.TrySetResult(); }
                if (snapshot.Track?.Id == second.Id && snapshot.Status == PlaybackStatus.Playing) secondPlaying.TrySetResult();
                if (snapshot.Track?.Id == second.Id && snapshot.Status == PlaybackStatus.Ended)
                { Interlocked.Increment(ref secondEnds); secondEnded.TrySetResult(); }
            };
            window.Show();
            try
            {
                await UntilAsync(() => !model.IsBusy && model.Tracks.Count == 2);
                await model.SavePreferencesAsync(model.GetPreferences() with { Muted = true });
                model.SelectedTrack = model.Tracks.Single(row => row.Source.Id == first.Id);
                Task start = model.TogglePlaybackAsync();
                await UntilAsync(() => tracks.IsBlocked);
                await firstEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Dispatcher.UIThread.RunJobs();
                Assert.That(start.IsCompleted, Is.False, "The first track must end while its catalog write is held.");
                Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(first.Id));
                tracks.Release.TrySetResult(); await start;
                await secondPlaying.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await secondEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await UntilAsync(() => model.StatusMessage == "End of queue.");
                await model.WaitForPlaybackOperationsAsync();
                IReadOnlyList<Track> stored = await repository.ListAsync();
                Assert.Multiple(() =>
                {
                    Assert.That(stored.Select(track => track.PlayCount), Is.All.EqualTo(1));
                    Assert.That(stored.Select(track => track.SkipCount), Is.All.Zero);
                    Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(second.Id));
                    Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Ended));
                    Assert.That(engine.Snapshot.IsMuted, Is.True);
                    Assert.That(Volatile.Read(ref firstEnds), Is.EqualTo(1));
                    Assert.That(Volatile.Read(ref secondEnds), Is.EqualTo(1));
                });
                foreach ((Uri location, byte[] hash) in sourceHashes)
                    Assert.That(SHA256.HashData(File.ReadAllBytes(location.LocalPath)), Is.EqualTo(hash));
            }
            finally
            {
                tracks.Release.TrySetResult();
                window.Close(); await UntilAsync(() => !window.IsVisible);
            }

            Track Fixture(string title, string codec)
            {
                string path = Path.Combine(directory, title + "." + codec);
                File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture." + codec), path);
                return new(Guid.NewGuid(), new Uri(path), title, "Synthetic Artist", "Synthetic Album", TimeSpan.FromSeconds(1));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10, timeout.Token);
        }
    }
}
