using Aibell.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private LyricsViewModel? lyricsPanel;

    private void InitializeLyrics()
    {
        lyricsPanel = viewModel.Lyrics;
        if (lyricsPanel is null) return;
        lyricsPanel.CurrentLineChanged += ScrollToLyricLine;
        Closed += (_, _) => lyricsPanel.CurrentLineChanged -= ScrollToLyricLine;
        // Showing the tab again after a hidden spell scrolls to the line that is being sung.
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsLyricsTabSelected) && viewModel.IsLyricsTabSelected) Dispatcher.UIThread.Post(() => ScrollToLyricLine(null, CurrentLyricIndex()), DispatcherPriority.Background);
        };
    }

    private int CurrentLyricIndex()
    {
        if (lyricsPanel is null) return -1;
        for (int index = 0; index < lyricsPanel.Lines.Count; index++) if (lyricsPanel.Lines[index].IsCurrent) return index;
        return -1;
    }

    // Keeps the current line about a third of the way down the panel, so the next lines are visible before they are sung.
    private void ScrollToLyricLine(object? sender, int index)
    {
        if (this.FindControl<ScrollViewer>("LyricsScroll") is not { } scroll) return;
        if (index < 0) { if (lyricsPanel?.IsSynced != true) scroll.Offset = default; return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (scroll.Content is not ItemsControl items || items.ContainerFromIndex(index) is not Control container) return;
            Point? top = container.TranslatePoint(new Point(0, 0), items);
            if (top is null) return;
            double target = Math.Max(0, top.Value.Y + items.Margin.Top - scroll.Viewport.Height / 3);
            scroll.Offset = new Vector(0, Math.Min(target, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        }, DispatcherPriority.Background);
    }

    private void ShowQueueTab(object? sender, RoutedEventArgs eventArgs) => viewModel.ShowQueueTab();

    private void ShowLyricsTab(object? sender, RoutedEventArgs eventArgs) => viewModel.ShowLyricsTab();

    private async void SearchLyrics(object? sender, RoutedEventArgs eventArgs) => await RunUserActionAsync(async () =>
    {
        if (viewModel.Lyrics is not { Track: { } track } panel) return;
        LyricsSearchDialog dialog = new(panel.Service, track);
        if (await dialog.ShowDialog<bool>(this) && dialog.Selected is { } chosen) panel.Show(track, chosen);
    });

    private async void EditLyrics(object? sender, RoutedEventArgs eventArgs) => await RunUserActionAsync(async () =>
    {
        if (viewModel.Lyrics is not { Track: { } track } panel) return;
        string? block = await viewModel.GetLyricsFileWriteBlockAsync(track);
        LyricsEditDialog dialog = new(panel.Service, track, panel.Lyrics, block, (target, text) => viewModel.SaveLyricsToFileAsync(target, text));
        await dialog.ShowDialog<bool>(this);
        switch (dialog.Outcome)
        {
            case LyricsEditOutcome.Saved: panel.Show(track, dialog.Result); break;
            case LyricsEditOutcome.Removed: await panel.RefreshAsync(); break;
        }
    });
}
