using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Podcasts;
using Aibell.Desktop.ViewModels;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class PodcastsWindowTests
{
    private string directory = null!;
    private PodcastLoopback server = null!;
    private HttpClient http = null!;
    private readonly List<Window> windows = [];
    private readonly List<SqliteTrackRepository> repositories = [];

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-podcast-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        server = new(File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.mp3")));
        http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (PodcastsWindow window in windows.Cast<PodcastsWindow>()) { await window.ShutdownAsync(); window.Close(); }
        foreach (SqliteTrackRepository repository in repositories) await repository.DisposeAsync();
        windows.Clear(); repositories.Clear();
        server.Dispose(); http.Dispose(); SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }

    [AvaloniaTest]
    public async Task SubscribeThenReopenOffline_ShowsCachedFeedAndEpisodesWithoutAnyRequest()
    {
        (PodcastsWindow window, _, SqliteTrackRepository repository) = Open();
        Type("PodcastFeedUrl", window, server.FeedUrl.AbsoluteUri);
        Click("SubscribePodcast", window);
        await WaitAsync(() => Status(window).Contains("Subscribed", StringComparison.Ordinal));
        Assert.That(Rows<PodcastEpisodeItem>(window, "PodcastEpisodeList"), Has.Count.EqualTo(2));
        await window.ShutdownAsync(); window.Close(); await repository.DisposeAsync();
        int requests = server.Requests;
        server.Dispose();

        (PodcastsWindow reopened, _, _) = Open();
        await WaitAsync(() => Rows<PodcastSubscriptionItem>(reopened, "PodcastSubscriptionList").Count == 1);
        await WaitAsync(() => Rows<PodcastEpisodeItem>(reopened, "PodcastEpisodeList").Count == 2);
        Assert.That(requests, Is.EqualTo(1));
        Assert.That(server.Requests, Is.EqualTo(requests), "reopening is offline");
    }

    [AvaloniaTest]
    public async Task Subscribe_InvalidUrlAndDuplicate_ReportClearlyWithoutDuplicateRows()
    {
        (PodcastsWindow window, PodcastsViewModel viewModel, _) = Open();
        viewModel.FeedUrl = "https://user:secret@example.invalid/feed"; await viewModel.SubscribeAsync();
        Assert.That(Status(window), Does.Contain("without embedded credentials"));
        Assert.That(server.Requests, Is.Zero);
        viewModel.FeedUrl = server.FeedUrl.AbsoluteUri; await viewModel.SubscribeAsync();
        viewModel.FeedUrl = server.FeedUrl.AbsoluteUri; await viewModel.SubscribeAsync();
        Assert.That(viewModel.Subscriptions, Has.Count.EqualTo(1));
        Assert.That(Status(window), Does.Contain("Already subscribed"));
        Assert.That(server.FeedRequests, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Refresh_NotModifiedThenChanged_ShowsStatusAndNewEpisodes()
    {
        (PodcastsWindow window, PodcastsViewModel viewModel, _) = Open();
        viewModel.FeedUrl = server.FeedUrl.AbsoluteUri; await viewModel.SubscribeAsync();
        Click("RefreshPodcast", window);
        await WaitAsync(() => Status(window).Contains("no changes", StringComparison.Ordinal));
        Assert.That(server.NotModifiedResponses, Is.EqualTo(1));
        server.Version = 2;
        await viewModel.RefreshSelectedAsync();
        Assert.That(Status(window), Does.Contain("1 new"));
        Assert.That(viewModel.Episodes, Has.Count.EqualTo(3));
    }

    [AvaloniaTest]
    public async Task Refresh_ServerFailure_KeepsCachedEpisodesAndShowsError()
    {
        (PodcastsWindow window, PodcastsViewModel viewModel, _) = Open();
        viewModel.FeedUrl = server.FeedUrl.AbsoluteUri; await viewModel.SubscribeAsync();
        server.FailFeed = true;
        await viewModel.RefreshSelectedAsync();
        Assert.That(Status(window), Does.Contain("Refresh failed"));
        Assert.That(viewModel.Episodes, Has.Count.EqualTo(2));
        Assert.That(viewModel.IsBusy, Is.False);
    }

    [AvaloniaTest]
    public async Task Download_ThenPlay_WritesExactBytesAndHandsTheLocalFileToPlayback()
    {
        (PodcastsWindow window, PodcastsViewModel viewModel, _) = Open();
        viewModel.FeedUrl = server.FeedUrl.AbsoluteUri; await viewModel.SubscribeAsync();
        PodcastEpisodeItem episode = viewModel.Episodes.First(item => item.Title == "Episode One");
        viewModel.SelectedEpisode = episode;
        Assert.That(server.AudioRequests, Is.Zero, "subscribing never downloads enclosures");
        Click("DownloadEpisode", window, episode);
        await WaitAsync(() => episode.State == PodcastEpisodeState.Downloaded);
        Click("PlayEpisode", window, episode);
        await WaitAsync(() => handoff.Requests.Count == 1);

        PodcastPlaybackRequest request = handoff.Requests.Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.File.IsFile, Is.True);
            Assert.That(SHA256.HashData(File.ReadAllBytes(request.File.LocalPath)), Is.EqualTo(SHA256.HashData(server.Audio)));
            Assert.That(request.Title, Is.EqualTo("Episode One")); Assert.That(request.Show, Is.EqualTo("Loopback Show"));
            Assert.That(request.File.LocalPath, Does.StartWith(Path.Combine(directory, "podcasts")));
            Assert.That(server.AudioRequests, Is.EqualTo(1));
        });
        await window.ShutdownAsync();
    }

    [AvaloniaTest]
    public async Task Download_CancelButton_StopsTransferAndLeavesNoFileOrDownloadedState()
    {
        (PodcastsWindow window, PodcastsViewModel viewModel, _) = Open();
        viewModel.FeedUrl = server.FeedUrl.AbsoluteUri; await viewModel.SubscribeAsync();
        server.StallAudio = true;
        PodcastEpisodeItem episode = viewModel.Episodes.First();
        Click("DownloadEpisode", window, episode);
        await WaitAsync(() => episode is { State: PodcastEpisodeState.Downloading } && server.AudioRequests == 1);
        Click("CancelEpisodeDownload", window, episode);
        await WaitAsync(() => episode.State == PodcastEpisodeState.Cancelled);
        Assert.Multiple(() =>
        {
            Assert.That(episode.DownloadText, Is.EqualTo("Retry")); Assert.That(Status(window), Does.Contain("cancelled"));
            Assert.That(Directory.Exists(Path.Combine(directory, "podcasts")) ? Directory.GetFiles(Path.Combine(directory, "podcasts"), "*", SearchOption.AllDirectories) : [], Is.Empty);
        });
        server.StallAudio = false;
        Click("DownloadEpisode", window, episode);
        await WaitAsync(() => episode.State == PodcastEpisodeState.Downloaded);
    }

    [AvaloniaTest]
    public async Task Download_ServerError_ShowsFailureAndAllowsRetry()
    {
        (PodcastsWindow window, PodcastsViewModel viewModel, _) = Open();
        viewModel.FeedUrl = server.FeedUrl.AbsoluteUri; await viewModel.SubscribeAsync();
        server.AudioStatus = 404;
        PodcastEpisodeItem episode = viewModel.Episodes.First();
        Click("DownloadEpisode", window, episode);
        await WaitAsync(() => episode.State == PodcastEpisodeState.Failed);
        Assert.That(episode.StateText, Does.StartWith("Download failed")); Assert.That(episode.CanPlay, Is.False);
        server.AudioStatus = 200;
        Click("DownloadEpisode", window, episode);
        await WaitAsync(() => episode.State == PodcastEpisodeState.Downloaded);
    }

    [AvaloniaTest]
    public async Task Remove_KeepsDownloadedFileAndClearsLists_AndShutdownCancelsInFlightWork()
    {
        (PodcastsWindow window, PodcastsViewModel viewModel, _) = Open();
        viewModel.FeedUrl = server.FeedUrl.AbsoluteUri; await viewModel.SubscribeAsync();
        PodcastEpisodeItem done = viewModel.Episodes.First();
        await viewModel.DownloadAsync(done);
        string file = viewModel.Episodes.First().Episode.LocalPath!;
        await viewModel.RemoveSelectedAsync();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.Subscriptions, Is.Empty); Assert.That(viewModel.Episodes, Is.Empty);
            Assert.That(File.Exists(file), Is.True); Assert.That(Status(window), Does.Contain("kept on disk"));
        });

        viewModel.FeedUrl = server.FeedUrl.AbsoluteUri; await viewModel.SubscribeAsync();
        server.StallAudio = true;
        PodcastEpisodeItem pending = viewModel.Episodes.Last();
        Task download = viewModel.DownloadAsync(pending);
        await WaitAsync(() => server.AudioRequests >= 2);
        await window.ShutdownAsync(); await window.ShutdownAsync();
        await download.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(pending.State, Is.EqualTo(PodcastEpisodeState.Downloading), "Closed UI does not receive late state mutations.");
        Assert.That(File.Exists(file), Is.True);
    }

    private readonly RecordingHandoff handoff = new();

    private (PodcastsWindow, PodcastsViewModel, SqliteTrackRepository) Open()
    {
        SqliteTrackRepository repository = new(Path.Combine(directory, "library.db"));
        repositories.Add(repository);
        PodcastService service = new(repository, new HttpPodcastFeedReader(http), new HttpPodcastEpisodeDownloader(http), new PodcastDownloadStore(Path.Combine(directory, "podcasts")));
        PodcastsViewModel viewModel = new(service, handoff);
        PodcastsWindow window = new(viewModel); windows.Add(window);
        window.Show(); Dispatcher.UIThread.RunJobs();
        return (window, viewModel, repository);
    }

    private static string Status(Window window) => window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PodcastStatus").Text ?? "";
    private static void Type(string name, Window window, string text) => window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == name).Text = text;
    private static List<T> Rows<T>(Window window, string list) =>
        window.GetVisualDescendants().OfType<ItemsControl>().Single(l => l.Name == list).Items.OfType<T>().ToList();

    private static void Click(string name, Window window, object? context = null)
    {
        Dispatcher.UIThread.RunJobs();
        Button button = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == name && b.IsVisible && (context is null || ReferenceEquals(b.DataContext, context)));
        Assert.That(button.IsEnabled, Is.True, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for the Podcasts window.");
            Dispatcher.UIThread.RunJobs(); await Task.Delay(20);
        }
    }

    private sealed class RecordingHandoff : IPodcastPlaybackHandoff
    {
        public List<PodcastPlaybackRequest> Requests { get; } = [];
        public Task PlayAsync(PodcastPlaybackRequest request, CancellationToken cancellationToken = default) { Requests.Add(request); return Task.CompletedTask; }
    }

    /// <summary>Owned loopback HTTP listener: RSS feed with ETag/304, audio, and switchable failures.</summary>
    private sealed class PodcastLoopback : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource stop = new();
        private int requests, feedRequests, audioRequests, notModified;
        public byte[] Audio { get; }
        public Uri FeedUrl { get; }
        public volatile int Version = 1;
        public volatile bool FailFeed, StallAudio;
        public volatile int AudioStatus = 200;
        public int Requests => Volatile.Read(ref requests);
        public int FeedRequests => Volatile.Read(ref feedRequests);
        public int AudioRequests => Volatile.Read(ref audioRequests);
        public int NotModifiedResponses => Volatile.Read(ref notModified);

        public PodcastLoopback(byte[] audio)
        {
            Audio = audio;
            int port;
            using (System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
            listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            FeedUrl = new Uri($"http://127.0.0.1:{port}/feed.xml");
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try { while (true) { HttpListenerContext context = await listener.GetContextAsync(); _ = Task.Run(() => HandleAsync(context)); } }
            catch (Exception) when (stop.IsCancellationRequested || !listener.IsListening) { }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            Interlocked.Increment(ref requests);
            try
            {
                HttpListenerResponse response = context.Response;
                if (context.Request.Url!.AbsolutePath == "/feed.xml")
                {
                    Interlocked.Increment(ref feedRequests);
                    string etag = $"\"v{Version}\"";
                    if (FailFeed) response.StatusCode = 500;
                    else if (context.Request.Headers["If-None-Match"] == etag) { Interlocked.Increment(ref notModified); response.StatusCode = 304; }
                    else
                    {
                        byte[] body = Encoding.UTF8.GetBytes(Feed());
                        response.ContentType = "application/rss+xml"; response.Headers["ETag"] = etag; response.ContentLength64 = body.Length;
                        await response.OutputStream.WriteAsync(body);
                    }
                }
                else
                {
                    Interlocked.Increment(ref audioRequests);
                    if (StallAudio)
                    {
                        response.ContentType = "audio/mpeg"; response.ContentLength64 = Audio.Length;
                        await response.OutputStream.WriteAsync(Audio.AsMemory(0, 16));
                        await response.OutputStream.FlushAsync();
                        await Task.Delay(Timeout.Infinite, stop.Token);
                    }
                    else if (AudioStatus != 200) response.StatusCode = AudioStatus;
                    else
                    {
                        response.ContentType = "audio/mpeg"; response.ContentLength64 = Audio.Length;
                        await response.OutputStream.WriteAsync(Audio);
                    }
                }
                response.Close();
            }
            catch (Exception) { try { context.Response.Abort(); } catch (Exception) { } }
        }

        private string Feed()
        {
            string items = $"""
                <item><title>Episode One</title><guid>ep-1</guid><pubDate>Thu, 01 Oct 2026 10:00:00 GMT</pubDate>
                  <enclosure url="http://127.0.0.1:{FeedUrl.Port}/audio/1.mp3" type="audio/mpeg" length="{Audio.Length}" /></item>
                <item><title>Episode Zero</title><guid>ep-0</guid><pubDate>Wed, 30 Sep 2026 10:00:00 GMT</pubDate>
                  <enclosure url="http://127.0.0.1:{FeedUrl.Port}/audio/0.mp3" type="audio/mpeg" length="{Audio.Length}" /></item>
                """;
            if (Version > 1) items += $"""<item><title>Episode Two</title><guid>ep-2</guid><pubDate>Fri, 02 Oct 2026 10:00:00 GMT</pubDate><enclosure url="http://127.0.0.1:{FeedUrl.Port}/audio/2.mp3" type="audio/mpeg" length="{Audio.Length}" /></item>""";
            return $"""<?xml version="1.0"?><rss version="2.0"><channel><title>Loopback Show</title><description>Synthetic</description>{items}</channel></rss>""";
        }

        public void Dispose()
        {
            if (stop.IsCancellationRequested) return;
            stop.Cancel(); try { listener.Stop(); listener.Close(); } catch (ObjectDisposedException) { }
        }
    }
}
