using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Library;
using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;
using Aibell.Application.Playback;
using Aibell.Application.Settings;
using Aibell.Application.Playlists;
using Aibell.Application.Radio;
using Aibell.Infrastructure.Playlists;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Radio;
using Aibell.Domain.Playback;
using Aibell.Domain.Playlists;
using Avalonia.Styling;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Domain.Radio;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Settings;
using Aibell.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed partial class MainWindowWorkflowTests
{
    private string directory = null!;
    private SqliteTrackRepository repository = null!;
    private JsonApplicationSettingsStore settings = null!;
    private LibraryMonitoringService monitoring = null!;
    private MainWindowViewModel viewModel = null!;
    private MainWindow window = null!;
    private TestPlaybackEngine engine = null!;

    [AvaloniaTest]
    public async Task MainWindow_ImportSearchPlaybackQueuePlaylistAndRestart_PreservesLibraryAndRequests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-workflow-{Guid.NewGuid():N}");
        string media = Path.Combine(directory, "music");
        Directory.CreateDirectory(media);
        foreach (string extension in new[] { "flac", "wav" })
            File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", $"fixture.{extension}"),
                Path.Combine(media, $"fixture.{extension}"));
        Dictionary<string, byte[]> originalHashes = Directory.GetFiles(media)
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));

        Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        // Native folder pickers are outside the headless platform. Enter through
        // the same import use case called after the real picker returns a folder.
        await viewModel.ImportAsync(media);
        await UntilAsync(() => window.FindControl<ListBox>("TrackList")!.ItemCount == 2);
        TextBox search = window.FindControl<TextBox>("SearchBox")!;
        search.Focus();
        window.KeyTextInput("no such title");
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Tracks.Count == 0);
        search.Text = "Fixture";
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Tracks.Count == 2);

        ListBox tracks = window.FindControl<ListBox>("TrackList")!;
        tracks.SelectedIndex = 0;
        Track selected = viewModel.SelectedTrack!.Source;
        Button play = window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("play"));
        Activate(play);
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing);
        Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(selected.Id));
        Activate(play);
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Paused);

        // Exercise the rendered row's Queue Last handler twice: duplicate
        // requests must remain distinct and retain order after reopening.
        Border row = tracks.GetVisualDescendants().OfType<Border>()
            .First(border => border.ContextMenu is not null && ReferenceEquals(border.DataContext, viewModel.SelectedTrack));
        MenuItem queueLast = row.ContextMenu!.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "Queue Last"));
        queueLast.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        queueLast.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => window.FindControl<ListBox>("PlayQueueList")!.ItemCount == 2);
        Guid[] queueIds = viewModel.QueueItems.Select(entry => entry.Id).ToArray();
        Assert.That(queueIds.Distinct().Count(), Is.EqualTo(2));

        Button create = window.GetVisualDescendants().OfType<Button>().Single(button =>
            AutomationProperties.GetName(button) == "Create smart playlist");
        Activate(create);
        await UntilAsync(() => window.OwnedWindows.OfType<SmartPlaylistDialog>().Any());
        SmartPlaylistDialog dialog = window.OwnedWindows.OfType<SmartPlaylistDialog>().Single();
        TextBox name = dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "SmartPlaylistName");
        name.Text = "Fixture playlist";
        var rule = dialog.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Children.OfType<ComboBox>().Count() == 2);
        rule.Children.OfType<ComboBox>().First().SelectedIndex = 0;
        rule.Children.OfType<TextBox>().Single().Text = "Fixture";
        Dispatcher.UIThread.RunJobs();
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Any(playlist => playlist.Name == "Fixture playlist"));
        ListBox playlists = window.FindControl<ListBox>("PlaylistList")!;
        playlists.SelectedItem = viewModel.Playlists.Single(playlist => playlist.Name == "Fixture playlist");
        await UntilAsync(() => viewModel.SelectedPlaylist?.Name == "Fixture playlist" && viewModel.Tracks.Count == 2);

        await CloseAsync();
        await ReleaseServicesAsync();
        Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.QueueItems.Count == 2);
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.QueueItems.Select(entry => entry.Id), Is.EqualTo(queueIds));
            Assert.That(viewModel.QueueItems.Select(entry => entry.Row.Source.Id), Is.All.EqualTo(selected.Id));
            Assert.That(viewModel.Playlists.Any(playlist => playlist.Name == "Fixture playlist" && playlist.IsSmart), Is.True);
            Assert.That(viewModel.Playlists.Single(playlist => playlist.Name == "Fixture playlist").TrackCount, Is.EqualTo(2));
            Assert.That(window.FindControl<ListBox>("TrackList")!.ItemCount, Is.EqualTo(2));
            Assert.That(engine.LoadCount, Is.Zero);
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        });
        foreach ((string path, byte[] hash) in originalHashes)
            Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(path)), Is.EqualTo(hash),
                "Import, playback and persistence must leave the original media unchanged.");
    }

    [AvaloniaTest]
    public async Task MainWindow_PodcastsOfflineEpisode_ReusesQueueEngineWithoutMusicAdmission()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-podcast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        PodcastService? service = null;
        PodcastsViewModel? podcastViewModel = null;
        OfflinePodcastTransport transport = new();
        Open(podcastViewModelFactory: handoff => podcastViewModel = new(service!, handoff));
        await UntilAsync(() => !viewModel.IsBusy);
        string root = Path.Combine(directory, "podcasts");
        Directory.CreateDirectory(root);
        PodcastSubscription subscription = new(Guid.NewGuid(), new Uri("https://example.invalid/feed"), "Offline show", "Cached show", DateTimeOffset.UtcNow);
        string file = new PodcastDownloadStore(root).PrepareDestination(subscription.Id,
            Convert.ToHexString(SHA256.HashData("episode-1"u8))[..16].ToLowerInvariant() + ".mp3").LocalPath;
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.mp3"), file, overwrite: true);
        StoredPodcastEpisode episode = new(subscription.Id, "episode-1", "Feed episode title", new Uri("https://example.invalid/episode.mp3"));
        await repository.AddSubscriptionAsync(subscription, [episode]);
        await repository.RecordDownloadAsync(subscription.Id, episode.EpisodeId, file, new FileInfo(file).Length, DateTimeOffset.UtcNow);
        StoredPodcastEpisode stored = (await repository.ListEpisodesAsync(subscription.Id)).Single();
        service = new(repository, transport, transport, new PodcastDownloadStore(root));
        Activate(window.FindControl<Button>("OpenPodcastsButton")!);
        await UntilAsync(() => podcastViewModel?.Episodes.Count == 1);
        Assert.That(podcastViewModel!.Subscriptions.Single().Title, Is.EqualTo("Offline show"));
        Assert.That(podcastViewModel.Episodes.Single().CanPlay, Is.True);
        Task playback = Task.Run(() => service.PlayAsync(subscription,
            stored, viewModel));
        await UntilAsync(() => playback.IsCompleted);
        await playback;
        Track first = engine.Snapshot.Track!;
        Assert.Multiple(() =>
        {
            Assert.That(engine.LoadCount, Is.EqualTo(1));
            Assert.That(first.Title, Is.EqualTo("Feed episode title"));
            Assert.That(first.ShowName, Is.EqualTo("Offline show"));
            Assert.That(viewModel.QueueItems.Single().Row.Source.Id, Is.EqualTo(first.Id));
            Assert.That(transport.Calls, Is.Zero);
        });
        Assert.That(await repository.ListAsync(), Is.Empty);
        await service.PlayAsync(subscription, stored, viewModel);
        Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(first.Id), "Metadata reader provides stable local identity.");
        window.Close();
        await UntilAsync(() => !window.IsVisible);
        Assert.That(window.OwnedWindows, Is.Empty);
        int queued = viewModel.QueueItems.Count;
        await viewModel.PlayAsync(new PodcastPlaybackRequest(new Uri(file), "Late episode", "Show", null));
        Assert.That(viewModel.QueueItems.Count, Is.EqualTo(queued));
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringPodcastMetadataRead_DrainsWithoutLateQueueMutation()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-podcast-close-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "episode.mp3");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.mp3"), file);
        BlockingMetadataReader reader = new();
        Open(metadataReader: reader);
        await UntilAsync(() => !viewModel.IsBusy);
        Task playback = viewModel.PlayAsync(new PodcastPlaybackRequest(new Uri(file), "Episode", "Show", null));
        await UntilAsync(() => reader.Started);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.That(window.IsVisible, Is.True);
        reader.Release.TrySetResult();
        await playback;
        await UntilAsync(() => !window.IsVisible);
        Assert.That(engine.LoadCount, Is.Zero);
        Assert.That(viewModel.QueueItems, Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringPodcastSubscribe_CancelsAndDrainsBeforeOwnerDisposal()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-podcast-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        PodcastService? service = null;
        PodcastsViewModel? podcastViewModel = null;
        BlockingPodcastFeedReader reader = new();
        Open(podcastViewModelFactory: handoff => podcastViewModel = new(service!, handoff));
        await UntilAsync(() => !viewModel.IsBusy);
        service = new(repository, reader, new OfflinePodcastTransport(), new PodcastDownloadStore(Path.Combine(directory, "podcasts")));
        Activate(window.FindControl<Button>("OpenPodcastsButton")!);
        await UntilAsync(() => podcastViewModel is not null);
        podcastViewModel!.FeedUrl = "https://example.invalid/feed";
        Task subscribe = podcastViewModel.SubscribeAsync();
        await UntilAsync(() => reader.Started);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.That(window.IsVisible, Is.True);
        Assert.That(reader.Token.IsCancellationRequested, Is.True);
        reader.Release.TrySetResult();
        await subscribe;
        await UntilAsync(() => !window.IsVisible);
        Assert.That(await repository.ListSubscriptionsAsync(), Is.Empty);
    }

    private sealed class BlockingPodcastFeedReader : IPodcastFeedReader
    {
        public bool Started { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default)
        {
            Started = true;
            Token = cancellationToken;
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            throw new AssertionException("Owner close must cancel the feed read.");
        }
    }

    private sealed class OfflinePodcastTransport : IPodcastFeedReader, IPodcastEpisodeDownloader
    {
        public int Calls { get; private set; }
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new AssertionException("Opening cached podcasts must stay offline."); }
        public Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new AssertionException("Playing a cached episode must stay offline."); }
    }

    [TearDown]
    public async Task TearDown()
    {
        if (window is not null) await CloseAsync();
        if (repository is not null) await ReleaseServicesAsync();
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [AvaloniaTest]
    public async Task MainWindow_MetadataContextMenu_SavesClickedTrackAndPreservesQueueAndFilesAfterRestart()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-properties-{Guid.NewGuid():N}");
        string media = Path.Combine(directory, "music"); Directory.CreateDirectory(media);
        string source = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac");
        File.Copy(source, Path.Combine(media, "first.flac")); File.Copy(source, Path.Combine(media, "second.flac"));
        byte[] original = SHA256.HashData(await File.ReadAllBytesAsync(source));
        Open(); await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media);
        ListBox list = window.FindControl<ListBox>("TrackList")!;
        await UntilAsync(() => list.ItemCount == 2);
        list.SelectedIndex = 0;
        await UntilAsync(() => viewModel.SelectedTrack is not null);
        Track editedTrack = viewModel.SelectedTrack!.Source;
        viewModel.QueueTracks([editedTrack, editedTrack]);
        Guid[] queueIds = viewModel.QueueItems.Select(entry => entry.Id).ToArray();
        Button play = window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("play"));
        Activate(play); await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing);
        list.SelectedIndex = 1;
        list.SelectedItems!.Add(viewModel.Tracks.Single(item => item.Source.Id == editedTrack.Id));
        Dispatcher.UIThread.RunJobs();
        Assert.That(list.SelectedItems, Has.Count.EqualTo(2));
        Border row = list.GetVisualDescendants().OfType<Border>().First(border => border.ContextMenu is not null &&
            border.DataContext is TrackRowViewModel item && item.Source.Id == editedTrack.Id);
        row.BringIntoView(); Dispatcher.UIThread.RunJobs();
        Avalonia.Point position = row.TranslatePoint(new Avalonia.Point(80, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(position, MouseButton.Right); window.MouseUp(position, MouseButton.Right);
        await UntilAsync(() => row.ContextMenu!.IsOpen);
        MenuItem metadata = row.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "View Metadata…"));
        Assert.That(((TrackRowViewModel)metadata.Tag!).Source.Id, Is.EqualTo(editedTrack.Id));
        Assert.That(row.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Add to Playlist"))
            .Items.OfType<MenuItem>().Any(item => Equals(item.Header, "New Playlist…")), Is.True);
        metadata.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); row.ContextMenu.Close();
        await UntilAsync(() => window.OwnedWindows.OfType<TrackPropertiesDialog>().Any());
        TrackPropertiesDialog dialog = window.OwnedWindows.OfType<TrackPropertiesDialog>().Single();
        TextBox title = dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "MetadataTitle");
        Assert.That(title.Text, Is.EqualTo(editedTrack.Title));
        title.Text = "Zebra saved title";
        dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "MetadataGenre").Text = "Catalog genre";
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
        dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "MetadataSortTitle").Text = "AAA";
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        await UntilAsync(() => !dialog.IsVisible && !viewModel.IsBusy);
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.Tracks[0].Source.Title, Is.EqualTo("Zebra saved title"));
            Assert.That(viewModel.Tracks[0].Source.Id, Is.EqualTo(editedTrack.Id));
            Assert.That(viewModel.QueueItems.Select(entry => entry.Id), Is.EqualTo(queueIds));
            Assert.That(viewModel.QueueItems.Select(entry => entry.Row.Source.Title), Is.All.EqualTo("Zebra saved title"));
            Assert.That(viewModel.NowPlaying, Does.StartWith("Zebra saved title"));
        });
        await CloseAsync(); await ReleaseServicesAsync();
        Open(); await UntilAsync(() => !viewModel.IsBusy && viewModel.Tracks.Count == 2);
        Assert.That(viewModel.Tracks.Single(item => item.Source.Id == editedTrack.Id).Source.Metadata!.SortTitle, Is.EqualTo("AAA"));
        list = window.FindControl<ListBox>("TrackList")!;
        list.SelectedItem = viewModel.Tracks.Single(item => item.Source.Id == editedTrack.Id);
        await UntilAsync(() => viewModel.SelectedTrack?.Source.Id == editedTrack.Id);
        Dispatcher.UIThread.RunJobs();
        Control selectedContainer = (Control)list.ContainerFromIndex(list.SelectedIndex)!;
        Assert.That(selectedContainer.Focus(), Is.True);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Alt);
        await UntilAsync(() => window.OwnedWindows.OfType<TrackPropertiesDialog>().Any());
        TrackPropertiesDialog restored = window.OwnedWindows.OfType<TrackPropertiesDialog>().Single();
        Assert.That(restored.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "MetadataTitle").Text, Is.EqualTo("Zebra saved title"));
        restored.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await UntilAsync(() => !restored.IsVisible);
        foreach (string file in Directory.GetFiles(media))
            Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(file)), Is.EqualTo(original));
    }

    [AvaloniaTest]
    public async Task MainWindow_FileSaveChoicePlaybackGuardAndRestore_UpdatesTagsAndLibrary()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-file-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string source = Path.Combine(media, "fixture.flac");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), source);
        ATL.Track seed = new(source);
        seed.Description = "Original description 日本語";
        Assert.That(seed.Save(), Is.True);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        Open(); await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media);
        window.FindControl<ListBox>("TrackList")!.SelectedIndex = 0;
        Track track = viewModel.SelectedTrack!.Source;
        TrackPropertiesData data = await viewModel.GetTrackPropertiesAsync(track);
        Assert.That(data.CanWriteFile, Is.True);
        await viewModel.SaveTrackMetadataAsync(track.Id, data.Metadata with
        {
            Rating = 4,
            Keywords = "Library keywords",
            ShowName = "Library show",
            Grouping = "Library group",
            SortTitle = "Library sort title",
            LicenseUri = "https://example.com/local-license"
        });
        await viewModel.TogglePlaybackAsync();
        Assert.That(async () => await viewModel.SaveTrackMetadataToFileAsync(data, data.Metadata with { Title = "Blocked" }),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(SHA256.HashData(File.ReadAllBytes(source)), Is.EqualTo(original));
        await viewModel.StopPlaybackAsync();
        // Open the real dialog through the keyboard shortcut.
        ListBox list = window.FindControl<ListBox>("TrackList")!;
        await UntilAsync(() => list.ContainerFromIndex(0) is not null);
        list.SelectedIndex = 0;
        ((Control)list.ContainerFromIndex(0)!).Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Alt);
        await UntilAsync(() => window.OwnedWindows.OfType<TrackPropertiesDialog>().Any());
        TrackPropertiesDialog dialog = window.OwnedWindows.OfType<TrackPropertiesDialog>().Single();
        dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "MetadataTitle").Text = "Written to file";
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "MetadataDescription").Text = "Edited description ação";
        dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "MetadataKeywords").Text = "Written keywords 日本語";
        dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "MetadataLicenseUri").Text = "https://example.com/written-license";
        dialog.GetVisualDescendants().OfType<Expander>().Single(expander => Equals(expander.Header, "Show and episode details")).IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "MetadataShowName").Text = "Written show 日本語";
        dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "MetadataSeasonNumber").Text = "2";
        dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "MetadataEpisodeNumber").Text = "7";
        dialog.GetVisualDescendants().OfType<ComboBox>().Single(input => input.Name == "MetadataSaveDestination").SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs(); dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        await UntilAsync(() => !dialog.IsVisible);
        Assert.That((await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source))).Metadata.Title, Is.EqualTo("Written to file"));
        Track saved = (await repository.ListAsync()).Single();
        Assert.That(saved.Title, Is.EqualTo("Written to file"));
        Assert.That(saved.Description, Is.EqualTo("Edited description ação"));
        Assert.That(saved.Keywords, Is.EqualTo("Written keywords 日本語"));
        Assert.That(saved.ShowName, Is.EqualTo("Written show 日本語"));
        Assert.That(saved.SeasonNumber, Is.EqualTo(2));
        Assert.That(saved.EpisodeNumber, Is.EqualTo(7));
        Assert.That(new ATL.Track(source).AdditionalFields.Values, Does.Contain("https://example.com/written-license"));
        Assert.That(saved.FileSize, Is.EqualTo(new FileInfo(source).Length));
        Assert.That(viewModel.StatusMessage, Does.Contain("Original backup:"));
        Assert.That(SHA256.HashData(File.ReadAllBytes(source)), Is.Not.EqualTo(original));
        // Restore from the editor and confirm the source and catalog return together.
        await UntilAsync(() => list.ContainerFromIndex(0) is not null);
        list.SelectedIndex = 0; ((Control)list.ContainerFromIndex(0)!).Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Alt);
        await UntilAsync(() => window.OwnedWindows.OfType<TrackPropertiesDialog>().Any());
        TrackPropertiesDialog restore = window.OwnedWindows.OfType<TrackPropertiesDialog>().Single();
        restore.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RestoreMetadataBackup")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => restore.OwnedWindows.OfType<MetadataBackupsDialog>().Any());
        MetadataBackupsDialog backups = restore.OwnedWindows.OfType<MetadataBackupsDialog>().Single();
        await UntilAsync(() => backups.GetVisualDescendants().OfType<ListBox>().Single().ItemCount > 0);
        backups.GetVisualDescendants().OfType<ListBox>().Single().SelectedIndex = 0;
        backups.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RestoreSelectedMetadataBackup")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => backups.OwnedWindows.Any());
        Window confirmation = backups.OwnedWindows.Single();
        confirmation.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmRestoreMetadata")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !restore.IsVisible);
        Assert.That(SHA256.HashData(File.ReadAllBytes(source)), Is.EqualTo(original));
        Assert.That((await repository.ListAsync()).Single().Title, Is.EqualTo(track.Title));
        Assert.That((await repository.ListAsync()).Single().Description, Is.EqualTo("Original description 日本語"));
        // File saves survive reopening, and the replaced version remains available as an undo backup.
        await CloseAsync(); await ReleaseServicesAsync(); Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Tracks.Count == 1);
        TrackPropertiesData reopened = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks[0].Source);
        Assert.That(reopened.FileBackup, Is.Not.Null);
        Assert.That(reopened.Metadata.Title, Is.EqualTo(track.Title));
        Assert.That(reopened.Metadata.Description, Is.EqualTo("Original description 日本語"));
        Assert.That(reopened.Metadata.Rating, Is.EqualTo(4));
        Assert.That(reopened.Metadata.Keywords, Is.Empty);
        Assert.That(reopened.Metadata.ShowName, Is.Empty);
        Assert.That(reopened.Metadata.SeasonNumber, Is.Zero);
        Assert.That(reopened.Metadata.EpisodeNumber, Is.Zero);
        Assert.That(reopened.Metadata.Grouping, Is.EqualTo("Library group"));
        Assert.That(reopened.Metadata.SortTitle, Is.EqualTo("Library sort title"));
        Assert.That(reopened.Metadata.LicenseUri, Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_M4aRestore_ReconcilesWritableSortTagsAndRetainsLocalValues()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-m4a-restore-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string source = Path.Combine(media, "fixture.m4a");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.m4a"), source);
        ATL.Track seed = new(source) { Group = "Original group", SortTitle = "Original sort title", Description = "Original description" };
        Assert.That(seed.Save(), Is.True);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        Open(); await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media);
        TrackPropertiesData data = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks.Single().Source);
        await viewModel.SaveTrackMetadataAsync(data.Track.Id, data.Metadata with { Keywords = "Local keywords", Rating = 3 });
        data = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks.Single().Source);
        await viewModel.SaveTrackMetadataToFileAsync(data, data.Metadata with
        {
            Grouping = "Written group",
            SortTitle = "Written sort title",
            Description = "Written description",
            Keywords = "Written keywords",
            LicenseUri = "https://example.com/written-license",
            ShowName = "Written show",
            SeasonNumber = 3,
            EpisodeNumber = 12
        });
        data = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks.Single().Source);
        Assert.That(data.FileBackup, Is.Not.Null);
        await viewModel.RestoreTrackFileAsync(data);
        TrackMetadata restored = (await viewModel.GetTrackPropertiesAsync(viewModel.Tracks.Single().Source)).Metadata;
        Assert.Multiple(() =>
        {
            Assert.That(restored.Grouping, Is.EqualTo("Original group"));
            Assert.That(restored.SortTitle, Is.EqualTo("Original sort title"));
            Assert.That(restored.Description, Is.EqualTo("Original description"));
            Assert.That(restored.Keywords, Is.Empty);
            Assert.That(restored.LicenseUri, Is.Empty);
            Assert.That(restored.ShowName, Is.Empty);
            Assert.That(restored.SeasonNumber, Is.Zero);
            Assert.That(restored.EpisodeNumber, Is.Zero);
            Assert.That(restored.Rating, Is.EqualTo(3));
            Assert.That(SHA256.HashData(File.ReadAllBytes(source)), Is.EqualTo(original));
        });
    }

    [AvaloniaTest]
    public async Task MainWindow_Preferences_PlaybackEngineChoice_IsOnThePlaybackTabAndPersistsAcrossRestart()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-engine-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory); Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        window.FindControl<Button>("OpenPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
        PreferencesDialog dialog = window.OwnedWindows.OfType<PreferencesDialog>().Single();
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        ComboBox engine = dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "PlaybackEngine");
        string[] expectedItems = OperatingSystem.IsLinux()
            ? ["PipeWire (Managed)", "ALSA (Managed)", "miniaudio", "VLC"]
            : ["miniaudio (Native)", "miniaudio (Managed)", "VLC"];
        Assert.That(engine.Items.Cast<string>(), Is.EqualTo(expectedItems));
        Assert.That(engine.SelectedIndex, Is.EqualTo(0), "the platform's preferred player is the default");
        engine.SelectedIndex = engine.ItemCount - 1;
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SavePreferences").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !dialog.IsVisible);
        Assert.That(viewModel.GetPreferences().PlaybackEngine, Is.EqualTo(PlaybackEngineKind.Vlc));
        await CloseAsync(); await ReleaseServicesAsync(); Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Assert.That(viewModel.GetPreferences().PlaybackEngine, Is.EqualTo(PlaybackEngineKind.Vlc));
    }

    [AvaloniaTest]
    public async Task MainWindow_Preferences_SaveCancelAndRestart_PreservesAppearanceAndPlaybackDefaults()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-preferences-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory); Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        window.FindControl<Button>("OpenPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
        PreferencesDialog dialog = window.OwnedWindows.OfType<PreferencesDialog>().Single();
        Assert.That(dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "PreferencesNotifications").IsEnabled, Is.False);
        dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "AppearanceTheme").SelectedIndex = 2;
        dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "PreferencesShowQueue").IsChecked = false;
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        dialog.GetVisualDescendants().OfType<Slider>().Single().Value = 37;
        dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "PreferencesMuted").IsChecked = true;
        dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "PreferencesShuffle").IsChecked = true;
        dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "PlaybackRepeat").SelectedIndex = (int)RepeatMode.All;
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SavePreferences").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !dialog.IsVisible);
        Assert.That(Avalonia.Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        Assert.That(viewModel.GetPreferences(), Is.EqualTo(new PreferencesData(ApplicationTheme.Dark, false, 0.37, true, true, RepeatMode.All, PlaybackEngine: PlaybackEngineDefaults.Preferred)));
        await CloseAsync(); await ReleaseServicesAsync(); Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Assert.That(viewModel.GetPreferences(), Is.EqualTo(new PreferencesData(ApplicationTheme.Dark, false, 0.37, true, true, RepeatMode.All, PlaybackEngine: PlaybackEngineDefaults.Preferred)));
        // The keyboard shortcut reopens preferences; Cancel keeps the saved choice.
        window.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
        await UntilAsync(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
        PreferencesDialog cancel = window.OwnedWindows.OfType<PreferencesDialog>().Single();
        cancel.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "AppearanceTheme").SelectedIndex = 1;
        cancel.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await UntilAsync(() => !cancel.IsVisible);
        Assert.That(viewModel.AppearanceTheme, Is.EqualTo(ApplicationTheme.Dark));
    }

    [AvaloniaTest]
    public async Task MainWindow_PreferencesFolderActions_RescanAndRemoveKeepMediaFiles()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-folders-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string file = Path.Combine(media, "fixture.flac");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), file);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(file));
        Open(); await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media);
        window.FindControl<Button>("OpenPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
        PreferencesDialog dialog = window.OwnedWindows.OfType<PreferencesDialog>().Single();
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
        ListBox folders = dialog.GetVisualDescendants().OfType<ListBox>().Single(); folders.SelectedIndex = 0;
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PreferencesRescanFolder")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !viewModel.IsBusy && viewModel.StatusMessage.StartsWith("Rescanned", StringComparison.Ordinal));
        folders.SelectedIndex = 0;
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PreferencesRemoveFolder")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => dialog.OwnedWindows.Any());
        Window confirmation = dialog.OwnedWindows.Single();
        confirmation.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmRemoveMusicFolder")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !viewModel.IsBusy && viewModel.LibrarySources.Count == 0);
        Assert.That(viewModel.Tracks, Is.Empty);
        Assert.That(SHA256.HashData(File.ReadAllBytes(file)), Is.EqualTo(hash));
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await UntilAsync(() => !dialog.IsVisible);
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringFileWrite_DrainsReplacementAndCatalogBeforeDisposal()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-file-close-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string file = Path.Combine(media, "fixture.flac");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), file);
        BlockingFileWriter blocking = new(); Open(blocking);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media);
        TrackPropertiesData data = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks[0].Source);
        Task save = viewModel.SaveTrackMetadataToFileAsync(data, data.Metadata with { Title = "Committed during close" });
        window.Close(); Dispatcher.UIThread.RunJobs();
        try { Assert.That(window.IsVisible, Is.True); Assert.That(save.IsCompleted, Is.False); }
        finally { blocking.Release.TrySetResult(); }
        await save.WaitAsync(TimeSpan.FromSeconds(10));
        await UntilAsync(() => !window.IsVisible);
        Assert.That((await repository.ListAsync()).Single().Title, Is.EqualTo("Committed during close"));
        Assert.That((await new AtlTrackFileDetailsReader().ReadAsync(new Uri(file))).Metadata.Title, Is.EqualTo("Committed during close"));
    }

    private sealed class BlockingFileWriter : IMetadataFileWriter
    {
        private readonly MetadataFileWriter inner = new(new AtlMetadataEditStager());
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MetadataFileWriteResult> WriteAsync(Uri location, MetadataEdit edit, string? expectedSourceHash = null, CancellationToken cancellationToken = default)
        { await Release.Task; return await inner.WriteAsync(location, edit, expectedSourceHash, cancellationToken); }
        public Task<IReadOnlyList<MetadataFileBackup>> GetBackupsAsync(Uri location, CancellationToken cancellationToken = default) => inner.GetBackupsAsync(location, cancellationToken);
        public Task<MetadataFileWriteResult> RestoreAsync(MetadataFileBackup backup, CancellationToken cancellationToken = default) => inner.RestoreAsync(backup, cancellationToken);
    }

    [AvaloniaTest]
    public async Task MainWindow_BackupManager_DeleteHistoricalVersion_KeepsCurrentFileAndLibrary()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-backups-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string file = Path.Combine(media, "fixture.flac");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), file);
        Open(); await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media);
        TrackPropertiesData original = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks[0].Source);
        await viewModel.SaveTrackMetadataToFileAsync(original, original.Metadata with { Title = "First" });
        TrackPropertiesData first = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks[0].Source);
        await viewModel.SaveTrackMetadataToFileAsync(first, first.Metadata with { Title = "Second" });
        byte[] hash = SHA256.HashData(File.ReadAllBytes(file));
        ListBox tracks = window.FindControl<ListBox>("TrackList")!;
        await UntilAsync(() => tracks.ContainerFromIndex(0) is not null);
        tracks.SelectedIndex = 0; ((Control)tracks.ContainerFromIndex(0)!).Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Alt);
        await UntilAsync(() => window.OwnedWindows.OfType<TrackPropertiesDialog>().Any());
        TrackPropertiesDialog properties = window.OwnedWindows.OfType<TrackPropertiesDialog>().Single();
        properties.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 3; Dispatcher.UIThread.RunJobs();
        properties.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ManageMetadataBackups")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => properties.OwnedWindows.OfType<MetadataBackupsDialog>().Any());
        MetadataBackupsDialog manager = properties.OwnedWindows.OfType<MetadataBackupsDialog>().Single();
        ListBox backups = manager.GetVisualDescendants().OfType<ListBox>().Single();
        await UntilAsync(() => backups.ItemCount == 2);
        backups.SelectedItem = backups.Items.OfType<MetadataBackupInfo>().Single(item => !item.CanRestore);
        Assert.That(manager.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RestoreSelectedMetadataBackup").IsEnabled, Is.False);
        manager.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DeleteMetadataBackup")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => manager.OwnedWindows.Any());
        Window confirmation = manager.OwnedWindows.Single();
        confirmation.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await UntilAsync(() => !confirmation.IsVisible);
        Assert.That(Directory.GetFiles(media, "*.backup"), Has.Length.EqualTo(2));
        manager.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DeleteMetadataBackup")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => manager.OwnedWindows.Any());
        confirmation = manager.OwnedWindows.Single();
        confirmation.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmDeleteMetadataBackup")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => backups.ItemCount == 1 && !viewModel.IsBusy);
        Assert.That(Directory.GetFiles(media, "*.backup"), Has.Length.EqualTo(1));
        Assert.That(SHA256.HashData(File.ReadAllBytes(file)), Is.EqualTo(hash));
        Assert.That((await repository.ListAsync()).Single().Title, Is.EqualTo("Second"));
        manager.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await UntilAsync(() => !manager.IsVisible);
        Assert.That(properties.IsVisible, Is.True);
        properties.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await UntilAsync(() => !properties.IsVisible);
    }

    [AvaloniaTest]
    public async Task MainWindow_WaveFileWriteAndRestore_PreservesCatalogOnlyFieldsAcrossRestart()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-wave-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string file = Path.Combine(media, "fixture.wav");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.wav"), file);
        byte[] original = SHA256.HashData(await File.ReadAllBytesAsync(file));
        Open(); await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media);
        TrackPropertiesData data = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks.Single().Source);
        Assert.That(data.CanWriteFile, Is.True);
        TrackMetadata local = data.Metadata with
        {
            Composer = "Local composer",
            AlbumArtist = "Local album artist",
            Description = "Local description",
            TrackTotal = 12,
            DiscNumber = 2,
            DiscTotal = 3,
            Rating = 4,
            SortTitle = "Local sort",
            ShowName = "Local show",
            Keywords = "Local keywords"
        };
        await viewModel.SaveTrackMetadataAsync(data.Track.Id, local);
        data = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks.Single().Source);
        Assert.That(async () => await viewModel.SaveTrackMetadataToFileAsync(data, local with { Composer = "Unsupported file composer" }),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(file)), Is.EqualTo(original));
        await viewModel.SaveTrackMetadataToFileAsync(data, local with { Title = "Written WAV title", Year = 0, TrackNumber = 2 });
        data = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks.Single().Source);
        Assert.That(data.Metadata.Title, Is.EqualTo("Written WAV title"));
        Assert.That(data.File!.Metadata.Title, Is.EqualTo("Written WAV title"));
        Assert.That(data.File.Metadata.Composer, Is.Empty);
        Assert.That(data.FileBackup, Is.Not.Null);
        await viewModel.RestoreTrackFileAsync(data);
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(file)), Is.EqualTo(original));
        await CloseAsync(); await ReleaseServicesAsync(); Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Tracks.Count == 1);
        data = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks.Single().Source);
        Assert.That(data.Metadata, Is.EqualTo(local));
        Assert.That(data.File!.Metadata.Title, Is.EqualTo(local.Title));
    }

    [AvaloniaTest]
    public async Task MainWindow_Mp3FileSaveAndRescan_KeepsIndexedDurationAndProvidesBackup()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-mp3-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string file = Path.Combine(media, "fixture.mp3");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.mp3"), file);
        Open(); await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media);
        TrackPropertiesData data = await viewModel.GetTrackPropertiesAsync(viewModel.Tracks[0].Source);
        Assert.That(data.CanWriteFile, Is.True);
        TimeSpan duration = data.Track.Duration;
        await viewModel.SaveTrackMetadataToFileAsync(data, data.Metadata with { Title = "MP3 file title" });
        viewModel.SelectedLibrarySource = viewModel.LibrarySources[0];
        await viewModel.RescanSelectedLibrarySourceAsync();
        Assert.That(viewModel.Tracks.Single().Source.Duration, Is.EqualTo(duration));
        Assert.That(viewModel.Tracks.Single().Source.Title, Is.EqualTo("MP3 file title"));
        Assert.That((await viewModel.GetTrackPropertiesAsync(viewModel.Tracks[0].Source)).FileBackup, Is.Not.Null);
    }

    [AvaloniaTest]
    public async Task MainWindow_ArtworkAddRemoveAndCacheRefresh_PreservesFolderArtwork()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-artwork-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string file = Path.Combine(media, "fixture.flac"), sidecar = Path.Combine(media, "cover.png");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), file);
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "back.png"), sidecar);
        byte[] front = File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "cover.png"));
        byte[] folderHash = SHA256.HashData(File.ReadAllBytes(sidecar));
        using LocalArtworkCache cache = new(Path.Combine(directory, "artwork-cache")); Open(artwork: cache);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media);
        ListBox tracks = window.FindControl<ListBox>("TrackList")!; tracks.SelectedIndex = 0;
        await viewModel.TogglePlaybackAsync(); await viewModel.StopPlaybackAsync(); await viewModel.WaitForArtworkAsync();
        for (int operation = 0; operation < 2; operation++)
        {
            await UntilAsync(() => tracks.ContainerFromIndex(0) is not null);
            tracks.SelectedIndex = 0; ((Control)tracks.ContainerFromIndex(0)!).Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Alt);
            await UntilAsync(() => window.OwnedWindows.OfType<TrackPropertiesDialog>().Any());
            TrackPropertiesDialog editor = window.OwnedWindows.OfType<TrackPropertiesDialog>().Single();
            if (operation == 0) editor.SetArtwork(front);
            else editor.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoveTrackArtwork")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(editor.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveTrackProperties").IsEnabled, Is.False,
                "Artwork changes require an explicit File destination.");
            editor.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "MetadataSaveDestination").SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs(); editor.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
            await UntilAsync(() => !editor.IsVisible);
            await viewModel.WaitForArtworkAsync();
            byte[] expected = operation == 0 ? SHA256.HashData(front) : folderHash;
            Assert.That(SHA256.HashData(File.ReadAllBytes(viewModel.ArtworkLocation!.LocalPath)), Is.EqualTo(expected));
            Assert.That(SHA256.HashData(File.ReadAllBytes(sidecar)), Is.EqualTo(folderHash));
            Assert.That((await new AtlTrackFileDetailsReader().ReadAsync(new Uri(file))).EmbeddedArtwork!, Has.Count.EqualTo(operation == 0 ? 1 : 0));
        }
        Assert.That(Directory.GetFiles(media, "*.backup"), Has.Length.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task MainWindow_RecoveryFromPreferences_DiscardsOnlyNestedTemporaryEdit()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-recovery-ui-{Guid.NewGuid():N}");
        string live = Directory.CreateDirectory(Path.Combine(directory, "live")).FullName;
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(media, "Artist", "Album")).FullName;
        string source = Path.Combine(live, "fixture.flac");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), source);
        string original = Path.Combine(nested, "fixture.flac");
        string preview;
        await using (var stage = await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "Abandoned preview")))
        {
            File.Copy(source, original);
            preview = Path.Combine(nested, Path.GetFileName(stage.StagedFile.LocalPath));
            File.Copy(stage.StagedFile.LocalPath, preview);
            File.Copy(Path.ChangeExtension(stage.StagedFile.LocalPath, ".json"), Path.ChangeExtension(preview, ".json"));
        }
        byte[] hash = SHA256.HashData(File.ReadAllBytes(original));
        Open(); await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media);
        window.FindControl<Button>("OpenPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
        PreferencesDialog preferences = window.OwnedWindows.OfType<PreferencesDialog>().Single();
        preferences.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
        preferences.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PreferencesMetadataRecovery")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => preferences.OwnedWindows.OfType<MetadataRecoveryDialog>().Any());
        MetadataRecoveryDialog recovery = preferences.OwnedWindows.OfType<MetadataRecoveryDialog>().Single();
        await UntilAsync(() => recovery.GetVisualDescendants().OfType<ListBox>().Single().ItemCount == 1 && !viewModel.IsBusy);
        recovery.GetVisualDescendants().OfType<ListBox>().Single().SelectedIndex = 0;
        recovery.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DiscardMetadataPreview")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => recovery.OwnedWindows.Any());
        recovery.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmDiscardMetadataPreview")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !File.Exists(preview) && !viewModel.IsBusy);
        Assert.That(File.Exists(Path.ChangeExtension(preview, ".json")), Is.False);
        Assert.That(SHA256.HashData(File.ReadAllBytes(original)), Is.EqualTo(hash));
        Assert.That((await repository.ListAsync()).Single().Title, Is.EqualTo("Fixture Tone"));
        recovery.Close();
        await UntilAsync(() => preferences.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SavePreferences").IsEnabled);
        preferences.Close();
    }

    [AvaloniaTest]
    public async Task MainWindow_ClosingDuringRecoveryDialog_CancelsScanAndClosesOwnedWindows()
    {
        directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"aibell-recovery-close-{Guid.NewGuid():N}")).FullName;
        BlockingMetadataRecovery recovery = new();
        Open(recovery: recovery); await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(directory);
        window.FindControl<Button>("OpenPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
        PreferencesDialog preferences = window.OwnedWindows.OfType<PreferencesDialog>().Single();
        preferences.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
        preferences.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PreferencesMetadataRecovery")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => recovery.Entered);
        window.Close();
        await UntilAsync(() => !window.IsVisible);
        Assert.That(recovery.Cancelled, Is.True);
        Assert.That(preferences.IsVisible, Is.False);
        Assert.That(viewModel.IsBusy, Is.False);
    }

    private sealed class BlockingMetadataRecovery : IMetadataEditRecovery
    {
        public bool Entered { get; private set; }
        public bool Cancelled { get; private set; }
        public Task<MetadataEditRecoveryScan> ScanAsync(IReadOnlyCollection<string> folders, CancellationToken token = default) => ScanRecursivelyAsync(folders, token);
        public async Task<MetadataEditRecoveryScan> ScanRecursivelyAsync(IReadOnlyCollection<string> folders, CancellationToken token = default)
        {
            Entered = true;
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new([], []);
        }
        public Task DiscardAsync(PendingMetadataEdit edit, CancellationToken token = default) => Task.CompletedTask;
    }

    [AvaloniaTest]
    public async Task MainWindow_SystemMediaCommands_ControlActiveTrackAndRejectStaleSeek()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-media-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        foreach (string name in new[] { "first", "second" })
            File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), Path.Combine(media, name + ".flac"));
        TestMediaSession session = new(); Open(mediaSession: session);
        await UntilAsync(() => session.Controller is not null && !viewModel.IsBusy);
        await viewModel.ImportAsync(media);
        viewModel.SelectedTrack = viewModel.Tracks[0];
        await viewModel.TogglePlaybackAsync();
        Guid playing = engine.Snapshot.Track!.Id;
        viewModel.SelectedTrack = viewModel.Tracks[1];
        IMediaSessionController controls = session.Controller!;
        await Task.Run(() => controls.ExecuteAsync(new(MediaSessionAction.Pause)));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Paused));
        Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(playing));
        Assert.That(controls.State.CanGoNext, Is.False);
        await controls.ExecuteAsync(new(MediaSessionAction.Next));
        await controls.ExecuteAsync(new(MediaSessionAction.Seek, Position: long.MaxValue));
        Assert.That(engine.Snapshot.Track.Id, Is.EqualTo(playing));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Paused));
        await controls.ExecuteAsync(new(MediaSessionAction.Play));
        Assert.That(engine.Snapshot.Track.Id, Is.EqualTo(playing));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        await controls.ExecuteAsync(new(MediaSessionAction.SetPosition, TrackId: Guid.NewGuid(), Position: 500_000));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
        await controls.ExecuteAsync(new(MediaSessionAction.SetPosition, TrackId: playing, Position: 500_000));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        await controls.ExecuteAsync(new(MediaSessionAction.Seek, Position: long.MinValue));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
        await controls.ExecuteAsync(new(MediaSessionAction.SetShuffle, Shuffle: true));
        await controls.ExecuteAsync(new(MediaSessionAction.SetShuffle, Shuffle: true));
        await controls.ExecuteAsync(new(MediaSessionAction.SetRepeat, Repeat: RepeatMode.One));
        await controls.ExecuteAsync(new(MediaSessionAction.SetVolume, Volume: 0.42));
        Assert.That(controls.State.Shuffle, Is.True);
        Assert.That(controls.State.Repeat, Is.EqualTo(RepeatMode.One));
        Assert.That(controls.State.Playback.Volume, Is.EqualTo(0.42));
        await CloseAsync();
        Assert.That(session.Disposed, Is.True);
        Assert.That(async () => await controls.ExecuteAsync(new(MediaSessionAction.Play)), Throws.TypeOf<ObjectDisposedException>());
    }

    [AvaloniaTest]
    public async Task MainWindow_UnavailableSystemMediaBus_DoesNotBlockLibraryOrShutdown()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-media-unavailable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        TestMediaSession session = new() { Fail = true }; Open(mediaSession: session);
        await UntilAsync(() => session.Controller is not null && !viewModel.IsBusy);
        Assert.That(window.IsVisible, Is.True);
        Assert.That(viewModel.StatusMessage, Does.Not.Contain("media controls"));
        await CloseAsync();
        Assert.That(session.Disposed, Is.True);
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringMediaCommand_CancelsAndDrainsCommandBeforeDisposal()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-media-close-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), Path.Combine(media, "fixture.flac"));
        TestMediaSession session = new(); Open(mediaSession: session);
        await UntilAsync(() => session.Controller is not null && !viewModel.IsBusy);
        await viewModel.ImportAsync(media);
        viewModel.SelectedTrack = viewModel.Tracks.Single();
        await viewModel.TogglePlaybackAsync();
        await viewModel.StopPlaybackAsync();
        engine.PlayGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task command = Task.Run(() => session.Controller!.ExecuteAsync(new(MediaSessionAction.Play)));
        await UntilAsync(() => engine.PlayWaiting);
        Assert.That(command.IsCompleted, Is.False);
        window.Close();
        await UntilAsync(() => !window.IsVisible);
        Assert.That(async () => await command, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        Assert.That(session.Disposed, Is.True);
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringMediaStartup_CancelsConnectionPromptly()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-media-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        TestMediaSession session = new() { BlockStartup = true }; Open(mediaSession: session);
        await UntilAsync(() => session.Controller is not null && !viewModel.IsBusy);
        await CloseAsync();
        Assert.That(session.StartupCancelled, Is.True);
        Assert.That(session.Disposed, Is.True);
    }

    [AvaloniaTest]
    public async Task MainWindow_PlaylistFileReviewImportExportAndRestart_PreservesOrderAndAudio()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-playlist-file-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string first = Path.Combine(media, "first.flac"), second = Path.Combine(media, "second.flac");
        foreach (string path in new[] { first, second }) File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), path);
        byte[] original = SHA256.HashData(await File.ReadAllBytesAsync(first));
        string source = Path.Combine(directory, "Review.m3u8");
        await File.WriteAllTextAsync(source, "music/second.flac\nmissing.flac\nmusic/first.flac\nmusic/second.flac\n");
        Open(); await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Task<bool> cancelled = window.ShowPlaylistImportAsync(new Uri(source));
        await UntilAsync(() => window.OwnedWindows.OfType<PlaylistImportDialog>().Any());
        PlaylistImportDialog dialog = window.OwnedWindows.OfType<PlaylistImportDialog>().Single();
        await UntilAsync(() => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmPlaylistImport").IsEnabled);
        Assert.That(await repository.ListAsync(), Is.Empty);
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.That(await cancelled.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        Assert.That(await repository.ListAsync(), Is.Empty);
        Task<bool> imported = window.ShowPlaylistImportAsync(new Uri(source));
        await UntilAsync(() => window.OwnedWindows.OfType<PlaylistImportDialog>().Any());
        dialog = window.OwnedWindows.OfType<PlaylistImportDialog>().Single();
        await UntilAsync(() => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmPlaylistImport").IsEnabled);
        dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "ImportedPlaylistName").Text = "  Imported order  ";
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.That(await imported.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(viewModel.SelectedPlaylist!.Name, Is.EqualTo("Imported order"));
        Assert.That(viewModel.Tracks.Select(row => row.Source.Location), Is.EqualTo(new[] { new Uri(second), new Uri(first) }));
        Assert.That(viewModel.LibrarySources, Is.Empty);
        Guid id = viewModel.SelectedPlaylist.Id!.Value;
        await viewModel.SearchAsync("no matches");
        string exported = Path.Combine(directory, "export.m3u8");
        await viewModel.ExportPlaylistFileAsync(id, new Uri(exported));
        var content = await new PlaylistFileStore().ReadAsync(new Uri(exported));
        Assert.That(content.Entries.Select(entry => entry.Location), Is.EqualTo(new[] { new Uri(second), new Uri(first) }));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(first)), Is.EqualTo(original));
        Playlist smart = await repository.CreateSmartAsync("Smart export", new(SmartPlaylistMatch.All,
            [new(SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "Fixture")]));
        string smartFile = Path.Combine(directory, "smart.m3u8");
        await viewModel.ExportPlaylistFileAsync(smart.Id, new Uri(smartFile));
        Assert.That((await new PlaylistFileStore().ReadAsync(new Uri(smartFile))).Entries, Has.Count.EqualTo(2));
        await CloseAsync(); await ReleaseServicesAsync(); Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Any(playlist => playlist.Id == id));
        await viewModel.SelectPlaylistAsync(viewModel.Playlists.Single(playlist => playlist.Id == id));
        Assert.That(viewModel.Tracks.Select(row => row.Source.Location), Is.EqualTo(content.Entries.Select(entry => entry.Location)));
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringPlaylistReview_CancelsAndDrainsWithoutCreatingPlaylist()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-playlist-cancel-ui-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory);
        BlockingPlaylistFiles files = new(); Open(playlistStore: files);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Task<bool> review = window.ShowPlaylistImportAsync(new Uri(Path.Combine(directory, "review.m3u8")));
        await files.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.Close(); await UntilAsync(() => !window.IsVisible);
        Assert.That(await review.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        Assert.That(files.Cancelled, Is.True);
        Assert.That(await repository.ListAsync(), Is.Empty);
        Assert.That(await ((IPlaylistRepository)repository).ListAsync(), Is.Empty);
    }

    private sealed class BlockingPlaylistFiles : IPlaylistFileStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async Task<PlaylistFileContent> ReadAsync(Uri source, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException("The review should be cancelled.");
        }
        public Task WriteAsync(Uri destination, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [AvaloniaTest]
    public async Task MainWindow_TrackNotifications_DefaultOffAndOptIn_PreservesPreferenceAndAvoidsPauseDuplicates()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-notify-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        foreach (string name in new[] { "first", "second" })
            File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), Path.Combine(media, name + ".flac"));
        TestNotifications notifications = new(); Open(notifications: notifications);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0); await viewModel.ImportAsync(media);
        Track first = viewModel.Tracks[0].Source, second = viewModel.Tracks[1].Source;
        await viewModel.SaveTrackMetadataAsync(second.Id, TrackMetadata.FromTrack(second) with { Title = "Catalog notification title" });
        viewModel.SelectedTrack = viewModel.Tracks.Single(row => row.Source.Id == first.Id);
        await viewModel.TogglePlaybackAsync(); await UntilAsync(() => viewModel.IsPlaybackPlaying);
        Assert.That(viewModel.GetPreferences().ShowNotifications, Is.False); Assert.That(notifications.Notes, Is.Empty);
        window.FindControl<Button>("OpenPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
        PreferencesDialog preferences = window.OwnedWindows.OfType<PreferencesDialog>().Single();
        CheckBox notificationChoice = preferences.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => box.Name == "PreferencesNotifications");
        Assert.That(notificationChoice.IsEnabled, Is.True); notificationChoice.IsChecked = true;
        Activate(preferences.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SavePreferences"));
        await UntilAsync(() => !preferences.IsVisible);
        await viewModel.TogglePlaybackAsync(); await UntilAsync(() => !viewModel.IsPlaybackPlaying);
        await viewModel.TogglePlaybackAsync(); await UntilAsync(() => viewModel.IsPlaybackPlaying);
        Assert.That(notifications.Notes, Is.Empty);
        viewModel.QueueTracks([second]); await viewModel.MoveNextAsync();
        await UntilAsync(() => notifications.Notes.Count == 1); await viewModel.WaitForDesktopNotificationsAsync();
        Assert.That(notifications.Notes.Single().Title, Is.EqualTo("Catalog notification title"));
        await viewModel.TogglePlaybackAsync(); await viewModel.TogglePlaybackAsync(); Dispatcher.UIThread.RunJobs();
        Assert.That(notifications.Notes, Has.Count.EqualTo(1));
        Assert.That((await settings.LoadAsync()).ShowDesktopNotifications, Is.True);
        await CloseAsync(); await ReleaseServicesAsync(); Open(notifications: notifications);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Tracks.Count == 2);
        Assert.That(viewModel.GetPreferences().ShowNotifications, Is.True);
        Assert.That(notifications.Notes, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task MainWindow_NotificationFailure_DoesNotInterruptPlaybackOrShowAnApplicationError()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-notify-failure-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), Path.Combine(media, "fixture.flac"));
        TestNotifications notifications = new() { Fail = true }; Open(notifications: notifications);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0); await viewModel.ImportAsync(media);
        await viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { ShowNotifications = true });
        viewModel.SelectedTrack = viewModel.Tracks.Single(); await viewModel.TogglePlaybackAsync();
        await UntilAsync(() => notifications.Notes.Count == 1); await viewModel.WaitForDesktopNotificationsAsync();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        Assert.That(viewModel.StatusMessage, Does.Not.Contain("notification service"));
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseWithNotificationPending_CancelsAndDrainsBeforeDisposing()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-notify-close-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), Path.Combine(media, "fixture.flac"));
        TestNotifications notifications = new() { Block = true }; Open(notifications: notifications);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0); await viewModel.ImportAsync(media);
        await viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { ShowNotifications = true });
        viewModel.SelectedTrack = viewModel.Tracks.Single(); await viewModel.TogglePlaybackAsync();
        await notifications.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.Close(); await UntilAsync(() => !window.IsVisible);
        Assert.That(notifications.Cancelled, Is.True);
        Assert.That(viewModel.WaitForDesktopNotificationsAsync().IsCompleted, Is.True);
    }

    [AvaloniaTest]
    public async Task MainWindow_Radio_SavePlayLiveEndAndRestart_PreservesStationsWithoutCatalogOrNetworkOnSave()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-ui-{Guid.NewGuid():N}"); Open(radio: true);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Activate(window.FindControl<Button>("SelectRadioButton")!);
        await UntilAsync(() => viewModel.IsRadioView && !viewModel.IsBusy);
        Activate(window.FindControl<Button>("AddRadioStationButton")!);
        await UntilAsync(() => window.OwnedWindows.OfType<RadioStationDialog>().Any());
        RadioStationDialog dialog = window.OwnedWindows.OfType<RadioStationDialog>().Single();
        dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "RadioStationName").Text = "Synthetic Radio";
        TextBox url = dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "RadioStationUrl");
        url.Text = "file:///tmp/private.mp3"; Dispatcher.UIThread.RunJobs();
        Assert.That(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveRadioStation").IsEnabled, Is.False);
        url.Text = "https://radio.example.invalid/live";
        dialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 4;
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        await UntilAsync(() => !dialog.IsVisible && viewModel.RadioStations.Count == 1);
        Assert.That(engine.LoadCount, Is.Zero); Assert.That(await repository.ListAsync(), Is.Empty);
        RadioStation station = viewModel.RadioStations.Single();
        Track pending = new(Guid.NewGuid(), new Uri("file:///music/pending.flac"), "Pending", "Artist", "Album", TimeSpan.FromMinutes(1));
        viewModel.QueueTracks([pending]); Guid request = viewModel.QueueItems.Single().Id;
        ListBox list = window.FindControl<ListBox>("RadioStationList")!; Dispatcher.UIThread.RunJobs();
        Assert.That(list.GetVisualDescendants().OfType<ListBoxItem>().Single().Focus(), Is.True);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && !viewModel.IsBusy);
        await viewModel.WaitForPlaybackOperationsAsync(); Dispatcher.UIThread.RunJobs();
        Assert.That(viewModel.DurationText, Is.EqualTo("LIVE")); Assert.That(viewModel.ShowRadioStop, Is.True);
        await viewModel.ExecuteMediaSessionCommandAsync(new(MediaSessionAction.Pause));
        await viewModel.ExecuteMediaSessionCommandAsync(new(MediaSessionAction.PlayPause));
        Assert.That(engine.LoadCount, Is.EqualTo(1)); Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        Assert.That(window.FindControl<Grid>("PositionSeekSurface")!.IsEnabled, Is.False);
        Assert.That(viewModel.GetMediaSessionState().CanPause, Is.False); Assert.That(viewModel.GetMediaSessionState().CanSeek, Is.False);
        await engine.SeekAsync(TimeSpan.FromSeconds(25)); await viewModel.SeekAsync(1);
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.FromSeconds(25)));
        await viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { Repeat = RepeatMode.One });
        engine.End(); await UntilAsync(() => viewModel.StatusMessage.StartsWith("Radio stream ended", StringComparison.Ordinal));
        Assert.That(engine.LoadCount, Is.EqualTo(1)); Assert.That(viewModel.QueueItems.Single().Id, Is.EqualTo(request));
        await viewModel.TogglePlaybackAsync(); await viewModel.WaitForPlaybackOperationsAsync();
        await viewModel.TogglePlaybackAsync(); await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Stopped);
        Assert.That((await repository.ListStationsAsync()).Single().PlayCount, Is.EqualTo(2));
        await CloseAsync(); await ReleaseServicesAsync(); Open(radio: true);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Activate(window.FindControl<Button>("SelectRadioButton")!); await UntilAsync(() => viewModel.IsRadioView && !viewModel.IsBusy);
        Assert.That(viewModel.RadioStations.Single().Id, Is.EqualTo(station.Id));
        Assert.That(viewModel.RadioStations.Single().Rating, Is.EqualTo(4));
        Assert.That(engine.LoadCount, Is.Zero); Assert.That(await repository.ListAsync(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_Radio_EditReconnectAndConfirmedRemove_LeavesMusicAndPendingQueueUntouched()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-edit-ui-{Guid.NewGuid():N}"); Open(radio: true);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        RadioStation station = new(Guid.NewGuid(), "Original", new Uri("https://radio.example.invalid/original"));
        await viewModel.SaveRadioStationAsync(station, true); await viewModel.SelectRadioAsync();
        await viewModel.PlayRadioStationAsync(station.Id); Dispatcher.UIThread.RunJobs();
        Activate(window.FindControl<Button>("EditRadioStationButton")!);
        await UntilAsync(() => window.OwnedWindows.OfType<RadioStationDialog>().Any());
        RadioStationDialog edit = window.OwnedWindows.OfType<RadioStationDialog>().Single();
        edit.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "RadioStationName").Text = "Edited";
        edit.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "RadioStationUrl").Text = "https://radio.example.invalid/edited";
        edit.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        await UntilAsync(() => !edit.IsVisible);
        Assert.That(engine.Snapshot.Track!.Location, Is.EqualTo(station.Stream));
        await viewModel.ExecuteMediaSessionCommandAsync(new(MediaSessionAction.Stop));
        await viewModel.ExecuteMediaSessionCommandAsync(new(MediaSessionAction.Play)); Dispatcher.UIThread.RunJobs();
        Assert.That(engine.Snapshot.Track!.Location.AbsolutePath, Is.EqualTo("/edited"));
        Assert.That((await repository.ListStationsAsync()).Single().PlayCount, Is.EqualTo(2));
        Activate(window.FindControl<Button>("RemoveRadioStationButton")!);
        await UntilAsync(() => window.OwnedWindows.Any());
        window.OwnedWindows.Single().KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await UntilAsync(() => !window.OwnedWindows.Any());
        Assert.That(await repository.ListStationsAsync(), Has.Count.EqualTo(1));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing));
        Activate(window.FindControl<Button>("RemoveRadioStationButton")!); await UntilAsync(() => window.OwnedWindows.Any());
        window.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmRemoveRadioStation")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !viewModel.IsBusy && viewModel.RadioStations.Count == 0);
        Assert.That(await repository.ListStationsAsync(), Is.Empty); Assert.That(await repository.ListAsync(), Is.Empty);
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        int loads = engine.LoadCount;
        await viewModel.ExecuteMediaSessionCommandAsync(new(MediaSessionAction.Play));
        Assert.That(engine.LoadCount, Is.EqualTo(loads));
    }

    [AvaloniaTest]
    public async Task MainWindow_Radio_CloseDuringConnection_DrainsBeforeStorageDisposal()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-close-ui-{Guid.NewGuid():N}"); Open(radio: true);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        RadioStation station = new(Guid.NewGuid(), "Blocked", new Uri("https://radio.example.invalid/live"));
        await viewModel.SaveRadioStationAsync(station, true); await viewModel.SelectRadioAsync();
        engine.PlayGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task play = viewModel.PlayRadioStationAsync(station.Id); await UntilAsync(() => engine.PlayWaiting);
        window.Close(); Dispatcher.UIThread.RunJobs();
        try { Assert.That(window.IsVisible, Is.True); Assert.That(viewModel.CanManageRadio, Is.False); }
        finally { engine.PlayGate.TrySetResult(); }
        await play; await UntilAsync(() => !window.IsVisible);
        Assert.That((await repository.ListStationsAsync()).Single().PlayCount, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task MainWindow_Radio_RemoveWhileNativeConnectionIsOpening_StopsTheConnection()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-opening-ui-{Guid.NewGuid():N}"); Open(radio: true);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        RadioStation station = new(Guid.NewGuid(), "Opening", new Uri("https://radio.example.invalid/live"));
        await viewModel.SaveRadioStationAsync(station, true); await viewModel.SelectRadioAsync();
        engine.OpeningOnly = true; engine.SuppressUnchanged = true;
        await viewModel.PlayRadioStationAsync(station.Id); Dispatcher.UIThread.RunJobs();
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
        Assert.That(viewModel.CanStop, Is.True); Assert.That(viewModel.ShowRadioStop, Is.True);
        Assert.That(viewModel.ShowPlaybackPlay, Is.False); Assert.That(viewModel.StatusMessage, Does.StartWith("Connecting"));
        await viewModel.ExecuteMediaSessionCommandAsync(new(MediaSessionAction.Play));
        Assert.That(engine.LoadCount, Is.EqualTo(1));
        await viewModel.RemoveRadioStationAsync(station.Id); Dispatcher.UIThread.RunJobs();
        Assert.That(engine.StopCount, Is.EqualTo(1)); Assert.That(viewModel.CanStop, Is.False);
        Assert.That(viewModel.PlaybackButtonText, Is.EqualTo("Play")); Assert.That(viewModel.ShowRadioStop, Is.False);
        Assert.That(await repository.ListStationsAsync(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringFolderImport_DrainsTheCatalogAndKeepsSourceBytes()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-import-close-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string source = Path.Combine(media, "fixture.flac");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), source);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        BlockingMetadataReader reader = new(); Open(metadataReader: reader);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Task import = viewModel.ImportAsync(media); await UntilAsync(() => reader.Started);
        window.Close(); Dispatcher.UIThread.RunJobs();
        try { Assert.That(window.IsVisible, Is.True, "An accepted import must finish before storage is disposed."); }
        finally { reader.Release.TrySetResult(); await import; await viewModel.FlushPreferencesAsync(); }
        await UntilAsync(() => !window.IsVisible);
        Assert.That((await repository.ListAsync()).Single().Location.LocalPath, Is.EqualTo(source));
        Assert.That(SHA256.HashData(File.ReadAllBytes(source)), Is.EqualTo(hash));
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringSearch_DrainsItAndRejectsLaterQueries()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-search-close-ui-{Guid.NewGuid():N}");
        BlockingLibraryBrowser? browser = null;
        Open(browserFactory: source => browser = new(source));
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        browser!.Block = true;
        Task search = viewModel.SearchAsync("held"); await UntilAsync(() => browser.Started);
        window.Close(); Dispatcher.UIThread.RunJobs();
        Task later = viewModel.SearchAsync("ignored-after-close");
        try
        {
            Assert.That(window.IsVisible, Is.True);
            Assert.That(later.IsCompleted, Is.True);
            Assert.That(viewModel.SearchQuery, Is.EqualTo("held"));
        }
        finally { browser.Release.TrySetResult(); await Task.WhenAll(search, later); }
        await UntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringPreferenceApplication_DrainsEngineAndAtomicSettingsSave()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-prefs-close-ui-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), Path.Combine(media, "fixture.flac"));
        Open(); await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media); viewModel.SelectedTrack = viewModel.Tracks.Single();
        await viewModel.TogglePlaybackAsync();
        engine.VolumeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task save = viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { Volume = 0.42 });
        await UntilAsync(() => engine.VolumeWaiting);
        window.Close(); Dispatcher.UIThread.RunJobs();
        try { Assert.That(window.IsVisible, Is.True); }
        finally { engine.VolumeGate.TrySetResult(); await save; }
        await UntilAsync(() => !window.IsVisible);
        Assert.That((await settings.LoadAsync()).Volume, Is.EqualTo(0.42));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task MainWindow_CloseDuringFailedOrCancelledSearch_ObservesTheTaskAndStillCloses(bool cancel)
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-failed-search-close-{Guid.NewGuid():N}");
        BlockingLibraryBrowser? browser = null; Open(browserFactory: source => browser = new(source));
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        using CancellationTokenSource cancellation = new();
        browser!.Block = true;
        Task search = viewModel.SearchAsync("held", cancellation.Token); await UntilAsync(() => browser.Started);
        window.Close(); Dispatcher.UIThread.RunJobs(); Assert.That(window.IsVisible, Is.True);
        if (cancel) cancellation.Cancel();
        else { browser.Failure = new IOException("Synthetic query failure"); browser.Release.TrySetResult(); }
        Exception? failure = null;
        try { await search; } catch (Exception exception) { failure = exception; }
        if (cancel) Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
        else Assert.That(failure, Is.TypeOf<IOException>());
        browser.Release.TrySetResult(); await UntilAsync(() => !window.IsVisible);
        Assert.That(await repository.ListAsync(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_SearchFailure_ShowsTheErrorAndAcceptsTheNextQuery()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-query-error-ui-{Guid.NewGuid():N}");
        BlockingLibraryBrowser? browser = null; Open(browserFactory: source => browser = new(source));
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        browser!.Block = true; browser.Failure = new IOException("Synthetic query failure");
        List<Exception> unhandled = [];
        void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs args)
        { unhandled.Add(args.Exception); args.Handled = true; }
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            window.FindControl<TextBox>("SearchBox")!.Text = "failing query";
            await UntilAsync(() => browser.Started); browser.Release.TrySetResult();
            await viewModel.WaitForLibraryOperationsAsync(); await PumpAsync();
            Assert.That(unhandled, Is.Empty);
            Assert.That(viewModel.StatusMessage, Does.Contain("Synthetic query failure"));
            browser.Block = false; browser.Failure = null;
            window.FindControl<TextBox>("SearchBox")!.Text = "recovered query";
            await UntilAsync(() => viewModel.SearchQuery == "recovered query");
            Assert.That(window.IsVisible, Is.True);
        }
        finally { browser.Release.TrySetResult(); Dispatcher.UIThread.UnhandledException -= OnUnhandled; }
    }

    [AvaloniaTest]
    [TestCase("folder")]
    [TestCase("playlist")]
    [TestCase("export")]
    public async Task MainWindow_NativePickerFailure_IsReportedAndRetryRemainsPossible(string action)
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-picker-error-ui-{Guid.NewGuid():N}");
        DelayedFilePicker picker = new(new Uri(Path.Combine(directory, "picked.m3u8")))
        { Failure = new IOException("Synthetic picker failure") };
        Open(filePicker: picker);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        if (action == "export")
        {
            viewModel.NewPlaylistName = "Export test"; await viewModel.CreatePlaylistAsync();
            await UntilAsync(() => viewModel.SelectedPlaylist?.Name == "Export test");
        }
        Control control = PickerActionControl(action);
        List<Exception> unhandled = [];
        void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs args)
        { unhandled.Add(args.Exception); args.Handled = true; }
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
            await UntilAsync(() => picker.Started); picker.Release.TrySetResult(); await PumpAsync();
            Assert.That(unhandled, Is.Empty);
            Assert.That(viewModel.StatusMessage, Does.Contain("Synthetic picker failure"));
            Assert.That(window.IsVisible, Is.True); Assert.That(viewModel.IsBusy, Is.False);
            picker.Failure = null; picker.Cancelled = true;
            control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
            await UntilAsync(() => picker.Calls == 2); await PumpAsync();
            Assert.That(unhandled, Is.Empty); Assert.That(viewModel.IsBusy, Is.False);
            Assert.That(window.OwnedWindows, Is.Empty);
        }
        finally { picker.Release.TrySetResult(); Dispatcher.UIThread.UnhandledException -= OnUnhandled; }
    }

    [AvaloniaTest]
    [TestCase("folder", false)]
    [TestCase("playlist", false)]
    [TestCase("export", false)]
    [TestCase("folder", true)]
    [TestCase("playlist", true)]
    [TestCase("export", true)]
    public async Task MainWindow_NativePickerReturnsAfterClose_IgnoresTheResultAndKeepsFilesUntouched(string action, bool fail)
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-picker-close-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string selected = action == "folder" ? Directory.CreateDirectory(Path.Combine(directory, "music")).FullName : Path.Combine(directory, "picked.m3u8");
        string sentinel = Path.Combine(directory, "picked.m3u8"); File.WriteAllText(sentinel, "#EXTM3U\n# preserved fixture\n");
        byte[] original = File.ReadAllBytes(sentinel);
        DelayedFilePicker picker = new(new Uri(selected)); Open(filePicker: picker);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        if (action == "export")
        {
            viewModel.NewPlaylistName = "Export test"; await viewModel.CreatePlaylistAsync();
            await UntilAsync(() => viewModel.SelectedPlaylist?.Name == "Export test");
        }
        List<Exception> unhandled = [];
        void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs args)
        { unhandled.Add(args.Exception); args.Handled = true; }
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            Control control = PickerActionControl(action);
            control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
            await UntilAsync(() => picker.Started);
            window.Close(); await UntilAsync(() => !window.IsVisible);
            string status = viewModel.StatusMessage;
            if (fail) picker.Failure = new IOException("Late synthetic picker failure");
            picker.Release.TrySetResult(); await PumpAsync();
            Assert.That(unhandled, Is.Empty); Assert.That(window.OwnedWindows, Is.Empty);
            Assert.That(viewModel.StatusMessage, Is.EqualTo(status));
            Assert.That(await repository.ListAsync(), Is.Empty);
            Assert.That(File.ReadAllBytes(sentinel), Is.EqualTo(original));
        }
        finally { picker.Release.TrySetResult(); Dispatcher.UIThread.UnhandledException -= OnUnhandled; }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task MainWindow_PreferencesPickerReturnsAfterOwnerClose_DoesNotImportOrReopen(bool fail)
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-preferences-picker-close-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        DelayedFilePicker picker = new(new Uri(media)); Open(filePicker: picker);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Activate(window.FindControl<Button>("OpenPreferencesButton")!);
        await UntilAsync(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
        PreferencesDialog preferences = window.OwnedWindows.OfType<PreferencesDialog>().Single();
        preferences.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
        Activate(preferences.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PreferencesAddFolder"));
        await UntilAsync(() => picker.Started);
        try { window.Close(); await UntilAsync(() => !window.IsVisible); }
        finally
        {
            if (fail) picker.Failure = new IOException("Late preferences picker failure");
            picker.Release.TrySetResult();
        }
        await PumpAsync();
        Assert.That(preferences.IsVisible, Is.False);
        Assert.That(await ((ILibrarySourceRepository)repository).ListAsync(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_PreferencesPicker_ImportsTheChosenFolderAndKeepsSourceBytes()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-preferences-picker-import-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        string source = Path.Combine(media, "fixture.flac");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), source);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        DelayedFilePicker picker = new(new Uri(media)); Open(filePicker: picker);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Activate(window.FindControl<Button>("OpenPreferencesButton")!);
        await UntilAsync(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
        PreferencesDialog preferences = window.OwnedWindows.OfType<PreferencesDialog>().Single();
        preferences.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
        Activate(preferences.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PreferencesAddFolder"));
        await UntilAsync(() => picker.Started); picker.Release.TrySetResult();
        await UntilAsync(() => viewModel.LibrarySources.Count == 1 && viewModel.Tracks.Count == 1 && !viewModel.IsBusy);
        Assert.That((await repository.ListAsync()).Single().Location.LocalPath, Is.EqualTo(source));
        Assert.That(SHA256.HashData(File.ReadAllBytes(source)), Is.EqualTo(original));
    }

    private Control PickerActionControl(string action)
    {
        if (action != "export")
        {
            string tip = action switch { "folder" => "Add music folder", _ => "Import playlist file" };
            return window.GetVisualDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), tip));
        }
        Border row = window.FindControl<ListBox>("PlaylistList")!.GetVisualDescendants().OfType<Border>()
            .First(border => border.ContextMenu is not null && ReferenceEquals(border.DataContext, viewModel.SelectedPlaylist));
        return row.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Export playlist…"));
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioKeyboard_SpaceStopsLivePlaybackAndFindClearsStationSearch()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-keyboard-ui-{Guid.NewGuid():N}"); Open(radio: true);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        RadioStation station = new(Guid.NewGuid(), "Keyboard station", new Uri("https://radio.example.invalid/live"));
        await viewModel.SaveRadioStationAsync(station, true); await viewModel.SelectRadioAsync();
        viewModel.SelectedRadioStation = viewModel.RadioStations.Single();
        ListBox list = window.FindControl<ListBox>("RadioStationList")!; list.UpdateLayout();
        Assert.That(list.GetVisualDescendants().OfType<ListBoxItem>().Single().Focus(), Is.True);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing);
        Button play = window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("play"));
        await viewModel.WaitForPlaybackOperationsAsync(); Dispatcher.UIThread.RunJobs();
        Assert.That(Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(play)!.GetName(), Is.EqualTo("Stop radio"));
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Stopped);
        Assert.That(engine.StopCount, Is.EqualTo(1));
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.F, RawInputModifiers.Control);
        // In Radio, Find goes to the search box inside the Radio area; the toolbar box mirrors it.
        TextBox search = window.FindControl<TextBox>("RadioSearchBox")!; Assert.That(search.IsFocused, Is.True);
        search.Text = "no matching station"; await UntilAsync(() => viewModel.RadioStations.Count == 0);
        Assert.That(window.FindControl<TextBox>("SearchBox")!.Text, Is.EqualTo("no matching station"));
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await UntilAsync(() => viewModel.RadioStations.Count == 1);
        Assert.That(await repository.ListAsync(), Is.Empty);
        Assert.That((await repository.ListStationsAsync()).Single().PlayCount, Is.EqualTo(1));
    }

    private static async Task PumpAsync()
    {
        for (int index = 0; index < 10; index++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class BlockingMetadataReader : IMediaMetadataReader
    {
        public bool Started { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default)
        { Started = true; await Release.Task.WaitAsync(cancellationToken); return await new AtlMediaMetadataReader().ReadAsync(location, cancellationToken); }
    }

    private sealed class BlockingLibraryBrowser(ILibraryBrowser inner) : ILibraryBrowser
    {
        public bool Block { get; set; }
        public bool Started { get; private set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<GenreSummary>> ListGenresAsync(CancellationToken token = default) => inner.ListGenresAsync(token);
        public Task<IReadOnlyList<ArtistSummary>> ListArtistsAsync(string? genre, CancellationToken token = default) => inner.ListArtistsAsync(genre, token);
        public Task<IReadOnlyList<AlbumSummary>> ListAlbumsAsync(string? genre, string? artist, CancellationToken token = default) => inner.ListAlbumsAsync(genre, artist, token);
        public async Task<IReadOnlyList<Track>> GetTracksAsync(string? genre, string? artist, string? album, string query, CancellationToken token = default)
        {
            if (Block) { Started = true; await Release.Task.WaitAsync(token); }
            if (Failure is not null) throw Failure;
            return await inner.GetTracksAsync(genre, artist, album, query, token);
        }
    }

    private sealed class TestNotifications : IDesktopNotifications
    {
        public List<NowPlayingNotification> Notes { get; } = [];
        public bool Fail { get; init; }
        public bool Block { get; init; }
        public bool Cancelled { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ShowAsync(NowPlayingNotification notification, CancellationToken cancellationToken = default)
        {
            Notes.Add(notification); Started.TrySetResult();
            if (Fail) throw new IOException("Test notification service unavailable");
            if (Block)
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }

    [AvaloniaTest]
    public async Task MainWindow_MediaSessionBackgroundStateRead_UsesPublishedSnapshot()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-media-snapshot-ui-{Guid.NewGuid():N}");
        TestMediaSession session = new();
        Open(mediaSession: session);
        await UntilAsync(() => session.Controller is not null);
        engine.RequireUiSnapshotRead = true;
        MediaSessionState initial = await Task.Run(() => session.Controller!.State);
        Assert.That(initial.Playback.Track, Is.Null);
        await viewModel.ChangeVolumeAsync(0.35);
        await UntilAsync(() => session.Controller!.State.Playback.Volume == 0.35);
        MediaSessionState changed = await Task.Run(() => session.Controller!.State);
        Assert.That(changed.Playback.Volume, Is.EqualTo(0.35));
        Assert.That(initial.Playback.Volume, Is.EqualTo(0.8), "Published state remains an immutable snapshot.");
        await CloseAsync();
    }

    private sealed class TestMediaSession : IMediaSession
    {
        public IMediaSessionController? Controller { get; private set; }
        public bool Disposed { get; private set; }
        public bool Fail { get; init; }
        public bool BlockStartup { get; init; }
        public bool StartupCancelled { get; private set; }
        public async Task StartAsync(IMediaSessionController controller, CancellationToken cancellationToken = default)
        {
            Controller = controller;
            if (Fail) throw new IOException("No private test bus");
            if (BlockStartup)
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { StartupCancelled = true; throw; }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioPlaylistPickerReviewAndImport_StaysOfflineAndPersistsAfterRestart()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-playlist-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "stations.m3u8");
        await File.WriteAllTextAsync(source, "#EXTINF:-1,Imported radio\nhttps://radio.example.invalid/live\nhttps://radio.example.invalid/live\nfile:///tmp/no.flac\n");
        byte[] original = await File.ReadAllBytesAsync(source);
        DelayedFilePicker picker = new(new Uri(source));
        Open(radio: true, filePicker: picker);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Activate(window.FindControl<Button>("SelectRadioButton")!);
        await UntilAsync(() => viewModel.IsRadioView && !viewModel.IsBusy);
        Activate(window.FindControl<Button>("ImportRadioPlaylistButton")!);
        await UntilAsync(() => picker.Started);
        picker.Release.TrySetResult();
        await UntilAsync(() => window.OwnedWindows.OfType<RadioPlaylistImportDialog>().Any());
        RadioPlaylistImportDialog dialog = window.OwnedWindows.OfType<RadioPlaylistImportDialog>().Single();
        Button accept = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmRadioImport");
        await UntilAsync(() => accept.IsEnabled);
        Assert.Multiple(() =>
        {
            Assert.That(engine.LoadCount, Is.Zero);
            Assert.That(viewModel.RadioStations, Is.Empty);
            Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RadioImportSummary").Text,
                Does.Contain("1 skipped; 1 duplicate"));
        });
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        await UntilAsync(() => !dialog.IsVisible && !viewModel.IsBusy && viewModel.RadioStations.Count == 1);
        Guid stationId = viewModel.RadioStations.Single().Id;
        Assert.That(engine.LoadCount, Is.Zero);
        Assert.That(await repository.ListAsync(), Is.Empty);
        await CloseAsync(); await ReleaseServicesAsync();
        Open(radio: true);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.SelectRadioAsync();
        Assert.That(viewModel.RadioStations.Single().Id, Is.EqualTo(stationId));
        Assert.That(viewModel.RadioStations.Single().PlayCount, Is.Zero);
        Assert.That(engine.LoadCount, Is.Zero);
        Assert.That(await File.ReadAllBytesAsync(source), Is.EqualTo(original));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task MainWindow_RadioPlaylistPickerReturnsAfterClose_IgnoresLateResultOrFailure(bool fail)
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-picker-close-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "stations.pls");
        await File.WriteAllTextAsync(source, "[playlist]\nFile1=https://radio.example.invalid/live\n");
        DelayedFilePicker picker = new(new Uri(source));
        Open(radio: true, filePicker: picker);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.SelectRadioAsync();
        Activate(window.FindControl<Button>("ImportRadioPlaylistButton")!);
        await UntilAsync(() => picker.Started);
        window.Close(); await UntilAsync(() => !window.IsVisible);
        string status = viewModel.StatusMessage;
        if (fail) picker.Failure = new IOException("Late picker failure");
        picker.Release.TrySetResult();
        await PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(window.OwnedWindows, Is.Empty);
            Assert.That(viewModel.StatusMessage, Is.EqualTo(status));
            Assert.That(engine.LoadCount, Is.Zero);
        });
        Assert.That(await repository.ListStationsAsync(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringRadioPlaylistPreparation_CancelsReaderAndDrainsBeforeDisposal()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-read-close-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        BlockingRadioPlaylistStore reader = new();
        Open(radio: true, radioPlaylistStore: reader);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Task<bool> review = window.ShowRadioPlaylistImportAsync(new Uri(Path.Combine(directory, "pending.m3u")));
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.Close();
        await UntilAsync(() => !window.IsVisible);
        Assert.Multiple(() =>
        {
            Assert.That(reader.Cancelled, Is.True);
            Assert.That(engine.LoadCount, Is.Zero);
            Assert.That(viewModel.CanImportRadioPlaylist, Is.False);
        });
        Assert.That(await review, Is.False);
        Assert.That(await repository.ListStationsAsync(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringAcceptedRadioImport_DrainsSaveBeforeDisposalAndRejectsLaterImports()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-save-close-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "stations.m3u");
        await File.WriteAllTextAsync(source, "#EXTINF:-1,Saved before close\nhttps://radio.example.invalid/live\n");
        byte[] original = await File.ReadAllBytesAsync(source);
        BlockingRadioImportRepository? blocked = null;
        Open(radio: true, radioRepositoryFactory: inner => blocked = new(inner));
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Task<bool> review = window.ShowRadioPlaylistImportAsync(new Uri(source));
        await UntilAsync(() => window.OwnedWindows.OfType<RadioPlaylistImportDialog>().Any());
        RadioPlaylistImportDialog dialog = window.OwnedWindows.OfType<RadioPlaylistImportDialog>().Single();
        Button accept = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmRadioImport");
        await UntilAsync(() => accept.IsEnabled);
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        await blocked!.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.Close(); Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(window.IsVisible, Is.True);
                Assert.That(dialog.IsVisible, Is.True);
                Assert.That(viewModel.CanImportRadioPlaylist, Is.False);
                Assert.That(blocked.Calls, Is.EqualTo(1));
            });
            Assert.That(await repository.ListStationsAsync(), Is.Empty);
        }
        finally { blocked.Release.TrySetResult(); }
        Assert.That(await review.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        await UntilAsync(() => !window.IsVisible);
        Assert.That((await repository.ListStationsAsync()).Single().Name, Is.EqualTo("Saved before close"));
        Assert.That(engine.LoadCount, Is.Zero);
        RadioPlaylistImportPreview later = new(new Uri(source), [], [], 0);
        await Assert.ThatAsync(async () => await viewModel.ImportRadioPlaylistAsync(later), Throws.TypeOf<InvalidOperationException>());
        Assert.That(blocked.Calls, Is.EqualTo(1));
        Assert.That(await File.ReadAllBytesAsync(source), Is.EqualTo(original));
    }

    private sealed class BlockingRadioImportRepository(IRadioStationRepository inner) : IRadioStationRepository
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async Task<RadioStationImportResult> ImportStationsAsync(IReadOnlyList<RadioStation> stations, CancellationToken cancellationToken = default)
        {
            Calls++; Started.TrySetResult(); await Release.Task;
            return await inner.ImportStationsAsync(stations, cancellationToken);
        }
        public Task<IReadOnlyList<RadioStation>> ListStationsAsync(CancellationToken cancellationToken = default) => inner.ListStationsAsync(cancellationToken);
        public Task AddStationAsync(RadioStation station, CancellationToken cancellationToken = default) => inner.AddStationAsync(station, cancellationToken);
        public Task UpdateStationAsync(RadioStation station, CancellationToken cancellationToken = default) => inner.UpdateStationAsync(station, cancellationToken);
        public Task RemoveStationAsync(Guid id, CancellationToken cancellationToken = default) => inner.RemoveStationAsync(id, cancellationToken);
        public Task RecordStationPlayedAsync(Guid id, DateTimeOffset occurredAt, CancellationToken cancellationToken = default) => inner.RecordStationPlayedAsync(id, occurredAt, cancellationToken);
    }

    private sealed class BlockingRadioPlaylistStore : IRadioPlaylistFileStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async Task<RadioPlaylistContent> ReadAsync(Uri source, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new([], []);
        }
    }

    private void Open(IMetadataFileWriter? fileWriter = null, IArtworkProvider? artwork = null, IMetadataEditRecovery? recovery = null, IMediaSession? mediaSession = null, IPlaylistFileStore? playlistStore = null, IDesktopNotifications? notifications = null, bool radio = false, IMediaMetadataReader? metadataReader = null, Func<ILibraryBrowser, ILibraryBrowser>? browserFactory = null, IDesktopFilePicker? filePicker = null, IRadioPlaylistFileStore? radioPlaylistStore = null, Func<IRadioStationRepository, IRadioStationRepository>? radioRepositoryFactory = null, Func<IPodcastPlaybackHandoff, PodcastsViewModel>? podcastViewModelFactory = null,
        Func<Aibell.Application.Audiobooks.IAudiobookPlaybackHandoff, AudiobooksViewModel>? audiobooksViewModelFactory = null,
        Func<Track, ArtworkWindow>? artworkWindowFactory = null, ICuratedStationCatalog? curatedCatalog = null, ICoverImageLoader? radioLogos = null)
    {
        repository = new(Path.Combine(directory, "library.db"));
        settings = new(Path.Combine(directory, "settings.json"));
        LibraryImportService importer = new(new FileSystemMediaDiscovery(), metadataReader ?? new AtlMediaMetadataReader(), repository);
        monitoring = new(repository, importer, NullLogger<LibraryMonitoringService>.Instance);
        engine = new();
        IRadioStationRepository radioPort = radioRepositoryFactory?.Invoke(repository) ?? repository;
        viewModel = new(importer, repository, browserFactory?.Invoke(repository) ?? repository, repository, repository, settings,
            monitoring, engine, artworkProvider: artwork,
            trackFileDetailsReader: new AtlTrackFileDetailsReader(), metadataFileWriter: fileWriter ?? new MetadataFileWriter(new AtlMetadataEditStager()), metadataEditRecovery: recovery ?? new MetadataEditRecovery(),
            playlistFiles: new PlaylistFileService(playlistStore ?? new PlaylistFileStore(), importer, repository, repository), desktopNotifications: notifications, radioRepository: radio ? radioPort : null,
            radioPlaylistFiles: radio ? new RadioPlaylistImportService(radioPlaylistStore ?? new RadioPlaylistFileStore(), radioPort) : null,
            curatedCatalog: curatedCatalog, radioLogos: radioLogos);
        window = new(viewModel, mediaSession, filePicker, podcastViewModelFactory, audiobooksViewModelFactory, artworkWindowFactory: artworkWindowFactory);
        window.Show();
    }

    private async Task CloseAsync()
    {
        foreach (Window owned in window.OwnedWindows.ToArray()) owned.Close();
        window.Close();
        await UntilAsync(() => !window.IsVisible);
    }

    private async Task ReleaseServicesAsync()
    {
        viewModel.Dispose();
        monitoring.Dispose();
        settings.Dispose();
        await repository.DisposeAsync();
    }

    private void Activate(Button button)
    {
        button.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class TestPlaybackEngine : IPlaybackEngine, IPlaybackRateControl, IEqualizerControl
    {
        public Aibell.Domain.Playback.EqualizerSettings Equalizer { get; private set; } = Aibell.Domain.Playback.EqualizerSettings.Flat;
        public int EqualizerCalls { get; private set; }
        public Task SetEqualizerAsync(Aibell.Domain.Playback.EqualizerSettings settings, CancellationToken cancellationToken = default) { Equalizer = settings; EqualizerCalls++; return Task.CompletedTask; }
        public double Rate { get; private set; } = 1.0;
        public Task SetRateAsync(double rate, CancellationToken cancellationToken = default) { Rate = rate; return Task.CompletedTask; }
        private PlaybackSnapshot snapshot = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8);
        public bool RequireUiSnapshotRead { get; set; }
        public PlaybackSnapshot Snapshot
        {
            get
            {
                if (RequireUiSnapshotRead) Dispatcher.UIThread.VerifyAccess();
                return snapshot;
            }
            private set => snapshot = value;
        }
        public List<string> Calls { get; } = [];
        public string? FailAt { get; set; }
        public int LoadCount { get; private set; }
        public bool OpeningOnly { get; set; }
        public bool SuppressUnchanged { get; set; }
        public int StopCount { get; private set; }
        public event EventHandler<PlaybackSnapshot>? StateChanged;
        public Task LoadAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls.Add("Load");
            if (FailAt == "Load") throw new InvalidOperationException("Load failed");
            LoadCount++;
            return Publish(Snapshot with { Track = track, Status = PlaybackStatus.Stopped, Position = TimeSpan.Zero });
        }
        public TaskCompletionSource? PlayGate { get; set; }
        public bool PlayWaiting { get; private set; }
        public async Task PlayAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Play");
            if (FailAt == "Play") throw new InvalidOperationException("Play failed");
            if (PlayGate is not null) { PlayWaiting = true; await PlayGate.Task.WaitAsync(cancellationToken); }
            if (!OpeningOnly) await Publish(Snapshot with { Status = PlaybackStatus.Playing });
        }
        public void End() => Publish(Snapshot with { Status = PlaybackStatus.Ended });
        public Task PauseAsync(CancellationToken cancellationToken = default) => Publish(Snapshot with { Status = PlaybackStatus.Paused });
        public Task StopAsync(CancellationToken cancellationToken = default) { StopCount++; return Publish(Snapshot with { Status = PlaybackStatus.Stopped }); }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            Calls.Add("Seek");
            if (FailAt == "Seek") throw new InvalidOperationException("Seek failed");
            return Publish(Snapshot with { Position = position });
        }
        public TaskCompletionSource? VolumeGate { get; set; }
        public bool VolumeWaiting { get; private set; }
        public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
        {
            Calls.Add("Volume");
            if (VolumeGate is not null) { VolumeWaiting = true; await VolumeGate.Task.WaitAsync(cancellationToken); }
            await Publish(Snapshot with { Volume = volume });
        }
        public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default) { Calls.Add("Mute"); return Publish(Snapshot with { IsMuted = isMuted }); }
        private Task Publish(PlaybackSnapshot snapshot)
        {
            if (SuppressUnchanged && Snapshot == snapshot) return Task.CompletedTask;
            Snapshot = snapshot;
            StateChanged?.Invoke(this, snapshot);
            return Task.CompletedTask;
        }
    }
}
