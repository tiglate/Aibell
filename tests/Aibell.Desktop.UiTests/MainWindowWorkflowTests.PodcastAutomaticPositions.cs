using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Aibell.Application.Podcasts;
using Aibell.Domain.Media;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    [AvaloniaTest]
    public async Task MainWindow_PodcastAutomaticPositions_StopAndQueueReplay_SaveWithoutPodcastView()
    {
        string file = PrepareAudiobook(); Open(); await UntilAsync(() => !viewModel.IsBusy);
        var (service, subscription, episode) = await SeedAutomaticEpisodeAsync(file);
        viewModel.ConfigurePodcastProgress(service);
        await service.PlayAsync(subscription, episode, viewModel);
        Track track = engine.Snapshot.Track!;
        await viewModel.SeekAsync((track.Duration / 3).TotalSeconds);
        TimeSpan expected = engine.Snapshot.Position;
        await viewModel.StopPlaybackAsync();
        Assert.That((await repository.ListEpisodesAsync(subscription.Id)).Single().SavedProgress!.Position, Is.EqualTo(expected));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(Aibell.Application.Playback.PlaybackStatus.Stopped));
        // Replay the same downloaded file through the queue, with no Podcast handoff or visible panel.
        viewModel.QueueTracks([track]);
        await viewModel.PlayQueueEntryAsync(viewModel.QueueItems[^1].Id);
        await viewModel.SeekAsync((track.Duration / 2).TotalSeconds);
        expected = engine.Snapshot.Position;
        await engine.PauseAsync();
        await viewModel.FlushPodcastProgressAsync();
        Assert.That((await repository.ListEpisodesAsync(subscription.Id)).Single().SavedProgress!.Position, Is.EqualTo(expected));
        Assert.That(await repository.ListAsync(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_PodcastSpeed_AppliesOnlyToEpisodesAndIsRemembered()
    {
        string file = PrepareAudiobook(); Open(radio: true); await UntilAsync(() => !viewModel.IsBusy);
        var (service, subscription, episode) = await SeedAutomaticEpisodeAsync(file);
        viewModel.ConfigurePodcastProgress(service);
        Assert.That(() => viewModel.SetPodcastPlaybackRateAsync(1.1), Throws.TypeOf<ArgumentOutOfRangeException>());

        await viewModel.SetPodcastPlaybackRateAsync(1.5);
        await service.PlayAsync(subscription, episode, viewModel);
        Assert.That(engine.Rate, Is.EqualTo(1.5), "An episode starts at the chosen speed.");
        await viewModel.SetPodcastPlaybackRateAsync(2.0);
        Assert.That(engine.Rate, Is.EqualTo(2.0), "Changing speed while an episode plays applies at once.");

        string music = Path.Combine(directory, "music-speed.mp3"); File.Copy(file, music);
        viewModel.QueueTracks([engine.Snapshot.Track! with { Id = Guid.NewGuid(), Location = new Uri(music), Title = "Music" }]);
        await viewModel.PlayQueueEntryAsync(viewModel.QueueItems[^1].Id);
        Assert.That(engine.Rate, Is.EqualTo(1.0), "Music always plays at normal speed.");
        await viewModel.SetPodcastPlaybackRateAsync(1.25);
        Assert.That(engine.Rate, Is.EqualTo(1.0), "Choosing a speed never touches a non-podcast input.");

        var station = new Aibell.Domain.Radio.RadioStation(Guid.NewGuid(), "Radio", new Uri("https://example.invalid/live"));
        await viewModel.SaveRadioStationAsync(station, true);
        await service.PlayAsync(subscription, episode, viewModel);
        Assert.That(engine.Rate, Is.EqualTo(1.25));
        await viewModel.PlayRadioStationAsync(station.Id);
        Assert.That(engine.Rate, Is.EqualTo(1.0), "Radio always plays at normal speed.");

        await viewModel.FlushPreferencesAsync();
        Assert.That((await settings.LoadAsync()).PodcastPlaybackRate, Is.EqualTo(1.25));
    }

    [AvaloniaTest]
    public async Task MainWindow_PodcastAutomaticPositions_OutgoingMusicAndRadioLoads_PreserveEpisode()
    {
        string file = PrepareAudiobook(); Open(radio: true); await UntilAsync(() => !viewModel.IsBusy);
        var (service, subscription, episode) = await SeedAutomaticEpisodeAsync(file);
        viewModel.ConfigurePodcastProgress(service);
        await service.PlayAsync(subscription, episode, viewModel);
        Track episodeTrack = engine.Snapshot.Track!;
        await viewModel.SeekAsync((episodeTrack.Duration / 3).TotalSeconds);
        TimeSpan expected = engine.Snapshot.Position;
        string music = Path.Combine(directory, "music.mp3"); File.Copy(file, music);
        viewModel.QueueTracks([episodeTrack with { Id = Guid.NewGuid(), Location = new Uri(music), Title = "Music" }]);
        await viewModel.PlayQueueEntryAsync(viewModel.QueueItems[^1].Id);
        Assert.That((await repository.ListEpisodesAsync(subscription.Id)).Single().SavedProgress!.Position, Is.EqualTo(expected));
        await service.PlayAsync(subscription, episode, viewModel);
        await viewModel.SeekAsync((episodeTrack.Duration / 2).TotalSeconds); expected = engine.Snapshot.Position;
        var station = new Aibell.Domain.Radio.RadioStation(Guid.NewGuid(), "Radio", new Uri("https://example.invalid/live"));
        await viewModel.SaveRadioStationAsync(station, true);
        await viewModel.PlayRadioStationAsync(station.Id);
        Assert.That((await repository.ListEpisodesAsync(subscription.Id)).Single().SavedProgress!.Position, Is.EqualTo(expected));
        await engine.PauseAsync(); await viewModel.FlushPodcastProgressAsync();
        Assert.That((await repository.ListEpisodesAsync(subscription.Id)).Single().SavedProgress!.Position, Is.EqualTo(expected));
        Assert.That(await repository.ListAsync(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_PodcastAutomaticPositions_CloseAfterShutdownFlag_FlushesBeforeDisposedStorage()
    {
        string file = PrepareAudiobook(); Open(); await UntilAsync(() => !viewModel.IsBusy);
        var (service, subscription, episode) = await SeedAutomaticEpisodeAsync(file);
        viewModel.ConfigurePodcastProgress(service);
        await service.PlayAsync(subscription, episode, viewModel);
        await viewModel.SeekAsync((engine.Snapshot.Track!.Duration / 3).TotalSeconds);
        TimeSpan expected = engine.Snapshot.Position;
        window.Close(); Dispatcher.UIThread.RunJobs(); await UntilAsync(() => !window.IsVisible);
        Assert.That((await repository.ListEpisodesAsync(subscription.Id)).Single().SavedProgress!.Position, Is.EqualTo(expected));
    }

    [AvaloniaTest]
    public async Task MainWindow_PodcastAutomaticPositions_FirstEverQueueFiles_ResolvesCachedIdentity()
    {
        string file = PrepareAudiobook(); Open(); await UntilAsync(() => !viewModel.IsBusy);
        var (service, subscription, episode) = await SeedAutomaticEpisodeAsync(file);
        viewModel.ConfigurePodcastProgress(service);
        // No Podcast Play handoff or open Podcasts view has ever registered this download.
        await viewModel.QueueFilesAsync([new Uri(episode.LocalPath!)]);
        await viewModel.PlayQueueEntryAsync(viewModel.QueueItems.Single().Id);
        await viewModel.SeekAsync((engine.Snapshot.Track!.Duration / 3).TotalSeconds);
        TimeSpan expected = engine.Snapshot.Position;
        await viewModel.StopPlaybackAsync();
        Assert.That((await repository.ListEpisodesAsync(subscription.Id)).Single().SavedProgress!.Position, Is.EqualTo(expected));
        Assert.That(await repository.ListAsync(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_PodcastAutomaticPositions_UnknownActualDuration_DoesNotUseFeedEstimate()
    {
        string file = PrepareAudiobook(); Open(metadataReader: new UnknownPodcastDurationReader());
        await UntilAsync(() => !viewModel.IsBusy);
        var (service, subscription, episode) = await SeedAutomaticEpisodeAsync(file);
        viewModel.ConfigurePodcastProgress(service);
        await viewModel.PlayAsync(new PodcastPlaybackRequest(new Uri(episode.LocalPath!), episode.Title, subscription.Title,
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(10), episode));
        Assert.That(engine.Snapshot.Track!.Duration, Is.EqualTo(TimeSpan.Zero));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
        await viewModel.StopPlaybackAsync();
        Assert.That((await repository.ListEpisodesAsync(subscription.Id)).Single().SavedProgress, Is.Null);
    }

    private sealed class UnknownPodcastDurationReader : Aibell.Application.Library.IMediaMetadataReader
    {
        public Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaMetadata("Unknown length", "Show", "Show", TimeSpan.Zero));
    }

    private async Task<(PodcastService Service, PodcastSubscription Subscription, StoredPodcastEpisode Episode)> SeedAutomaticEpisodeAsync(string file)
    {
        Guid id = Guid.NewGuid();
        PodcastSubscription subscription = new(id, new Uri("https://example.invalid/feed"), "Show", "", DateTimeOffset.UtcNow);
        StoredPodcastEpisode episode = new(id, "episode", "Episode", new Uri("https://example.invalid/episode.mp3"));
        await repository.AddSubscriptionAsync(subscription, [episode]);
        PodcastDownloadStore store = new(directory);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(episode.EpisodeId)))[..16].ToLowerInvariant();
        string downloaded = store.PrepareDestination(id, hash + ".mp3").LocalPath;
        File.Copy(file, downloaded);
        await repository.RecordDownloadAsync(id, episode.EpisodeId, downloaded, new FileInfo(downloaded).Length, DateTimeOffset.UnixEpoch);
        PositionOfflineTransport transport = new();
        return (new(repository, transport, transport, store), subscription,
            (await repository.ListEpisodesAsync(id)).Single());
    }
}
