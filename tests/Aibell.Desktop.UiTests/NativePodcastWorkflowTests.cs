using System.Net;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Library;
using Aibell.Application.Podcasts;
using Aibell.Application.Playback;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Playback;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Settings;
using Aibell.Infrastructure.Storage;
using Aibell.Playback.LibVLC;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
[NonParallelizable]
[Category("NativePlayback")]
public sealed class NativePodcastWorkflowTests
{
    [AvaloniaTest]
    public async Task SubscribeDownloadRefreshPlayAndReopenOffline_UsesCachedLocalEpisodeAndSharedNativePlayer()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_NATIVE_PLAYBACK_TESTS") != "1")
            Assert.Ignore("Set AIBELL_NATIVE_PLAYBACK_TESTS=1 to validate downloaded podcast playback.");
        string directory = Path.Combine(Path.GetTempPath(), $"aibell-native-podcast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string fixture = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.mp3");
            byte[] original = await File.ReadAllBytesAsync(fixture);
            await using LocalAudioHttpServer server = new(original, servePodcastFeed: true);
            using HttpClient http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
            { Timeout = Timeout.InfiniteTimeSpan };
            string database = Path.Combine(directory, "library.db");
            string downloads = Path.Combine(directory, "podcasts");
            await using SqliteTrackRepository repository = new(database);
            PodcastService service = new(repository, new HttpPodcastFeedReader(http), new HttpPodcastEpisodeDownloader(http), new PodcastDownloadStore(downloads));
            using JsonApplicationSettingsStore settings = new(Path.Combine(directory, "settings.json"));
            LibraryImportService importer = new(new FileSystemMediaDiscovery(), new AtlMediaMetadataReader(), repository);
            using LibraryMonitoringService monitoring = new(repository, importer, NullLogger<LibraryMonitoringService>.Instance);
            using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
            using MainWindowViewModel model = new(importer, repository, repository, repository, repository, settings,
                monitoring, engine);
            PodcastsViewModel? podcasts = null;
            MainWindow window = new(model, podcastViewModelFactory: handoff => podcasts = new(service, handoff));
            TaskCompletionSource playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.StateChanged += (_, snapshot) =>
            {
                if (snapshot.Status == PlaybackStatus.Playing) playing.TrySetResult();
                if (snapshot.Status == PlaybackStatus.Ended) ended.TrySetResult();
                if (snapshot.Status == PlaybackStatus.Faulted)
                {
                    InvalidOperationException failure = new(snapshot.Error);
                    playing.TrySetException(failure);
                    ended.TrySetException(failure);
                }
            };
            window.Show();
            try
            {
                await UntilAsync(() => !model.IsBusy && model.Playlists.Count > 0);
                await model.SavePreferencesAsync(model.GetPreferences() with { Muted = true, Repeat = RepeatMode.Off });
                Click(window, "OpenPodcastsButton");
                await UntilAsync(() => model.IsPodcastsView && podcasts is not null);
                Assert.That(server.Requests, Is.Zero, "Opening the owned catalog stays offline.");
                TextBox feedUrl = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PodcastFeedUrl");
                feedUrl.Text = server.FeedAddress.AbsoluteUri;
                Click(window, "SubscribePodcast");
                await UntilAsync(() => podcasts!.Episodes.Count == 1 && !podcasts.IsBusy);
                PodcastEpisodeItem episode = podcasts!.Episodes.Single();
                Assert.Multiple(() =>
                {
                    Assert.That(server.FeedRequests, Is.EqualTo(1));
                    Assert.That(server.AudioRequests, Is.Zero, "Subscription does not fetch the enclosure.");
                    Assert.That(episode.State, Is.EqualTo(PodcastEpisodeState.NotDownloaded));
                });
                Click(window, "DownloadEpisode", episode);
                await UntilAsync(() => episode.State is PodcastEpisodeState.Downloaded or PodcastEpisodeState.Failed);
                Assert.That(episode.State, Is.EqualTo(PodcastEpisodeState.Downloaded), podcasts.StatusMessage);
                StoredPodcastEpisode stored = (await repository.ListEpisodesAsync(episode.Episode.SubscriptionId)).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(stored.LocalPath, Does.StartWith(downloads + Path.DirectorySeparatorChar));
                    Assert.That(stored.DownloadedBytes, Is.EqualTo(original.Length));
                    Assert.That(SHA256.HashData(File.ReadAllBytes(stored.LocalPath!)), Is.EqualTo(SHA256.HashData(original)));
                    Assert.That(server.AudioRequests, Is.EqualTo(1));
                });
                Click(window, "RefreshPodcast");
                await UntilAsync(() => !podcasts.IsBusy && podcasts.StatusMessage.Contains("no changes", StringComparison.Ordinal));
                await podcasts.WhenEpisodesLoadedAsync();
                PodcastSubscription refreshed = (await repository.ListSubscriptionsAsync()).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(server.NotModifiedResponses, Is.EqualTo(1));
                    Assert.That(refreshed.EntityTag, Is.EqualTo(LocalAudioHttpServer.FeedEntityTag));
                    Assert.That(refreshed.LastModified, Is.EqualTo(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero)));
                    Assert.That(podcasts.Episodes.Single().Episode.LocalPath, Is.EqualTo(stored.LocalPath));
                });
                int requestsBeforePlay = server.Requests;
                // The fixture is short: the queue can already be consumed when "playing" is observed, so record what it held.
                List<string> queuedTitles = [];
                model.QueueItems.CollectionChanged += (_, _) => queuedTitles.AddRange(model.QueueItems.Select(item => item.Row.Title));
                Click(window, "PlayEpisode", podcasts.Episodes.Single());
                await playing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Multiple(() =>
                {
                    Assert.That(queuedTitles, Is.Not.Empty, "The episode went through the shared queue.");
                    Assert.That(queuedTitles, Is.All.EqualTo("Downloaded fixture episode"));
                });
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await UntilAsync(() => model.QueueItems.Count == 0);
                await model.WaitForPlaybackOperationsAsync();
                Assert.Multiple(() =>
                {
                    Assert.That(engine.Snapshot.Track!.Location, Is.EqualTo(new Uri(stored.LocalPath!)));
                    Assert.That(engine.Snapshot.Track.Location.IsFile, Is.True);
                    Assert.That(engine.Snapshot.IsMuted, Is.True);
                    Assert.That(model.QueueItems, Is.Empty, "The shared queue consumes the episode at its natural end.");
                    Assert.That(server.Requests, Is.EqualTo(requestsBeforePlay), "Playback consumes the local file only.");
                });
                Assert.That(await repository.ListAsync(), Is.Empty, "Podcast playback does not admit the episode to Music.");
                Click(window, "SelectMusicButton");
                Assert.That(model.IsPodcastsView, Is.False, "Music returns from the embedded Podcasts view.");
                await server.DisposeAsync();
                Click(window, "OpenPodcastsButton");
                await UntilAsync(() => model.IsPodcastsView && podcasts!.Episodes.Count == 1);
                Assert.Multiple(() =>
                {
                    Assert.That(podcasts!.Subscriptions, Has.Count.EqualTo(1));
                    Assert.That(podcasts.Episodes.Single().CanPlay, Is.True);
                    Assert.That(podcasts.Episodes.Single().Episode.LocalPath, Is.EqualTo(stored.LocalPath));
                    Assert.That(server.Requests, Is.EqualTo(requestsBeforePlay), "Offline reopen reads SQLite and the downloaded file.");
                });
                Assert.That(await File.ReadAllBytesAsync(fixture), Is.EqualTo(original));
            }
            finally
            {
                window.Close();
                await UntilAsync(() => !window.IsVisible);
            }
            // Independent connection verifies persisted validators and downloaded state after the window drained.
            await using SqliteTrackRepository reopened = new(database);
            PodcastSubscription persisted = (await reopened.ListSubscriptionsAsync()).Single();
            Assert.That(persisted.EntityTag, Is.EqualTo(LocalAudioHttpServer.FeedEntityTag));
            Assert.That((await reopened.ListEpisodesAsync(persisted.Id)).Single().DownloadState, Is.EqualTo(PodcastDownloadState.Downloaded));
            Assert.That(Directory.GetFiles(downloads, "*.partial", SearchOption.AllDirectories), Is.Empty);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Click(Window window, string name, object? context = null)
    {
        Dispatcher.UIThread.RunJobs();
        Button button = window.GetVisualDescendants().OfType<Button>().Single(candidate =>
            candidate.Name == name && candidate.IsVisible && (context is null || ReferenceEquals(candidate.DataContext, context)));
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
}
