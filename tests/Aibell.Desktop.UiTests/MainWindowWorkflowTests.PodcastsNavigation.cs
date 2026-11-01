using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using Aibell.Application.Podcasts;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    [AvaloniaTest]
    public async Task MainWindow_MusicRadioPodcastsRoundTrip_KeepsEmbeddedPodcastsAndMusicState()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-podcast-nav-{Guid.NewGuid():N}");
        string media = Path.Combine(directory, "music");
        Directory.CreateDirectory(media);
        foreach (string extension in new[] { "flac", "wav" })
            File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", $"fixture.{extension}"), Path.Combine(media, $"fixture.{extension}"));
        PodcastService? service = null;
        PodcastsViewModel? podcasts = null;
        int created = 0;
        Open(radio: true, podcastViewModelFactory: handoff => { created++; return podcasts = new(service!, handoff); });
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        service = new(repository, new OfflinePodcastTransport(), new OfflinePodcastTransport(), new PodcastDownloadStore(Path.Combine(directory, "podcasts")));
        Guid id = Guid.NewGuid();
        await repository.AddSubscriptionAsync(new(id, new Uri("https://example.invalid/feed"), "Cached show", "", DateTimeOffset.UtcNow),
            [new(id, "a", "Alpha launch", new Uri("https://example.invalid/a.mp3")), new(id, "b", "Beta report", new Uri("https://example.invalid/b.mp3"))]);
        await viewModel.ImportAsync(media);
        await UntilAsync(() => viewModel.Tracks.Count == 2);
        viewModel.SelectedTrack = viewModel.Tracks.Last();
        Guid selectedTrack = viewModel.SelectedTrack.Source.Id;
        PodcastsView pane = window.FindControl<PodcastsView>("PodcastsPane")!;
        ListBox trackList = window.FindControl<ListBox>("TrackList")!;
        Assert.Multiple(() =>
        {
            Assert.That(pane.IsEffectivelyVisible, Is.False);
        });

        Activate(window.FindControl<Button>("OpenPodcastsButton")!);
        await UntilAsync(() => podcasts?.Episodes.Count == 2);
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsPodcastsView, Is.True);
            Assert.That(pane.IsEffectivelyVisible, Is.True, "Podcasts opens in the main content area.");
            Assert.That(trackList.IsEffectivelyVisible, Is.False);
            Assert.That(window.OwnedWindows, Is.Empty, "Normal runtime never opens a Podcasts popup.");
            Assert.That(window.FindControl<TextBox>("SearchBox")!.IsEnabled, Is.False, "Library search must not mutate Music while Podcasts shows.");
            Assert.That(window.FindControl<Button>("SelectRadioButton")!.IsVisible, Is.True, "The sidebar stays.");
        });
        podcasts!.SearchText = "ALPHA";
        Assert.That(podcasts.Episodes.Single().EpisodeId, Is.EqualTo("a"));

        Activate(window.FindControl<Button>("SelectMusicButton")!);
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsPodcastsView, Is.False);
            Assert.That(trackList.IsEffectivelyVisible, Is.True);
            Assert.That(viewModel.Tracks, Has.Count.EqualTo(2));
            Assert.That(viewModel.SelectedTrack?.Source.Id, Is.EqualTo(selectedTrack), "Music selection survives the Podcasts visit.");
        });

        Activate(window.FindControl<Button>("OpenPodcastsButton")!);
        Assert.Multiple(() =>
        {
            Assert.That(created, Is.EqualTo(1), "The app-owned model is created once.");
            Assert.That(podcasts.SearchText, Is.EqualTo("ALPHA"), "Podcast state survives navigation.");
            Assert.That(podcasts.Episodes, Has.Count.EqualTo(1));
        });

        Activate(window.FindControl<Button>("SelectRadioButton")!);
        await UntilAsync(() => viewModel.IsRadioView);
        Assert.That(pane.IsEffectivelyVisible, Is.False);
        Activate(window.FindControl<Button>("OpenPodcastsButton")!);
        Activate(window.FindControl<Button>("SelectMusicButton")!);
        await UntilAsync(() => viewModel.IsMusicView && !viewModel.IsBusy);
        Assert.That(window.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_PodcastsView_TransportIgnoresHiddenMusicSelection()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-podcast-transport-{Guid.NewGuid():N}");
        string media = Path.Combine(directory, "music");
        Directory.CreateDirectory(media);
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.wav"), Path.Combine(media, "fixture.wav"));
        PodcastService? service = null;
        Open(podcastViewModelFactory: handoff => new(service!, handoff));
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        service = new(repository, new OfflinePodcastTransport(), new OfflinePodcastTransport(), new PodcastDownloadStore(Path.Combine(directory, "podcasts")));
        await viewModel.ImportAsync(media);
        await UntilAsync(() => viewModel.Tracks.Count == 1);
        viewModel.SelectedTrack = viewModel.Tracks.Single();
        Assert.That(viewModel.CanPlay, Is.True);
        Activate(window.FindControl<Button>("OpenPodcastsButton")!);
        Assert.That(viewModel.CanPlay, Is.False, "A hidden Music selection must not make the shell Play button start Music.");
        await viewModel.TogglePlaybackAsync();
        Assert.That(engine.LoadCount, Is.Zero);
    }
}
