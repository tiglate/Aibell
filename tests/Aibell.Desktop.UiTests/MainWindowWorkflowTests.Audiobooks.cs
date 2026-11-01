using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Aibell.Application.Audiobooks;
using Aibell.Application.Playback;
using Aibell.Application.Podcasts;
using Aibell.Desktop.ViewModels;
using Aibell.Infrastructure.Audiobooks;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    private static readonly string[] AudiobookStartCalls = ["Load", "Volume", "Mute", "Seek", "Play"];
    [AvaloniaTest]
    public async Task MainWindow_AudiobookResume_SeeksBeforePlayingAndSavesPausedPositionWithoutMusicAdmission()
    {
        string file = PrepareAudiobook();
        AudiobookShelfService? service = null;
        AudiobooksViewModel? shelf = null;
        Open(audiobooksViewModelFactory: handoff => shelf = new(service!, handoff));
        await UntilAsync(() => !viewModel.IsBusy);
        service = new(repository, new AtlMediaMetadataReader(), new AudiobookFiles());
        var added = await service.AddAsync(new Uri(file));
        TimeSpan saved = added.Entry.Duration / 3;
        Assert.That(saved, Is.GreaterThan(TimeSpan.Zero));
        await repository.SaveShelfPositionAsync(added.Entry.Location, saved, added.Entry.Duration, DateTimeOffset.UtcNow);
        Activate(window.FindControl<Button>("OpenAudiobooksButton")!);
        await UntilAsync(() => shelf?.Items.Count == 1 && !shelf.IsBusy);
        engine.RequireUiSnapshotRead = true;
        engine.Calls.Clear();
        await shelf!.PlayAsync(shelf.Items.Single(), resume: true);
        Assert.That(engine.Calls, Is.EqualTo(AudiobookStartCalls));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(saved));
        Assert.That(viewModel.QueueItems.Single().Row.Source.Location, Is.EqualTo(new Uri(file)));
        Assert.That(await repository.ListAsync(), Is.Empty);
        await engine.PauseAsync();
        Task<AudiobookPlaybackSnapshot?> snapshot = Task.Run(async () => await viewModel.GetCurrentAsync());
        await UntilAsync(() => snapshot.IsCompleted);
        Assert.That((await snapshot)!.CanSavePosition, Is.True);
        await shelf.SaveCurrentAsync();
        Assert.That((await repository.ListShelfAsync()).Single().SavedPosition, Is.EqualTo(saved));
        await engine.LoadAsync(engine.Snapshot.Track! with { Location = new Uri(Path.Combine(directory, "other.mp3")) });
        await engine.PlayAsync();
        Assert.That((await service.SaveCurrentAsync(viewModel)).Outcome, Is.EqualTo(AudiobookSaveOutcome.NotOnShelf));
        Assert.That((await repository.ListShelfAsync()).Single().SavedPosition, Is.EqualTo(saved));
        window.Close();
        await UntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task MainWindow_AudiobookHandoff_UsesActualDurationAndReportsMissingSource()
    {
        string file = PrepareAudiobook();
        Open();
        await UntilAsync(() => !viewModel.IsBusy);
        await viewModel.PlayAsync(new AudiobookPlaybackRequest(new Uri(file), "Book", "Author", TimeSpan.FromDays(1), TimeSpan.FromDays(2)));
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero), "An out-of-range saved position restarts at the real end boundary.");
        Assert.That(engine.Snapshot.Track!.Duration, Is.LessThan(TimeSpan.FromDays(1)));
        int count = viewModel.QueueItems.Count;
        await File.WriteAllTextAsync(file, "The source was replaced by an incomplete audio copy.");
        await Assert.ThatAsync(async () => await viewModel.PlayAsync(new AudiobookPlaybackRequest(new Uri(file), "Book", "Author", TimeSpan.Zero, null)), Throws.TypeOf<InvalidDataException>());
        Assert.That(viewModel.QueueItems.Count, Is.EqualTo(count));
        File.Delete(file);
        await Assert.ThatAsync(async () => await viewModel.PlayAsync(new AudiobookPlaybackRequest(new Uri(file), "Book", "Author", TimeSpan.Zero, null)), Throws.Exception);
        Assert.That(viewModel.QueueItems.Count, Is.EqualTo(count));
    }

    [AvaloniaTest]
    public async Task MainWindow_AudiobookEngineFailure_IsReportedToShelfInsteadOfSuccess()
    {
        string file = PrepareAudiobook();
        Open();
        await UntilAsync(() => !viewModel.IsBusy);
        foreach (string stage in new[] { "Load", "Seek", "Play" })
        {
            engine.FailAt = stage;
            await Assert.ThatAsync(async () => await viewModel.PlayAsync(new AudiobookPlaybackRequest(new Uri(file), "Book", "Author", TimeSpan.FromMilliseconds(100), null)),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains(stage));
        }
        engine.FailAt = null;
        await engine.StopAsync();
        Assert.That((await viewModel.GetCurrentAsync())!.CanSavePosition, Is.False);
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringAudiobookMetadataRead_DrainsAndDoesNotEnqueueAfterShutdown()
    {
        string file = PrepareAudiobook();
        BlockingMetadataReader reader = new();
        Open(metadataReader: reader);
        await UntilAsync(() => !viewModel.IsBusy);
        Task play = viewModel.PlayAsync(new AudiobookPlaybackRequest(new Uri(file), "Book", "Author", TimeSpan.FromMilliseconds(100), null));
        await UntilAsync(() => reader.Started);
        window.Close(); Dispatcher.UIThread.RunJobs();
        try { Assert.That(window.IsVisible, Is.True); }
        finally { reader.Release.TrySetResult(); }
        await Assert.ThatAsync(async () => await play, Throws.InstanceOf<OperationCanceledException>());
        await UntilAsync(() => !window.IsVisible);
        Assert.That(engine.LoadCount, Is.Zero);
        Assert.That(viewModel.QueueItems, Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_CloseDuringAudiobookShelfAdd_CancelsOwnedWorkBeforeStorageDisposal()
    {
        string file = PrepareAudiobook();
        AudiobookShelfService? service = null;
        AudiobooksViewModel? shelf = null;
        BlockingMetadataReader reader = new();
        Open(audiobooksViewModelFactory: handoff => shelf = new(service!, handoff));
        await UntilAsync(() => !viewModel.IsBusy);
        service = new(repository, reader, new AudiobookFiles());
        Activate(window.FindControl<Button>("OpenAudiobooksButton")!);
        await UntilAsync(() => shelf is { IsBusy: false });
        Task add = shelf!.AddAsync(new Uri(file));
        await UntilAsync(() => reader.Started);
        window.Close();
        await add;
        await UntilAsync(() => !window.IsVisible);
        Assert.That(await repository.ListShelfAsync(), Is.Empty);
        Assert.That(engine.LoadCount, Is.Zero);
    }

    [AvaloniaTest]
    public async Task MainWindow_StopDuringAudiobookLoadSetup_DoesNotStartDeferredPlayback()
    {
        string file = PrepareAudiobook();
        Open();
        await UntilAsync(() => !viewModel.IsBusy);
        engine.VolumeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task play = viewModel.PlayAsync(new AudiobookPlaybackRequest(new Uri(file), "Book", "Author", TimeSpan.FromMilliseconds(100), null));
        await UntilAsync(() => engine.VolumeWaiting);
        await viewModel.StopPlaybackAsync();
        engine.VolumeGate.TrySetResult();
        await Assert.ThatAsync(async () => await play, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(engine.Calls, Does.Not.Contain("Play"));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Stopped));
    }

    [AvaloniaTest]
    public async Task MainWindow_PodcastHandoff_PropagatesSharedEngineFailure()
    {
        string file = PrepareAudiobook();
        Open();
        await UntilAsync(() => !viewModel.IsBusy);
        engine.FailAt = "Play";
        await Assert.ThatAsync(async () => await viewModel.PlayAsync(new PodcastPlaybackRequest(new Uri(file), "Episode", "Show", null)),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Play failed"));
        Assert.That(await repository.ListAsync(), Is.Empty);
    }

    private string PrepareAudiobook()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-audiobook-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "book.mp3");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.mp3"), file);
        return file;
    }
}
