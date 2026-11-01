using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Aibell.Application.Discs;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    private static readonly OpticalDrive WorkflowDrive = new("/dev/sr0", "CD/DVD drive (sr0)");
    private static readonly DiscToc WorkflowToc = new([150, 15363, 32314], 46592);

    [AvaloniaTest]
    public async Task MainWindow_PlayDisc_QueuesRemainingTracksStartsTheChosenOneAndStaysOutOfTheLibrary()
    {
        PrepareDiscOwner();
        Open();
        await UntilAsync(() => !viewModel.IsBusy);
        int libraryBefore = (await repository.ListAsync()).Count;

        await viewModel.PlayDiscAsync(DiscTracks.Build(WorkflowDrive, WorkflowToc), 1);

        Assert.That(viewModel.QueueItems.Select(item => item.Row.Source.Location.AbsoluteUri),
            Is.EqualTo(DiscUris("cdda:///dev/sr0?track=2", "cdda:///dev/sr0?track=3")));
        Assert.That(engine.Snapshot.Track!.Location.AbsoluteUri, Is.EqualTo("cdda:///dev/sr0?track=2"));
        Assert.That((await repository.ListAsync()).Count, Is.EqualTo(libraryBefore));
    }

    [AvaloniaTest]
    public async Task MainWindow_PlayDisc_RejectsLibraryFiles()
    {
        PrepareDiscOwner();
        Open();
        await UntilAsync(() => !viewModel.IsBusy);
        Aibell.Domain.Media.Track file = new(Guid.NewGuid(), new Uri("file:///music/a.mp3"), "A", "B", "C", TimeSpan.FromMinutes(1));

        await Assert.ThatAsync(() => viewModel.PlayDiscAsync([file], 0), Throws.ArgumentException);
    }

    [AvaloniaTest]
    public async Task MainWindow_PlayDisc_IsNotSavedWithThePlayQueue()
    {
        PrepareDiscOwner();
        Open();
        await UntilAsync(() => !viewModel.IsBusy);
        await viewModel.PlayDiscAsync(DiscTracks.Build(WorkflowDrive, WorkflowToc), 0);
        await viewModel.FlushPreferencesAsync();

        Assert.That((await settings.LoadAsync()).PlayQueue ?? [], Is.Empty);
    }

    [AvaloniaTest]
    public async Task MainWindow_AudioCdView_ReplacesMusicInTheMainAreaAndMusicComesBack()
    {
        PrepareDiscOwner();
        Open();
        await UntilAsync(() => !viewModel.IsBusy);

        viewModel.ShowDiscView();
        Assert.That(viewModel.IsDiscView, Is.True);
        Assert.That(viewModel.IsMusicView, Is.False);
        Assert.That(viewModel.CanSearchLibrary, Is.False);

        viewModel.ShowPodcastsView();
        Assert.That(viewModel.IsDiscView, Is.False, "Podcasts and Audio CD never show together.");

        viewModel.ShowDiscView();
        Assert.That(viewModel.TryReturnFromOverlayToMusic(), Is.True);
        Assert.That(viewModel.IsDiscView, Is.False);
        Assert.That(viewModel.IsMusicView, Is.True);
    }

    [AvaloniaTest]
    public async Task MainWindow_DevicesAndAudiobooksViews_ReplaceMusicInTheMainAreaOneAtATime()
    {
        PrepareDiscOwner();
        Open();
        await UntilAsync(() => !viewModel.IsBusy);

        viewModel.ShowDevicesView();
        Assert.That(viewModel.IsDevicesView, Is.True);
        Assert.That(viewModel.IsMusicView, Is.False);
        Assert.That(viewModel.CanSearchLibrary, Is.False);

        viewModel.ShowAudiobooksView();
        Assert.That(viewModel.IsDevicesView, Is.False, "Only one main-area view shows at a time.");
        Assert.That(viewModel.IsAudiobooksView, Is.True);

        Assert.That(viewModel.TryReturnFromOverlayToMusic(), Is.True);
        Assert.That(viewModel.IsAudiobooksView, Is.False);
        Assert.That(viewModel.IsMusicView, Is.True);
    }

    [AvaloniaTest]
    public async Task MainWindow_Sidebar_ShowsEveryLibraryEntryInsideItsRow()
    {
        PrepareDiscOwner();
        Open();
        await UntilAsync(() => !viewModel.IsBusy);
        window.UpdateLayout();

        foreach (string name in new[] { "SelectMusicButton", "SelectRadioButton", "OpenDiscButton", "OpenDevicesButton", "OpenPodcastsButton" })
        {
            Button button = window.FindControl<Button>(name)!;
            Control row = (Control)button.Parent!;
            Assert.That(button.Bounds.Bottom, Is.LessThanOrEqualTo(row.Bounds.Height + 0.5), name + " must not be clipped by the sidebar row.");
        }
    }

    [AvaloniaTest]
    public async Task MainWindow_FollowsTheUiLanguage_ForSidebarStatusAndPlaybackTexts()
    {
        System.Globalization.CultureInfo saved = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
            PrepareDiscOwner();
            Open();
            await UntilAsync(() => !viewModel.IsBusy);

            Assert.That(window.FindControl<Button>("OpenDiscButton")!.Content, Is.EqualTo("CD de áudio"));
            Assert.That(window.FindControl<Button>("OpenPodcastsButton")!.Content, Is.EqualTo("Podcasts"));
            Assert.That(viewModel.NowPlaying, Is.EqualTo("Nada tocando"));
            Assert.That(viewModel.PlaybackButtonText, Is.EqualTo("Tocar"));
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = saved; }
    }

    [AvaloniaTest]
    [TestCase("ar", "قرص CD صوتي")]
    [TestCase("he", "תקליטור שמע")]
    [TestCase("ja", "音楽 CD")]
    [TestCase("zh-Hans", "音频 CD")]
    [TestCase("ru", "Аудио-CD")]
    [TestCase("pl", "Płyta CD audio")]
    [TestCase("de", "Audio-CD")]
    [TestCase("fr", "CD audio")]
    [TestCase("it", "CD audio")]
    [TestCase("es", "CD de audio")]
    public async Task MainWindow_OpensInEveryShippedLanguage(string language, string expectedDiscButton)
    {
        System.Globalization.CultureInfo saved = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(language);
            PrepareDiscOwner();
            Open();
            await UntilAsync(() => !viewModel.IsBusy);

            Assert.That(window.FindControl<Button>("OpenDiscButton")!.Content, Is.EqualTo(expectedDiscButton));
            Assert.That(viewModel.NowPlaying, Does.Not.Contain("!").And.Not.Empty);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = saved; }
    }

    private static string[] DiscUris(params string[] uris) => uris;

    private void PrepareDiscOwner()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-disc-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
    }
}
