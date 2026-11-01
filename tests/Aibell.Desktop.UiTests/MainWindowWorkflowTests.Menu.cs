using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    private MenuItem Menu(string name) => window.FindControl<MenuItem>(name)!;

    // Opening a top-level menu is what refreshes what is enabled.
    private void OpenMenu(string name) => Menu(name).RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));

    private async Task OpenWithTracksAsync(bool radio = false)
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-menu-{Guid.NewGuid():N}");
        string media = Directory.CreateDirectory(Path.Combine(directory, "music")).FullName;
        foreach (string name in new[] { "first", "second" })
            File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.flac"), Path.Combine(media, name + ".flac"));
        Open(radio: radio);
        await UntilAsync(() => !viewModel.IsBusy);
        await viewModel.ImportAsync(media);
        await UntilAsync(() => window.FindControl<ListBox>("TrackList")!.ItemCount == 2);
    }

    [AvaloniaTest]
    public async Task Menu_LibraryGroupsFollowTheSelectedView()
    {
        await OpenWithTracksAsync(radio: true);

        OpenMenu("LibraryMenu"); OpenMenu("ViewMenu");
        Assert.That(Menu("MenuViewMusic").IsChecked, Is.True);
        Assert.That(Menu("MenuRadio").IsEnabled, Is.False, "Radio actions need the Radio view");
        Assert.That(Menu("MenuPodcasts").IsEnabled, Is.False);
        Assert.That(Menu("MenuSortBy").IsEnabled, Is.True, "sorting belongs to the track table");

        Activate(window.FindControl<Button>("SelectRadioButton")!);
        OpenMenu("LibraryMenu"); OpenMenu("ViewMenu");
        Assert.That(Menu("MenuViewRadio").IsChecked, Is.True);
        Assert.That(Menu("MenuViewMusic").IsChecked, Is.False);
        Assert.That(Menu("MenuRadio").IsEnabled, Is.True);
        Assert.That(Menu("MenuSortBy").IsEnabled, Is.False);
        Assert.That(Menu("MenuColumns").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    public async Task Menu_RadioIsDisabledWhenTheHostHasNoRadio()
    {
        await OpenWithTracksAsync(radio: false);

        OpenMenu("ViewMenu");

        Assert.That(Menu("MenuViewRadio").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    public async Task Menu_EditItemsFollowTheTrackSelection()
    {
        await OpenWithTracksAsync();
        viewModel.SelectedTrack = null;

        OpenMenu("EditMenu");
        Assert.That(Menu("MenuViewMetadata").IsEnabled, Is.False);
        Assert.That(Menu("MenuAddToPlaylist").IsEnabled, Is.False);
        Assert.That(Menu("MenuQueueNext").IsEnabled, Is.False);
        Assert.That(Menu("MenuSelectAll").IsEnabled, Is.True);

        viewModel.SelectedTrack = viewModel.Tracks[0];
        OpenMenu("EditMenu");
        Assert.That(Menu("MenuViewMetadata").IsEnabled, Is.True);
        Assert.That(Menu("MenuQueueNext").IsEnabled, Is.True);
        Assert.That(Menu("MenuRating").IsEnabled, Is.True);
        Assert.That(Menu("MenuAddToPlaylist").Items.OfType<MenuItem>().Any(item => Equals(item.Header, "New Playlist…")), Is.True);
        Assert.That(Menu("MenuRemoveFromPlaylist").IsEnabled, Is.False, "the library view is not a playlist");
    }

    [AvaloniaTest]
    public async Task Menu_PlaylistItemsNeedAPlaylist()
    {
        await OpenWithTracksAsync();

        OpenMenu("FileMenu");

        Assert.That(Menu("MenuAddFolder").IsEnabled, Is.True);
        Assert.That(Menu("MenuNewPlaylist").IsEnabled, Is.True);
        Assert.That(Menu("MenuRenamePlaylist").IsEnabled, Is.False);
        Assert.That(Menu("MenuDeletePlaylist").IsEnabled, Is.False);
        Assert.That(Menu("MenuExportPlaylist").IsEnabled, Is.False);
        Assert.That(Menu("MenuRescanFolder").IsEnabled, Is.EqualTo(viewModel.CanManageLibrarySource));
    }

    [AvaloniaTest]
    public async Task Menu_MainMenuItemReachesTheSelectedTracks()
    {
        await OpenWithTracksAsync();
        viewModel.SelectedTrack = viewModel.Tracks[0];

        OpenMenu("EditMenu");
        Menu("MenuQueueLast").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.That(viewModel.QueueItems.Count, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Menu_QueueItemsActOnTheSelectedEntry()
    {
        await OpenWithTracksAsync();
        viewModel.SelectedTrack = viewModel.Tracks[0];
        OpenMenu("EditMenu");
        Menu("MenuQueueLast").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        OpenMenu("ControlsMenu");
        Assert.That(Menu("MenuQueueRemove").IsEnabled, Is.False, "nothing is selected in the queue yet");
        Assert.That(Menu("MenuQueueClear").IsEnabled, Is.True);

        viewModel.SelectedQueueEntry = viewModel.QueueItems[0];
        OpenMenu("ControlsMenu");
        Assert.That(Menu("MenuQueueRemove").IsEnabled, Is.True);
        Menu("MenuQueueRemove").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.That(viewModel.QueueItems, Is.Empty);
    }

    [AvaloniaTest]
    public async Task Menu_ControlsFollowPlaybackState()
    {
        await OpenWithTracksAsync();
        viewModel.SelectedTrack = null;
        OpenMenu("ControlsMenu");
        Assert.That(Menu("MenuStop").IsEnabled, Is.False);
        Assert.That(Menu("MenuPlayPause").IsEnabled, Is.False);
        Assert.That(Menu("MenuMute").IsEnabled, Is.False);
        Assert.That(Menu("MenuEqualizer").IsEnabled, Is.True);

        viewModel.SelectedTrack = viewModel.Tracks[0];
        await viewModel.TogglePlaybackAsync();
        OpenMenu("ControlsMenu");
        Assert.That(Menu("MenuStop").IsEnabled, Is.True);
        Assert.That(Menu("MenuMute").IsEnabled, Is.True);
        Assert.That(Menu("MenuPlayPause").Header, Is.EqualTo(viewModel.PlaybackButtonText));

        Menu("MenuStop").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => !viewModel.CanStop);
    }

    [AvaloniaTest]
    public async Task Menu_SidebarAndQueueTogglesMirrorThePanels()
    {
        await OpenWithTracksAsync();

        OpenMenu("ViewMenu");
        Assert.That(Menu("MenuShowSidebar").IsChecked, Is.True);
        Assert.That(Menu("MenuShowQueue").IsChecked, Is.True);

        Menu("MenuShowSidebar").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => !viewModel.IsSidebarVisible);
        OpenMenu("ViewMenu");
        Assert.That(Menu("MenuShowSidebar").IsChecked, Is.False);
    }

    [AvaloniaTest]
    public async Task Menu_HelpAboutOpensOneAboutWindow()
    {
        await OpenWithTracksAsync();

        Menu("MenuAbout").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Menu("MenuAbout").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        AboutWindow about = window.OwnedWindows.OfType<AboutWindow>().Single();
        Assert.That(AboutWindowTests.Named<TextBlock>(about, "AboutName").Text, Is.EqualTo("Aibell"));
        about.Close();
    }
}
