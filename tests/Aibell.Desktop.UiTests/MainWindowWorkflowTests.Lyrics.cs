using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    private async Task OpenWithLyricsAsync()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-lyrics-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Open(lyrics: true);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
    }

    [AvaloniaTest]
    public async Task MainWindow_SidePanelTabs_SwitchBetweenThePlayQueueAndTheLyrics()
    {
        await OpenWithLyricsAsync();
        Control panel = window.FindControl<Control>("LyricsPanel")!;
        ListBox queue = window.FindControl<ListBox>("PlayQueueList")!;
        Button queueTab = window.FindControl<Button>("QueueTabButton")!, lyricsTab = window.FindControl<Button>("LyricsTabButton")!;
        Assert.That(queueTab.IsVisible && lyricsTab.IsVisible, Is.True, "both tabs show while lyrics are on");
        StackPanel tabs = (StackPanel)lyricsTab.Parent!;
        Assert.That(tabs.Children.IndexOf(lyricsTab), Is.LessThan(tabs.Children.IndexOf(queueTab)), "lyrics is the first tab");
        Assert.That(viewModel.IsLyricsTabSelected, Is.True, "the lyrics tab is shown by default");
        Assert.That(lyricsTab.Classes, Does.Contain("active"));
        Assert.That(queue.IsEffectivelyVisible, Is.False);
        queueTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Assert.That(queue.IsEffectivelyVisible, Is.True);
        Assert.That(queueTab.Classes, Does.Contain("active"));
        lyricsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Assert.That(viewModel.IsLyricsTabSelected, Is.True);
        Assert.That(panel.GetVisualAncestors().OfType<Border>().First().IsVisible, Is.True);
        Assert.That(window.FindControl<Grid>("LyricsPanel")!.IsEffectivelyVisible, Is.True);
        Assert.That(queue.IsEffectivelyVisible, Is.False);
        Assert.That(lyricsTab.Classes, Does.Contain("active"));
        queueTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Assert.That(queue.IsEffectivelyVisible, Is.True);
        Assert.That(window.FindControl<Grid>("LyricsPanel")!.IsEffectivelyVisible, Is.False);
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_SidePanel_WithoutALyricsServiceKeepsTheOriginalQueueHeader()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-nolyrics-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory); Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        Assert.That(viewModel.IsLyricsAvailable, Is.False);
        Assert.That(window.FindControl<Button>("LyricsTabButton")!.IsEffectivelyVisible, Is.False);
        Assert.That(window.FindControl<ListBox>("PlayQueueList")!.IsEffectivelyVisible, Is.True);
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_LyricsPreference_HidesTheTabAndThePanelAndPersists()
    {
        await OpenWithLyricsAsync();
        viewModel.ShowLyricsTab();
        Dispatcher.UIThread.RunJobs();
        Assert.That(viewModel.IsLyricsTabSelected, Is.True);
        window.FindControl<Button>("OpenPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
        PreferencesDialog dialog = window.OwnedWindows.OfType<PreferencesDialog>().Single();
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 4; Dispatcher.UIThread.RunJobs();
        CheckBox box = dialog.GetVisualDescendants().OfType<CheckBox>().Single(item => item.Name == "PreferencesLyrics");
        Assert.That(box.IsChecked, Is.True, "lyrics are on by default");
        box.IsChecked = false;
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SavePreferences").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !dialog.IsVisible && !viewModel.LyricsEnabled);
        Dispatcher.UIThread.RunJobs();
        Assert.That(viewModel.IsLyricsAvailable, Is.False);
        Assert.That(viewModel.IsLyricsTabSelected, Is.False, "the panel is gone, so the queue shows again");
        Assert.That(window.FindControl<Button>("LyricsTabButton")!.IsEffectivelyVisible, Is.False);
        Assert.That(window.FindControl<ListBox>("PlayQueueList")!.IsEffectivelyVisible, Is.True);
        Assert.That((await settings.LoadAsync()).LyricsEnabled, Is.False);
        await CloseAsync(); await ReleaseServicesAsync();
    }
}
