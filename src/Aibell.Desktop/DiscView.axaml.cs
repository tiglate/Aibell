using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Aibell.Application.Enrichment;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

/// <summary>
/// The Audio CD workspace. The view owns no work: its <see cref="DiscViewModel"/> is created, loaded and shut down by the host
/// (the main window), so navigating away only hides it. Double-clicking or pressing Enter on a track plays the disc from it.
/// </summary>
public sealed partial class DiscView : UserControl
{
    private bool loaded;

    public DiscView()
    {
        InitializeComponent();
        DiscTrackList.AddHandler(KeyDownEvent, TrackKeyDown, RoutingStrategies.Tunnel);
    }

    private DiscViewModel? Model => DataContext as DiscViewModel;

    /// <summary>Reads the first disc; a release picked afterwards by the user is re-applied, one set by the lookup itself is not.</summary>
    public async Task LoadAsync()
    {
        if (Model is not { } model) return;
        await model.LoadAsync();
        loaded = true;
    }

    private void CancelOperation(object? sender, RoutedEventArgs args) => Model?.CancelOperation();
    private async void Refresh(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.RefreshAsync(); }
    private async void LookUp(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.LookUpAsync(); }
    private async void ShowArtistInfo(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.ShowArtistAsync(); }
    private async void TrackDoubleTapped(object? sender, TappedEventArgs args) { if (Model is { } m) await m.PlayAsync(m.SelectedRow); }

    private async void TrackKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None || Model is not { SelectedRow: not null } m) return;
        args.Handled = true;
        await m.PlayAsync(m.SelectedRow);
    }

    private async void ReleaseChosen(object? sender, SelectionChangedEventArgs args)
    {
        // Selection also changes when a lookup fills the list; only a pick by the user should re-apply a release.
        if (!loaded || Model is not { IsBusy: false } m || args.AddedItems.Count != 1 || args.AddedItems[0] is not ReleaseCandidate release) return;
        await m.ChooseReleaseAsync(release);
    }
}
