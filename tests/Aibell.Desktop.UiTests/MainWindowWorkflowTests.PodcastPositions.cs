using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Aibell.Application.Podcasts;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Podcasts;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    [AvaloniaTest]
    public async Task MainWindow_PodcastSaveAndResume_UsesSelectedEpisodeAndPreservesSource()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-podcast-positions-{Guid.NewGuid():N}");
        string downloads = Path.Combine(directory, "downloads");
        Directory.CreateDirectory(downloads);
        string file = Path.Combine(downloads, "episode.mp3");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.mp3"), file);
        byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(file));
        PodcastService? service = null;
        PodcastsViewModel? podcasts = null;
        Open(podcastViewModelFactory: handoff => podcasts = new(service!, handoff));
        await UntilAsync(() => !viewModel.IsBusy);
        PositionOfflineTransport transport = new();
        service = new(repository, transport, transport, new PodcastDownloadStore(downloads),
            positions: repository, metadata: new AtlMediaMetadataReader());
        Guid id = Guid.NewGuid();
        await repository.AddSubscriptionAsync(new(id, new Uri("https://example.invalid/feed"), "Offline show", "", DateTimeOffset.UtcNow),
            [new(id, "episode", "Episode", new Uri("https://example.invalid/audio.mp3"))]);
        await repository.RecordDownloadAsync(id, "episode", file, new FileInfo(file).Length, DateTimeOffset.UtcNow);
        Activate(window.FindControl<Button>("OpenPodcastsButton")!);
        await UntilAsync(() => podcasts?.Episodes.Count == 1);
        podcasts!.SelectedEpisode = podcasts.Episodes.Single();
        await podcasts.PlayAsync(podcasts.SelectedEpisode);
        TimeSpan saved = engine.Snapshot.Track!.Duration / 3;
        await viewModel.SeekAsync(saved.TotalSeconds);
        saved = engine.Snapshot.Position;
        await engine.PauseAsync();
        await podcasts.SaveSelectedPositionAsync();
        Assert.That((await repository.ListEpisodesAsync(id)).Single().SavedProgress!.Position, Is.EqualTo(saved));
        Assert.That(podcasts.CanResumeSelected, Is.True);
        engine.Calls.Clear();
        await podcasts.ResumeSelectedAsync();
        Assert.That(engine.Calls, Is.EqualTo(AudiobookStartCalls));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(saved));
        await podcasts.PlayAsync(podcasts.SelectedEpisode);
        Assert.That(engine.Snapshot.Position, Is.EqualTo(saved), "Normal Play continues from the stored position.");
        await podcasts.StartOverAsync(podcasts.SelectedEpisode);
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero), "Start over is the explicit way to begin again.");
        Assert.That((await repository.ListEpisodesAsync(id)).Single().SavedProgress!.Position, Is.EqualTo(saved), "Start over does not rewrite the stored position by itself.");
        await engine.LoadAsync(engine.Snapshot.Track! with { Location = new Uri(Path.Combine(downloads, "unrelated.mp3")) });
        await engine.PlayAsync();
        await podcasts.SaveSelectedPositionAsync();
        Assert.That(podcasts.StatusMessage, Does.Contain("Play or pause this downloaded episode"));
        Assert.That((await repository.ListEpisodesAsync(id)).Single().SavedProgress!.Position, Is.EqualTo(saved));
        Assert.That(await repository.ListAsync(), Is.Empty);
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(file)), Is.EqualTo(hash));
        window.Close();
        await UntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task MainWindow_StopDuringPodcastMetadataRead_DoesNotStartDeferredResume()
    {
        string file = PrepareAudiobook();
        BlockingMetadataReader reader = new();
        Open(metadataReader: reader);
        await UntilAsync(() => !viewModel.IsBusy);
        Task play = viewModel.PlayAsync(new PodcastPlaybackRequest(new Uri(file), "Episode", "Show", null, TimeSpan.FromMilliseconds(100)));
        await UntilAsync(() => reader.Started);
        await viewModel.StopPlaybackAsync();
        reader.Release.TrySetResult();
        await Assert.ThatAsync(async () => await play, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(engine.LoadCount, Is.Zero);
        Assert.That(viewModel.QueueItems, Is.Empty);
    }

    private sealed class PositionOfflineTransport : IPodcastFeedReader, IPodcastEpisodeDownloader
    {
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Position actions must not fetch a feed.");
        public Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Position actions must not download media.");
    }
}
