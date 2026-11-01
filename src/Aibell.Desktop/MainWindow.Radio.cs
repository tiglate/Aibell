using Aibell.Desktop.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Radio;
using Avalonia;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private async void ImportRadioPlaylist(object? sender, RoutedEventArgs args)
    {
        await RunUserActionAsync(async () =>
        {
            if (!viewModel.CanImportRadioPlaylist) return;
            Uri? source = await filePicker.PickFileAsync(this, new FilePickerOpenOptions
            {
                Title = Loc.Get("Picker.ImportStations"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Loc.Get("Picker.StationPlaylists")) { Patterns = ["*.m3u", "*.m3u8", "*.pls"] }]
            });
            if (source is { IsFile: true } && CanRunUserAction) await ShowRadioPlaylistImportAsync(source);
        });
    }

    public Task<bool> ShowRadioPlaylistImportAsync(Uri source)
    {
        if (!CanRunUserAction) return Task.FromResult(false);
        RadioPlaylistImportDialog dialog = new(source, token => viewModel.PrepareRadioPlaylistAsync(source, token),
            async (preview, token) => { await viewModel.ImportRadioPlaylistAsync(preview, token); }, lifetimeCancellation.Token);
        return dialog.ShowDialog<bool>(this);
    }

    // A new filter, search, sort or mode starts the grid at the top; saving a station does not move it.
    private void RadioFilterChanged(object? sender, EventArgs args) => RadioStationList.Scroll?.Offset = default;

    private async void SelectRadio(object? sender, RoutedEventArgs args) =>
        await RunLibraryViewActionAsync(viewModel.SelectRadioAsync);

    private RadioCardViewModel? CardFromSender(object? sender) =>
        (sender as Control)?.DataContext as RadioCardViewModel ?? viewModel.SelectedRadioCard;

    private async void PlayRadioStation(object? sender, RoutedEventArgs args)
    {
        // A fast double click on a card's star must not also start the station.
        if (args.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any(button => button.Classes.Contains("star"))) return;
        if (CardFromSender(sender) is { } card) await RunUserActionAsync(() => viewModel.PlayRadioStationAsync(card.Id, lifetimeCancellation.Token));
    }

    private async void RadioStationKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None || args.Source is Button || viewModel.SelectedRadioId is not { } id) return;
        args.Handled = true;
        await RunUserActionAsync(() => viewModel.PlayRadioStationAsync(id, lifetimeCancellation.Token));
    }

    private void RadioStationPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (CardFromSender(sender) is { } card) viewModel.SelectedRadioCard = card;
    }

    private async void AddRadioStation(object? sender, RoutedEventArgs args) => await ShowRadioEditorAsync(null);
    private async void EditRadioStation(object? sender, RoutedEventArgs args)
    {
        if (CardFromSender(sender) is { IsCatalogCard: false } card) await ShowRadioEditorAsync(card.Station);
    }
    private Task<bool> ShowRadioEditorAsync(RadioStation? station)
    {
        RadioStationDialog dialog = new(station, value => viewModel.SaveRadioStationAsync(value, station is null, lifetimeCancellation.Token));
        return dialog.ShowDialog<bool>(this);
    }

    private void ShowMyRadioStations(object? sender, RoutedEventArgs args) => viewModel.SetRadioCatalogMode(false);
    private void ShowRadioCatalog(object? sender, RoutedEventArgs args) => viewModel.SetRadioCatalogMode(true);

    private async void EnableRadioCatalog(object? sender, RoutedEventArgs args) =>
        await RunUserActionAsync(() => viewModel.SetRadioCatalogEnabledAsync(true));

    // The star on a card: save a catalog station, or remove a saved one. Removing a station from "My stations" asks first
    // because it loses its play history; a catalog station can simply be saved again.
    private async void ToggleRadioSaved(object? sender, RoutedEventArgs args)
    {
        if (CardFromSender(sender) is not { } card) return;
        args.Handled = true;
        if (viewModel.SavedStationFor(card) is { } saved)
        {
            if (!await DialogChrome.ConfirmAsync(this, Loc.Get("Radio.RemoveTitle"), Loc.Format("Radio.RemovePrompt", saved.Name),
                Loc.Get("Dialog.Remove"), "ConfirmRemoveRadioStation")) return;
        }
        await RunUserActionAsync(() => viewModel.ToggleRadioCardSavedAsync(card, lifetimeCancellation.Token));
    }

    // The station search in the Radio area and the search box in the toolbar are one search: typing in either fills the other.
    private void RadioSearchChanged(object? sender, TextChangedEventArgs args)
    {
        string text = RadioSearchBox.Text ?? string.Empty;
        if (!string.Equals(SearchBox.Text ?? string.Empty, text, StringComparison.Ordinal)) SearchBox.Text = text;
    }

    private void MirrorRadioSearch()
    {
        string text = SearchBox.Text ?? string.Empty;
        if (!string.Equals(RadioSearchBox.Text ?? string.Empty, text, StringComparison.Ordinal)) RadioSearchBox.Text = text;
    }

    // Below this width the genre list steps aside; the Genres drop-down next to the search box offers the same choices.
    private void RadioBrowserSizeChanged(object? sender, SizeChangedEventArgs args) => RadioGenrePane.IsVisible = args.NewSize.Width >= 640;

    // Narrow windows put the three drop-downs on their own row under the search box.
    private void RadioFilterBarSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        bool narrow = args.NewSize.Width < 700;
        Grid.SetRow(RadioFilterChoices, narrow ? 1 : 0); Grid.SetColumn(RadioFilterChoices, narrow ? 0 : 1);
        Grid.SetColumnSpan(RadioFilterChoices, narrow ? 2 : 1);
    }

    // A logo is fetched only once its card is (nearly) inside the scroll viewport, and only after the user enabled the catalog.
    // EffectiveViewport is the viewport in the card's own coordinates, so a card scrolled out of view still gets a non-empty
    // rectangle: it must also overlap the card to count.
    private void RadioCardViewportChanged(object? sender, EffectiveViewportChangedEventArgs args)
    {
        if (sender is not Control { DataContext: RadioCardViewModel card } control || card.LogoUri is null) return;
        Rect viewport = args.EffectiveViewport;
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        const double preload = 160;
        if (!new Rect(control.Bounds.Size).Intersects(viewport.Inflate(preload))) return;
        _ = viewModel.RequestRadioLogoAsync(card);
    }

    private async void RemoveRadioStation(object? sender, RoutedEventArgs args)
    {
        if (CardFromSender(sender) is not { IsCatalogCard: false } card || card.Station is not { } station ||
            !await DialogChrome.ConfirmAsync(this, Loc.Get("Radio.RemoveTitle"), Loc.Format("Radio.RemovePrompt", station.Name),
                Loc.Get("Dialog.Remove"), "ConfirmRemoveRadioStation")) return;
        await RunUserActionAsync(() => viewModel.RemoveRadioStationAsync(station.Id, lifetimeCancellation.Token));
    }
}
