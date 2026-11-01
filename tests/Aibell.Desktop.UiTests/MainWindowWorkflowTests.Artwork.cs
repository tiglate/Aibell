using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Enrichment;
using Aibell.Application.Library;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    private ArtworkWindowTests.FakeWriter coverWriter = new();
    private int artworkWindowsCreated;

    private Func<Track, ArtworkWindow> CoverFactory(MetadataLookupStatus status = MetadataLookupStatus.Found) => track =>
    {
        artworkWindowsCreated++;
        return new ArtworkWindow(new ArtworkLookupViewModel(track,
            new ArtworkWindowTests.FakeLookup(status, new ReleaseCandidate("test", "r", "Synthetic Fixtures", "Aibell Test Runner", 2026, null, new Uri("https://c.test/1.jpg"), 0.9, [])),
            new ArtworkWindowTests.FakeDownloader(), coverWriter));
    };

    private async Task<MenuItem> OpenArtworkMenuItemAsync(IMediaMetadataReader? reader = null)
    {
        artworkWindowsCreated = 0;
        coverWriter = new();
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-artwork-{Guid.NewGuid():N}");
        string media = Path.Combine(directory, "music"); Directory.CreateDirectory(media);
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), Path.Combine(media, "first.flac"));
        Open(metadataReader: reader, artworkWindowFactory: CoverFactory());
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.ImportAsync(media);
        ListBox list = window.FindControl<ListBox>("TrackList")!;
        await UntilAsync(() => list.ItemCount == 1);
        list.SelectedIndex = 0;
        await UntilAsync(() => viewModel.SelectedTrack is not null);
        Border row = list.GetVisualDescendants().OfType<Border>().First(border => border.ContextMenu is not null && border.DataContext is TrackRowViewModel);
        row.BringIntoView(); Dispatcher.UIThread.RunJobs();
        Avalonia.Point position = row.TranslatePoint(new Avalonia.Point(80, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(position, MouseButton.Right); window.MouseUp(position, MouseButton.Right);
        await UntilAsync(() => row.ContextMenu!.IsOpen);
        return row.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Get Artwork…"));
    }

    [AvaloniaTest]
    public async Task MainWindow_GetArtworkContextMenu_IsOfferedForAnAlbumTrack()
    {
        MenuItem item = await OpenArtworkMenuItemAsync();

        Assert.That(item.IsEnabled, Is.True);
        Assert.That(((TrackRowViewModel)item.Tag!).Source.Album, Is.EqualTo("Synthetic Fixtures"));
    }

    [AvaloniaTest]
    public async Task MainWindow_GetArtwork_OnlineLookupOff_AsksBeforeTurningItOnAndSendsNothingWhenDeclined()
    {
        MenuItem item = await OpenArtworkMenuItemAsync();
        Assert.That(viewModel.OnlineLookupAllowed, Is.False, "lookups are off by default");

        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Window confirm = await DialogAsync();
        Assert.That(confirm.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text), Has.Some.Contain("sends the album and artist name"));
        confirm.Close(false);
        await UntilAsync(() => window.OwnedWindows.Count == 0);

        Assert.That(viewModel.OnlineLookupAllowed, Is.False);
        Assert.That(artworkWindowsCreated, Is.Zero);
    }

    [AvaloniaTest]
    public async Task MainWindow_GetArtwork_AcceptingTurnsLookupOnOpensTheWindowAndASavedCoverIsReported()
    {
        MenuItem item = await OpenArtworkMenuItemAsync();

        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Click("ConfirmEnableOnlineLookup", await DialogAsync());
        await UntilAsync(() => window.OwnedWindows.OfType<ArtworkWindow>().Any());
        ArtworkWindow artwork = window.OwnedWindows.OfType<ArtworkWindow>().Single();

        Assert.That(viewModel.OnlineLookupAllowed, Is.True);
        Assert.That((await settings.LoadAsync()).OnlineLookup, Is.True, "the choice is saved like the Preferences checkbox");
        await UntilAsync(() => artwork.Model.CanSave);
        Click("SaveArtwork", artwork);
        await UntilAsync(() => artwork.Model.SavedPath is not null);
        Click("CloseArtwork", artwork);
        await UntilAsync(() => viewModel.StatusMessage.StartsWith("Cover saved", StringComparison.Ordinal));

        Assert.That(coverWriter.Saved, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task MainWindow_GetArtwork_TrackWithoutAlbum_IsDisabledWithAReason()
    {
        MenuItem item = await OpenArtworkMenuItemAsync(new BlankAlbumReader());

        Assert.That(item.IsEnabled, Is.False);
        Assert.That(ToolTip.GetTip(item), Is.EqualTo("This track has no album name to look up."));
    }

    private async Task<Window> DialogAsync()
    {
        await UntilAsync(() => window.OwnedWindows.Count == 1);
        Dispatcher.UIThread.RunJobs();
        return window.OwnedWindows[0];
    }

    private static void Click(string name, Window owner)
    {
        Dispatcher.UIThread.RunJobs();
        Button button = owner.GetVisualDescendants().OfType<Button>().First(candidate => candidate.Name == name);
        Assert.That(button.IsEnabled, Is.True, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private sealed class BlankAlbumReader : IMediaMetadataReader
    {
        public Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaMetadata("Loose track", "Someone", "", TimeSpan.FromSeconds(1)));
    }
}
