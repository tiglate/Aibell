using Aibell.Desktop.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

/// <summary>
/// The Podcasts workspace. The view owns no work: its <see cref="PodcastsViewModel"/> is created, loaded and shut down by
/// the host (the main window, or the compatibility <see cref="PodcastsWindow"/>), so navigating away only hides it.
/// </summary>
public sealed partial class PodcastsView : UserControl
{
    public PodcastsView() => InitializeComponent();

    private PodcastsViewModel? Model => DataContext as PodcastsViewModel;

    private static PodcastEpisodeItem? EpisodeFrom(object? sender) => (sender as Control)?.DataContext as PodcastEpisodeItem;

    private async void Subscribe(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.SubscribeAsync(); }
    private async void Refresh(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.RefreshSelectedAsync(); }
    private void CancelOperation(object? sender, RoutedEventArgs args) => Model?.CancelFeedOperation();
    private async void DownloadEpisode(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.DownloadAsync(EpisodeFrom(sender)); }
    private void CancelEpisodeDownload(object? sender, RoutedEventArgs args) => Model?.CancelDownload(EpisodeFrom(sender));
    private async void PlayEpisode(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.PlayAsync(EpisodeFrom(sender)); }
    private async void CoverAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs args) { if (Model is { } m && sender is Control { DataContext: PodcastEpisodeItem item }) await m.EnsureCoverAsync(item); }
    private void ToggleNotes(object? sender, RoutedEventArgs args)
    {
        if (EpisodeFrom(sender) is not { } item) return;
        item.NotesExpanded = !item.NotesExpanded;
        if (Model is { } m) _ = m.MarkSeenAsync(item);
    }
    private async void RefreshAll(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.RefreshAllAsync(); }
    private async void DownloadNew(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.DownloadNewAsync(); }
    private async void MarkAllSeen(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.MarkAllSeenAsync(); }
    private async void PauseEpisode(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.PauseAsync(EpisodeFrom(sender)); }
    private async void StartOverEpisode(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.StartOverAsync(EpisodeFrom(sender)); }
    private async void ResumeSelectedEpisode(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.ResumeSelectedAsync(); }
    private async void SaveSelectedPosition(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.SaveSelectedPositionAsync(); }
    private void ClearFilters(object? sender, RoutedEventArgs args) { Model?.ClearFilters(); PodcastEpisodeSearch.Focus(); }
    private void ClearSearch(object? sender, RoutedEventArgs args) { Model?.ClearSearch(); PodcastEpisodeSearch.Focus(); }

    private async void EpisodeDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (Model is { SelectedEpisode: { CanPlay: true } episode } m) await m.PlayAsync(episode);
    }

    private async void FeedUrlKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || Model is not { CanSubscribe: true } m) return;
        args.Handled = true;
        await m.SubscribeAsync();
    }

    private void SearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || Model is not { HasSearch: true } m) return;
        args.Handled = true;
        m.ClearSearch();
    }

    private PodcastEpisodeItem? ratingTarget;

    // The button's Click fires before its flyout opens, so remember which row the menu belongs to.
    private void BeginRating(object? sender, RoutedEventArgs args) => ratingTarget = EpisodeFrom(sender);

    private async void SetEpisodeRating(object? sender, RoutedEventArgs args)
    {
        if (Model is { } m && ratingTarget is { } item && (sender as MenuItem)?.Tag is string tag && int.TryParse(tag, out int rating))
            await m.SetRatingAsync(item, rating);
    }

    private async void DeleteDownload(object? sender, RoutedEventArgs args) => await ConfirmAndDeleteAsync(EpisodeFrom(sender));

    internal Task DeleteSelectedDownloadAsync() => ConfirmAndDeleteAsync(Model?.SelectedEpisode);

    private async Task ConfirmAndDeleteAsync(PodcastEpisodeItem? item)
    {
        if (Model is not { } m || item is null) return;
        if (await m.FileRemovalBlockReasonAsync(item) is { } blocked) { m.StatusMessage = blocked; return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!await DialogChrome.ConfirmAsync(owner, Loc.Get("Pod.DeleteTitle"), Loc.Format("Pod.DeletePrompt", item.Title),
                Loc.Get("Pod.DeleteAccept"), "ConfirmDeleteDownload")) return;
        await m.DeleteDownloadAsync(item);
    }

    private async void EpisodeListKeyDown(object? sender, KeyEventArgs args)
    {
        if (Model is not { SelectedEpisode: { } item } m || args.KeyModifiers != KeyModifiers.None) return;
        int digit = args.Key is >= Key.D0 and <= Key.D5 ? args.Key - Key.D0
            : args.Key is >= Key.NumPad0 and <= Key.NumPad5 ? args.Key - Key.NumPad0 : -1;
        if (digit >= 0)
        {
            args.Handled = true;
            await m.SetRatingAsync(item, digit);
        }
        else if (args.Key == Key.Delete && item.CanDeleteDownload)
        {
            args.Handled = true;
            await ConfirmAndDeleteAsync(item);
        }
    }

    private async void Remove(object? sender, RoutedEventArgs args) => await RemoveSelectedAsync();

    internal async Task RemoveSelectedAsync()
    {
        if (Model is not { SelectedSubscription: { } selected } m || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!await DialogChrome.ConfirmAsync(owner, Loc.Get("Pod.RemoveTitle"),
                Loc.Format("Pod.RemovePrompt", selected.Title),
                Loc.Get("Dialog.Remove"), "ConfirmRemovePodcast")) return;
        await m.RemoveSelectedAsync();
    }
}
