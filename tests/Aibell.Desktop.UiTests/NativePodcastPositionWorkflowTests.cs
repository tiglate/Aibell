using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Podcasts;
using Aibell.Application.Library;
using Aibell.Application.Playback;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Playback;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Settings;
using Aibell.Infrastructure.Storage;
using Aibell.Playback.LibVLC;
using LibVLCSharp.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
[NonParallelizable]
[Category("NativePlayback")]
public sealed class NativePodcastPositionWorkflowTests
{
    [AvaloniaTest]
    public async Task PauseWhileHiddenReopenOfflineNormalPlayAndStartOver_UsesAutomaticOffsetWithoutChangingFileOrMusic()
    {
        RequireNative();
        string directory = Path.Combine(Path.GetTempPath(), $"aibell-native-podcast-positions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string downloads = Path.Combine(directory, "downloads"); Directory.CreateDirectory(downloads);
            Guid id = Guid.NewGuid();
            string source = PrepareProbe(downloads, id); WriteProbe(source);
            byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            TimeSpan saved;
            await using (Profile profile = new(directory))
            {
                await profile.Repository.AddSubscriptionAsync(new(id, new Uri("https://example.invalid/feed"), "Offline position show", "Prepared downloaded episode; all actions stay offline.", DateTimeOffset.UtcNow),
                    [new(id, "probe", "Shared player position probe", new Uri("https://example.invalid/probe.wav"), Duration: TimeSpan.FromSeconds(20))]);
                await profile.Repository.RecordDownloadAsync(id, "probe", source, new FileInfo(source).Length, DateTimeOffset.UtcNow);
                await profile.OpenAsync(); await profile.OpenCatalogAsync();
                Assert.That(profile.CatalogModel!.SelectedEpisode!.Episode.SavedProgress, Is.Null);
                Click(profile.Window, "PlayEpisode");
                Assert.That(await profile.Capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(1000));
                await UntilAsync(() => profile.Engine.Snapshot.Status == PlaybackStatus.Playing && !profile.CatalogModel.IsPositionBusy);
                await profile.Model.SeekAsync(5);
                await UntilAsync(() => profile.Player.Time >= 5200 && profile.Engine.Snapshot.Position >= TimeSpan.FromSeconds(5.2));
                Assert.That(profile.Model.TryReturnFromOverlayToMusic(), Is.True);
                Assert.That(profile.Model.IsPodcastsView, Is.False);
                await profile.Model.TogglePlaybackAsync();
                await UntilAsync(() => profile.Engine.Snapshot.Status == PlaybackStatus.Paused);
                saved = profile.Engine.Snapshot.Position;
                Assert.That(saved, Is.InRange(TimeSpan.FromSeconds(5.2), TimeSpan.FromSeconds(8)));
                Assert.That(Math.Abs(profile.Player.Time - saved.TotalMilliseconds), Is.LessThan(400));
                await profile.Model.FlushPodcastProgressAsync();
                Assert.That((await profile.Repository.ListEpisodesAsync(id)).Single().SavedProgress!.Position, Is.EqualTo(saved), "Automatic progress continues while the Podcasts pane is hidden.");
                await profile.OpenCatalogAsync();
                await UntilAsync(() => !profile.CatalogModel.IsPositionBusy && profile.CatalogModel.CanResumeSelected);
                Assert.That((await profile.Repository.ListEpisodesAsync(id)).Single().SavedProgress!.Position, Is.EqualTo(saved));
                Assert.That(profile.CatalogModel.SelectedEpisode.SavedPositionText, Does.StartWith("Saved 0:0"));
                Assert.That(profile.Engine.Snapshot.Track!.Location, Is.EqualTo(new Uri(source)));
                Assert.That(await profile.Repository.ListAsync(), Is.Empty);
                Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(source)), Is.EqualTo(hash));
            }
            // The entire owner, SQLite repository, service, view models and native player are reconstructed offline.
            await using (Profile reopened = new(directory))
            {
                await reopened.OpenAsync(); await reopened.OpenCatalogAsync();
                Assert.That(reopened.CatalogModel!.SelectedEpisode!.Episode.SavedProgress!.Position, Is.EqualTo(saved));
                Click(reopened.Window, "PlayEpisode");
                short first = await reopened.Capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10));
                int expected = 1000 + (int)saved.TotalMilliseconds;
                TestContext.Out.WriteLine($"Saved {saved.TotalMilliseconds:F0} ms; first resumed PCM {first}; expected {expected}..{expected + 5}.");
                Assert.That(first, Is.InRange(expected, expected + 5), "The first decoded PCM identifies the saved source offset; opening PCM is 1000.");
                await UntilAsync(() => reopened.Engine.Snapshot.Status == PlaybackStatus.Playing && !reopened.CatalogModel.IsPositionBusy);
                await reopened.Model.TogglePlaybackAsync();
                await UntilAsync(() => reopened.Engine.Snapshot.Status == PlaybackStatus.Paused);
                await reopened.Model.FlushPodcastProgressAsync();
                Assert.That((await reopened.Repository.ListEpisodesAsync(id)).Single().SavedProgress!.Position, Is.GreaterThanOrEqualTo(saved));
                await reopened.Model.StopPlaybackAsync();
                reopened.Capture.Reset();
                Click(reopened.Window, "StartOverEpisode");
                Assert.That(await reopened.Capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(1000), "Explicit Start over opens at zero even when progress exists.");
                await UntilAsync(() => reopened.Engine.Snapshot.Status == PlaybackStatus.Playing && !reopened.CatalogModel.IsPositionBusy);
                await reopened.Model.TogglePlaybackAsync();
                await UntilAsync(() => reopened.Engine.Snapshot.Status == PlaybackStatus.Paused);
                await reopened.Model.FlushPodcastProgressAsync();
                Assert.That((await reopened.Repository.ListEpisodesAsync(id)).Single().SavedProgress!.Position, Is.LessThan(saved), "Start over creates a new automatic progress session.");
                Assert.That(await reopened.Repository.ListAsync(), Is.Empty);
                Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(source)), Is.EqualTo(hash));
            }
            await using SqliteTrackRepository final = new(Path.Combine(directory, "library.db"));
            Assert.That((await final.ListEpisodesAsync(id)).Single().SavedProgress!.Position, Is.LessThan(saved));
            Assert.That(await final.ListAsync(), Is.Empty);
            Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(source)), Is.EqualTo(hash));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task OwnerClose_AcceptedSaveOrResume_DrainsUncooperativeMetadataWithoutLateMutation(bool resume)
    {
        RequireNative();
        string directory = Path.Combine(Path.GetTempPath(), $"aibell-native-podcast-position-close-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string downloads = Path.Combine(directory, "downloads"); Directory.CreateDirectory(downloads);
            Guid id = Guid.NewGuid();
            string source = PrepareProbe(downloads, id); WriteProbe(source);
            byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            DelayedMetadata delayed = new();
            await using Profile profile = new(directory, delayed);
            await profile.Repository.AddSubscriptionAsync(new(id, new Uri("https://example.invalid/feed"), "Close probe", "", DateTimeOffset.UtcNow),
                [new(id, "probe", "Owned operation", new Uri("https://example.invalid/probe.wav"))]);
            await profile.Repository.RecordDownloadAsync(id, "probe", source, new FileInfo(source).Length, DateTimeOffset.UtcNow);
            PodcastPlaybackProgress previous = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20), DateTimeOffset.UnixEpoch);
            await profile.Repository.SaveEpisodePositionAsync(id, "probe", source, previous);
            await profile.OpenAsync(); await profile.OpenCatalogAsync();
            if (!resume)
            {
                Click(profile.Window, "PlayEpisode");
                await profile.Capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await UntilAsync(() => profile.Engine.Snapshot.Status == PlaybackStatus.Playing && !profile.CatalogModel!.IsPositionBusy);
                await profile.Model.TogglePlaybackAsync();
                await UntilAsync(() => profile.Engine.Snapshot.Status == PlaybackStatus.Paused);
            }
            if (!resume)
            {
                await profile.Model.FlushPodcastProgressAsync();
                previous = (await profile.Repository.ListEpisodesAsync(id)).Single().SavedProgress!;
            }
            delayed.Enabled = true;
            Task accepted = resume ? profile.CatalogModel!.ResumeSelectedAsync() : profile.CatalogModel!.SaveSelectedPositionAsync();
            string status;
            try
            {
                await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(accepted.IsCompleted, Is.False);
                status = profile.CatalogModel!.StatusMessage;
                profile.Window.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.That(profile.CatalogModel!.ShutdownAsync().IsCompleted, Is.False, "Owner close must wait for accepted metadata even if it ignores cancellation.");
            }
            finally { delayed.Release.TrySetResult(); }
            await accepted;
            await UntilAsync(() => !profile.Window.IsVisible);
            Assert.That(profile.CatalogModel.StatusMessage, Is.EqualTo(status), "A late result never changes the closed catalog.");
            Assert.That((await profile.Repository.ListEpisodesAsync(id)).Single().SavedProgress!.Position, Is.EqualTo(previous.Position));
            Assert.That(await profile.Repository.ListAsync(), Is.Empty);
            if (resume) Assert.That(profile.Capture.FirstSample.Task.IsCompleted, Is.False, "Canceled deferred Resume never opens a native input.");
            Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(source)), Is.EqualTo(hash));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private static void RequireNative()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_NATIVE_PLAYBACK_TESTS") != "1")
            Assert.Ignore("Set AIBELL_NATIVE_PLAYBACK_TESTS=1 to verify native podcast saved positions.");
    }

    private static string PrepareProbe(string root, Guid subscriptionId)
    {
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("probe")))[..16].ToLowerInvariant();
        return new PodcastDownloadStore(root).PrepareDestination(subscriptionId, hash + ".wav").LocalPath;
    }

    private static void WriteProbe(string path)
    {
        using BinaryWriter writer = new(File.Create(path));
        const int frames = 48000 * 20;
        writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(frames * 2);
        // Each millisecond carries its own level, revealing the decoder's source offset.
        for (int frame = 0; frame < frames; frame++) writer.Write((short)(1000 + frame / 48));
    }

    private static void Click(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Button button = window.GetVisualDescendants().OfType<Button>().Single(candidate => candidate.Name == name && candidate.IsVisible);
        Assert.That(button.IsEnabled, Is.True, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

    private sealed class Profile : IAsyncDisposable
    {
        private readonly JsonApplicationSettingsStore settings;
        private readonly LibraryMonitoringService monitoring;
        public SqliteTrackRepository Repository { get; }
        public LibVlcPlaybackEngine Engine { get; } = new(["--aout=dummy"]);
        public MainWindowViewModel Model { get; }
        public MainWindow Window { get; }
        public PodcastsViewModel? CatalogModel { get; private set; }
        public MediaPlayer Player { get; }
        public PcmCapture Capture { get; }

        public Profile(string directory, IMediaMetadataReader? positionMetadata = null)
        {
            Repository = new(Path.Combine(directory, "library.db"));
            settings = new(Path.Combine(directory, "settings.json"));
            AtlMediaMetadataReader reader = new();
            LibraryImportService importer = new(new FileSystemMediaDiscovery(), reader, Repository);
            monitoring = new(Repository, importer, NullLogger<LibraryMonitoringService>.Instance);
            Model = new(importer, Repository, Repository, Repository, Repository, settings,
                monitoring, Engine);
            OfflineTransport offline = new();
            PodcastService service = new(Repository, offline, offline, new PodcastDownloadStore(Path.Combine(directory, "downloads")),
                positions: Repository, metadata: positionMetadata ?? reader);
            Model.ConfigurePodcastProgress(service);
            Window = new(Model, podcastViewModelFactory: handoff => CatalogModel = new(service, handoff));
            Engine.SetVolumeAsync(1).GetAwaiter().GetResult();
            Player = (MediaPlayer)typeof(LibVlcPlaybackEngine).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Engine)!;
            Capture = new(Player);
        }

        public async Task OpenAsync()
        {
            Window.Show();
            await UntilAsync(() => !Model.IsBusy && Model.Playlists.Count > 0);
            await Model.SavePreferencesAsync(Model.GetPreferences() with { Volume = 1, Muted = false, Repeat = RepeatMode.Off });
        }

        public async Task OpenCatalogAsync()
        {
            Click(Window, "OpenPodcastsButton");
            await UntilAsync(() => Model.IsPodcastsView && CatalogModel is { IsBusy: false, Episodes.Count: 1 });
            CatalogModel!.SelectedEpisode = CatalogModel.Episodes.Single();
        }

        public async ValueTask DisposeAsync()
        {
            Capture.Release();
            Window.Close();
            await UntilAsync(() => !Window.IsVisible);
            await Model.WaitForPlaybackOperationsAsync();
            await Model.ShutdownPodcastProgressAsync();
            Capture.Dispose(); Model.Dispose(); Engine.Dispose(); monitoring.Dispose(); settings.Dispose();
            await Repository.DisposeAsync();
        }
    }

    private sealed class DelayedMetadata : IMediaMetadataReader
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Domain.Media.MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default)
        {
            if (Enabled) { Started.TrySetResult(); await Release.Task; }
            return await new AtlMediaMetadataReader().ReadAsync(location, CancellationToken.None);
        }
    }

    private sealed class OfflineTransport : IPodcastFeedReader, IPodcastEpisodeDownloader
    {
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Native position workflow must remain offline.");
        public Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Native position workflow must not redownload media.");
    }

    private sealed class PcmCapture : IDisposable
    {
        private readonly MediaPlayer player;
        private readonly CancellationTokenSource closing = new();
        private readonly MediaPlayer.LibVLCAudioPlayCb callback;
        private TaskCompletionSource<short> firstSample = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<short> FirstSample => Volatile.Read(ref firstSample);
        public void Reset() => Volatile.Write(ref firstSample, new(TaskCreationOptions.RunContinuationsAsynchronously));

        public PcmCapture(MediaPlayer player)
        {
            this.player = player;
            callback = (_, samples, count, _) =>
            {
                if (count == 0) return;
                FirstSample.TrySetResult(Marshal.ReadInt16(samples));
                // A virtual sink consumes the supplied PCM at its sample rate.
                // Teardown releases the wait before Stop joins native threads.
                closing.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(count / 48000d));
            };
            player.SetAudioFormat("S16N", 48000, 1);
            player.SetAudioCallbacks(callback, null, null, null, null);
        }

        public void Release() => closing.Cancel();

        public void Dispose()
        {
            Release();
            player.Stop();
            GC.KeepAlive(callback);
            closing.Dispose();
        }
    }
}
