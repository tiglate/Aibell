using Aibell.Application.Settings;
using Aibell.Desktop.Localization;
using System.Collections.ObjectModel;
using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Radio;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<RadioStation> allRadioStations = [];
    private RadioStation? playingRadioStation;
    private bool radioConnectionRequested;
    private string? radioGenreFilter;
    private string? radioCountryFilter;
    private bool updatingRadioFilters;
    private bool syncingRadioSelection;
    private bool radioCatalogEnabled;
    private List<RadioCardViewModel>? catalogCards;
    private Dictionary<string, RadioStation> savedByCatalogId = new(StringComparer.Ordinal);
    private Dictionary<string, RadioStation> savedByStream = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Uri, Avalonia.Media.Imaging.Bitmap> radioLogoCache = [];
    private CancellationTokenSource radioLogoCancellation = new();
    /// <summary>The saved stations that match the search, country and genre ("My stations" mode only).</summary>
    public ObservableCollection<RadioStation> RadioStations { get; } = [];
    /// <summary>The cards on screen, for whichever mode is active.</summary>
    public ObservableCollection<RadioCardViewModel> RadioCards { get; } = [];
    public ObservableCollection<RadioGenreRow> RadioGenres { get; } = [];
    public ObservableCollection<RadioGenreRow> RadioGenreChoices { get; } = [];
    public ObservableCollection<RadioCountryChoice> RadioCountries { get; } = [];
    [ObservableProperty] private RadioGenreRow? selectedRadioGenre;
    [ObservableProperty] private RadioGenreRow? selectedRadioGenreChoice;
    [ObservableProperty] private RadioCountryChoice? selectedRadioCountry;
    [ObservableProperty] private string radioGenreSearch = "";
    [ObservableProperty] private int radioSortIndex;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRadioMyMode), nameof(ShowRadioCatalogOptIn), nameof(ShowRadioBrowser))]
    private bool isRadioCatalogMode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(SelectedRadioId))]
    private RadioCardViewModel? selectedRadioCard;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanEditRadioStation))]
    private RadioStation? selectedRadioStation;
    private string radioEmptyText = Loc.Get("Main.NoStations");
    public string RadioEmptyText { get => radioEmptyText; private set => SetProperty(ref radioEmptyText, value); }
    public bool RadioCatalogEnabled => radioCatalogEnabled;
    /// <summary>The curated list is offered only when the application ships one.</summary>
    public bool CanUseRadioCatalog => curatedCatalog is { Stations.Count: > 0 };
    public bool IsRadioMyMode => !IsRadioCatalogMode;
    public bool ShowRadioCatalogOptIn => IsRadioCatalogMode && !radioCatalogEnabled;
    public bool ShowRadioBrowser => !ShowRadioCatalogOptIn;
    public Guid? SelectedRadioId => SelectedRadioCard?.Id ?? SelectedRadioStation?.Id;
    public bool CanUseRadio => radioRepository is not null;
    public bool CanManageRadio => CanUseRadio && !IsBusy && !changingTrack && !playbackShutdown;
    public bool CanEditRadioStation => CanManageRadio && SelectedRadioStation is not null;
    public bool IsRadioView => activeView == LibraryViewKind.Radio && overlay == OverlayView.None;
    public bool IsMusicView => !IsRadioView && overlay == OverlayView.None;
    public bool IsRadioEmpty => RadioCards.Count == 0 && ShowRadioBrowser;
    public string SearchPlaceholder => IsPodcastsView ? Loc.Get("Search.InPodcasts") : IsRadioView ? Loc.Get("Search.Stations") : Loc.Get("Search.Library");
    public bool IsRadioPlayback => playingRadioStation is { } station && playbackEngine.Snapshot.Track is { } track &&
        station.Id == track.Id && station.Stream == track.Location;
    public bool ShowSeekBar => !IsRadioPlayback;
    public bool ShowPlaybackPause => IsPlaybackPlaying && !IsRadioPlayback;
    public bool ShowRadioStop => IsRadioPlayback && (IsPlaybackPlaying || radioConnectionRequested);
    public bool ShowPlaybackPlay => !IsPlaybackPlaying && !ShowRadioStop;
    public bool CanSeekPlayback => !IsRadioPlayback && playbackEngine.Snapshot is
    {
        Status: PlaybackStatus.Playing or PlaybackStatus.Paused,
        Track.Duration: { } duration
    } && duration > TimeSpan.Zero;

    public async Task SelectRadioAsync(CancellationToken cancellationToken = default)
    {
        if (radioRepository is null) return;
        await RunBusyAsync(async () =>
        {
            await RefreshRadioAsync(cancellationToken);
            SetActiveView(LibraryViewKind.Radio); SelectedTrack = null;
            // Choosing Radio always opens the station catalog first; "My stations" is one click away.
            SetRadioCatalogMode(true);
            FilterRadioStations();
        });
    }

    /// <summary>Switches between the saved stations and the curated catalog. The catalog stays empty until the user opts in.</summary>
    public void SetRadioCatalogMode(bool catalog)
    {
        if (catalog && !CanUseRadioCatalog) catalog = false;
        if (IsRadioCatalogMode == catalog) return;
        IsRadioCatalogMode = catalog;
        radioGenreFilter = null; radioCountryFilter = null; RadioGenreSearch = "";
        OnPropertyChanged(nameof(IsRadioEmpty));
        // Start each mode at the top with its own selection rather than scrolling to a card picked in the other one.
        FilterRadioStations(Guid.Empty, resetScroll: true);
    }

    /// <summary>Turns the catalog on or off and saves the choice, exactly as the Preferences switch does.</summary>
    public Task SetRadioCatalogEnabledAsync(bool enabled) => SavePreferencesAsync(GetPreferences() with { RadioCatalog = enabled });

    private void ApplyRadioCatalogEnabled(bool enabled)
    {
        if (radioCatalogEnabled == enabled) return;
        radioCatalogEnabled = enabled;
        if (!enabled)
        {
            // Turning the catalog off also forgets every downloaded logo from memory and stops pending requests.
            radioLogoCancellation.Cancel(); radioLogoCancellation.Dispose(); radioLogoCancellation = new();
            radioLogoCache.Clear();
            radioNowPlayingLogoUri = null; RadioNowPlayingLogo = null;
            foreach (RadioCardViewModel card in RadioCards) { card.Logo = null; card.LogoRequested = false; }
            if (catalogCards is not null) foreach (RadioCardViewModel card in catalogCards) { card.Logo = null; card.LogoRequested = false; }
        }
        OnPropertyChanged(nameof(RadioCatalogEnabled)); OnPropertyChanged(nameof(ShowRadioCatalogOptIn)); OnPropertyChanged(nameof(ShowRadioBrowser));
        OnPropertyChanged(nameof(IsRadioEmpty));
        if (activeView == LibraryViewKind.Radio) FilterRadioStations();
    }

    public Task SaveRadioStationAsync(RadioStation station, bool add, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(async () =>
        {
            if (radioRepository is null) throw new NotSupportedException("Radio storage is unavailable.");
            station.Validate();
            if (changingTrack) throw new InvalidOperationException("Wait for playback to finish changing sources.");
            IsBusy = true;
            try
            {
                if (add) await radioRepository.AddStationAsync(station, cancellationToken);
                else await radioRepository.UpdateStationAsync(station, cancellationToken);
                await RefreshRadioAsync(CancellationToken.None, station.Id);
                StatusMessage = Loc.Get("Radio.Saved");
            }
            finally { IsBusy = false; }
        });

    /// <summary>Copies a catalog station into "My stations". Saving the same station twice changes nothing.</summary>
    public Task SaveCatalogRadioStationAsync(RadioCardViewModel card, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(async () =>
        {
            if (radioRepository is null) throw new NotSupportedException("Radio storage is unavailable.");
            ArgumentNullException.ThrowIfNull(card);
            if (changingTrack) throw new InvalidOperationException("Wait for playback to finish changing sources.");
            IsBusy = true;
            try
            {
                await radioRepository.ImportStationsAsync([card.Station], cancellationToken);
                await RefreshRadioAsync(CancellationToken.None);
                StatusMessage = Loc.Format("Radio.CatalogSaved", card.Name);
            }
            finally { IsBusy = false; }
        });

    /// <summary>The saved station behind a card: the same catalog id, or the same stream address added by hand.</summary>
    public RadioStation? SavedStationFor(RadioCardViewModel card) =>
        (card.Station.CatalogId is { } catalogId && savedByCatalogId.TryGetValue(catalogId, out RadioStation? byId) ? byId : null) ??
        (savedByStream.TryGetValue(card.Station.Stream.AbsoluteUri, out RadioStation? byStream) ? byStream : null);

    /// <summary>The star on a card: saves a catalog station, or removes a saved one.</summary>
    public Task ToggleRadioCardSavedAsync(RadioCardViewModel card, CancellationToken cancellationToken = default) =>
        SavedStationFor(card) is { } saved ? RemoveRadioStationAsync(saved.Id, cancellationToken) : SaveCatalogRadioStationAsync(card, cancellationToken);

    public Task RemoveRadioStationAsync(Guid id, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(async () =>
        {
            if (radioRepository is null) throw new NotSupportedException("Radio storage is unavailable.");
            if (changingTrack) throw new InvalidOperationException("Wait for playback to finish changing sources.");
            IsBusy = true;
            try
            {
                if (IsRadioPlayback && playingRadioStation?.Id == id) await StopPlaybackAsync(cancellationToken);
                await radioRepository.RemoveStationAsync(id, cancellationToken);
                await RefreshRadioAsync(CancellationToken.None);
                StatusMessage = Loc.Get("Radio.Removed");
            }
            finally { IsBusy = false; }
        });

    public Task PlayRadioStationAsync(Guid id, CancellationToken cancellationToken = default) =>
        RunPlaybackOperation(() => PlayRadioStationCoreAsync(id, cancellationToken));

    private async Task PlayRadioStationCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        if (radioRepository is null || changingTrack || IsBusy) return;
        changingTrack = true; NotifyNavigationState(); CancelArtwork();
        try
        {
            RadioStation? stored = (await radioRepository.ListStationsAsync(cancellationToken)).FirstOrDefault(item => item.Id == id);
            // A catalog station can be played without being saved; it is then not counted in the play history.
            RadioStation station = stored ?? curatedCatalog?.Stations.FirstOrDefault(item => item.StationId == id)?.ToRadioStation()
                ?? throw new InvalidOperationException("The station no longer exists. Refresh Radio before playing.");
            station.Validate();
            playingRadioStation = station; radioConnectionRequested = true;
            StatusMessage = Loc.Format("Radio.Connecting", station.Name);
            playbackQueue.Replace([]); playQueue.CompleteCurrent(); isPlayingQueuedTrack = false; RefreshPlayQueue();
            Track track = new(station.Id, station.Stream, station.Name, station.Creator, "Radio", TimeSpan.Zero,
                Genre: station.Genre, Rating: station.Rating, Description: station.Description, Source: "Radio");
            UpdateRadioNowPlayingLogo(track);
            await FlushPodcastProgressAsync(cancellationToken);
            await ApplyInputRateAsync(false, cancellationToken);
            await playbackEngine.LoadAsync(track, cancellationToken);
            await playbackEngine.SetVolumeAsync(Volume, cancellationToken);
            await playbackEngine.SetMutedAsync(preferredMuted, cancellationToken);
            await playbackEngine.PlayAsync(cancellationToken);
            if (stored is not null)
            {
                await radioRepository.RecordStationPlayedAsync(id, DateTimeOffset.UtcNow, cancellationToken);
                await RefreshRadioAsync(cancellationToken, id);
            }
            if (playbackEngine.Snapshot.Status is not (PlaybackStatus.Ended or PlaybackStatus.Faulted))
                StatusMessage = playbackEngine.Snapshot.Status == PlaybackStatus.Playing ? Loc.Format("Radio.Playing", station.Name) : Loc.Format("Radio.Connecting", station.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { radioConnectionRequested = false; StatusMessage = Loc.Format("Radio.Failed", exception.Message); }
        finally { CompletePlaybackTransition(); }
    }

    private async Task RefreshRadioAsync(CancellationToken cancellationToken, Guid? select = null)
    {
        if (radioRepository is null) return;
        Guid? selected = select ?? SelectedRadioId;
        allRadioStations = await radioRepository.ListStationsAsync(cancellationToken);
        savedByCatalogId = allRadioStations.Where(station => station.CatalogId is not null)
            .GroupBy(station => station.CatalogId!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        savedByStream = allRadioStations.GroupBy(station => station.Stream.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        FilterRadioStations(selected);
    }

    private RadioFormatNotice? NoticeFor(string? format, Uri stream) =>
        RadioFormatNotice.For(format, stream, activePlaybackEngine == PlaybackEngineKind.Vlc, OperatingSystem.IsWindows());

    // A saved copy of a catalog station keeps its catalog id, which is how its format is found; stations the user typed in have none.
    private string? CatalogFormat(RadioStation station) =>
        station.CatalogId is { } id ? curatedCatalog?.Stations.FirstOrDefault(item => item.Id == id)?.UnsupportedFormat : null;

    private List<RadioCardViewModel> CatalogCards()
    {
        if (catalogCards is not null) return catalogCards;
        int order = 0;
        catalogCards = [.. (curatedCatalog?.Stations ?? []).Select(station => new RadioCardViewModel(station.ToRadioStation(), true, false, order++, NoticeFor(station.UnsupportedFormat, station.Stream)))];
        return catalogCards;
    }

    /// <summary>Raised when the user changed what the Radio grid shows (mode, search, genre, country or sort), so the grid should start at the top.</summary>
    public event EventHandler? RadioFilterChanged;

    private void FilterRadioStations(Guid? selected = null, bool resetScroll = false)
    {
        selected ??= SelectedRadioId;
        bool catalog = IsRadioCatalogMode;
        List<RadioCardViewModel> pool;
        if (catalog)
        {
            pool = radioCatalogEnabled ? CatalogCards() : [];
            foreach (RadioCardViewModel card in pool) card.IsSaved = SavedStationFor(card) is not null;
        }
        else
        {
            int order = 0;
            pool = [.. allRadioStations.OrderBy(station => station.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(station => new RadioCardViewModel(station, false, true, order++, NoticeFor(CatalogFormat(station), station.Stream)) { Logo = CachedLogo(station.LogoUri) })];
        }
        RebuildRadioCountries(pool);
        List<RadioCardViewModel> searched = [.. pool.Where(card => Matches(card, SearchQuery) &&
            (radioCountryFilter is null || string.Equals(card.CountryCode, radioCountryFilter, StringComparison.OrdinalIgnoreCase)))];
        RebuildRadioGenres(searched);
        IEnumerable<RadioCardViewModel> shown = radioGenreFilter is null ? searched
            : searched.Where(card => string.Equals(card.Genre.Trim(), radioGenreFilter, StringComparison.OrdinalIgnoreCase));
        shown = Sort(shown, catalog);
        RadioCards.Clear();
        foreach (RadioCardViewModel card in shown) RadioCards.Add(card);
        syncingRadioSelection = true;
        try
        {
            if (!catalog)
            {
                RadioStations.Clear();
                foreach (RadioCardViewModel card in RadioCards) RadioStations.Add(card.Station);
                SelectedRadioCard = RadioCards.FirstOrDefault(card => card.Id == selected) ?? RadioCards.FirstOrDefault();
                SelectedRadioStation = SelectedRadioCard?.Station;
            }
            else
            {
                SelectedRadioCard = RadioCards.FirstOrDefault(card => card.Id == selected);
            }
        }
        finally { syncingRadioSelection = false; }
        RadioEmptyText = catalog || allRadioStations.Count > 0 ? Loc.Get("Radio.NoMatches") : Loc.Get("Main.NoStations");
        OnPropertyChanged(nameof(IsRadioEmpty));
        OnPropertyChanged(nameof(CanPlay));
        if (IsRadioView) StatusMessage = Loc.Plural("Radio.StationCount", RadioCards.Count);
        if (resetScroll) RadioFilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool Matches(RadioCardViewModel card, string query) => query.Length == 0 ||
        card.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || card.Genre.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        card.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) || card.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        card.Country.Contains(query, StringComparison.OrdinalIgnoreCase);

    // Play history lives with the saved station, so a catalog card sorts by the history of its saved copy.
    private IEnumerable<RadioCardViewModel> Sort(IEnumerable<RadioCardViewModel> cards, bool catalog)
    {
        RadioStation Data(RadioCardViewModel card) => catalog ? SavedStationFor(card) ?? card.Station : card.Station;
        StringComparer names = StringComparer.CurrentCultureIgnoreCase;
        return RadioSortIndex switch
        {
            1 => cards.OrderBy(card => card.Name, names),
            2 => cards.OrderByDescending(card => Data(card).LastPlayed ?? DateTimeOffset.MinValue).ThenBy(card => card.Order),
            3 => cards.OrderByDescending(card => Data(card).PlayCount).ThenBy(card => card.Order),
            4 => cards.OrderByDescending(card => Data(card).Rating).ThenBy(card => card.Order),
            // Most popular: the curated order for the catalog; for saved stations, the ones played most often, then by name.
            _ => catalog ? cards.OrderBy(card => card.Order) : cards.OrderByDescending(card => card.Station.PlayCount).ThenBy(card => card.Order)
        };
    }

    private Avalonia.Media.Imaging.Bitmap? CachedLogo(Uri? uri) => uri is not null && radioCatalogEnabled && radioLogoCache.TryGetValue(uri, out var bitmap) ? bitmap : null;

    /// <summary>
    /// Brings a drop-down's items up to date with the least change. Clearing and refilling the list while the user is picking from
    /// it makes the combo box lose its selection and show nothing, so entries that are still wanted keep their place and instance.
    /// </summary>
    private static void SyncChoices<T>(ObservableCollection<T> items, IReadOnlyList<T> wanted) where T : class
    {
        for (int index = 0; index < wanted.Count; index++)
        {
            if (index < items.Count && items[index].Equals(wanted[index])) continue;
            items.Insert(index, wanted[index]);
        }
        while (items.Count > wanted.Count) items.RemoveAt(items.Count - 1);
    }

    private void RebuildRadioCountries(List<RadioCardViewModel> cards)
    {
        List<RadioCountryChoice> rows = [new RadioCountryChoice(null, Loc.Get("Radio.AllCountries"))];
        rows.AddRange(cards.Where(card => card.CountryCode.Length > 0)
            .GroupBy(card => card.CountryCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RadioCountryChoice(group.Key, group.First().Country.Length > 0 ? group.First().Country : group.Key))
            .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase));
        if (radioCountryFilter is not null && !rows.Any(row => string.Equals(row.Code, radioCountryFilter, StringComparison.OrdinalIgnoreCase)))
            radioCountryFilter = null;
        updatingRadioFilters = true;
        try
        {
            SyncChoices(RadioCountries, rows);
            SelectedRadioCountry = RadioCountries.FirstOrDefault(row => string.Equals(row.Code, radioCountryFilter, StringComparison.OrdinalIgnoreCase)) ?? RadioCountries[0];
        }
        finally { updatingRadioFilters = false; }
    }

    private void RebuildRadioGenres(List<RadioCardViewModel> cards)
    {
        List<RadioGenreRow> rows = [new RadioGenreRow(null, Loc.Get("Radio.AllStations"), cards.Count, Loc.Get("Radio.AllGenres"))];
        rows.AddRange(cards.Where(card => card.Genre.Trim().Length > 0)
            .GroupBy(card => card.Genre.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new RadioGenreRow(group.Key, group.Key, group.Count(), group.Key)));
        // A genre with no stations left (removed, or filtered out by the search) falls back to All stations.
        if (radioGenreFilter is not null && !rows.Any(row => string.Equals(row.Genre, radioGenreFilter, StringComparison.OrdinalIgnoreCase)))
            radioGenreFilter = null;
        updatingRadioFilters = true;
        try
        {
            SyncChoices(RadioGenreChoices, rows);
            SelectedRadioGenreChoice = RadioGenreChoices.FirstOrDefault(row => string.Equals(row.Genre, radioGenreFilter, StringComparison.OrdinalIgnoreCase)) ?? RadioGenreChoices[0];
            string filter = RadioGenreSearch.Trim();
            SyncChoices(RadioGenres, [.. rows.Where(row => row.Genre is null || filter.Length == 0 ||
                row.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                string.Equals(row.Genre, radioGenreFilter, StringComparison.OrdinalIgnoreCase))]);
            SelectedRadioGenre = RadioGenres.FirstOrDefault(row => string.Equals(row.Genre, radioGenreFilter, StringComparison.OrdinalIgnoreCase)) ?? RadioGenres[0];
        }
        finally { updatingRadioFilters = false; }
    }

    partial void OnSelectedRadioGenreChanged(RadioGenreRow? value)
    {
        if (updatingRadioFilters || value is null) return;
        radioGenreFilter = value.Genre;
        FilterRadioStations(resetScroll: true);
    }

    partial void OnSelectedRadioGenreChoiceChanged(RadioGenreRow? value)
    {
        if (updatingRadioFilters || value is null) return;
        radioGenreFilter = value.Genre;
        FilterRadioStations(resetScroll: true);
    }

    partial void OnSelectedRadioCountryChanged(RadioCountryChoice? value)
    {
        if (updatingRadioFilters || value is null) return;
        radioCountryFilter = value.Code;
        FilterRadioStations(resetScroll: true);
    }

    partial void OnRadioGenreSearchChanged(string value)
    {
        if (activeView == LibraryViewKind.Radio) FilterRadioStations(resetScroll: true);
    }

    partial void OnRadioSortIndexChanged(int value) => FilterRadioStations(resetScroll: true);

    partial void OnSelectedRadioCardChanged(RadioCardViewModel? value)
    {
        if (syncingRadioSelection || value is null || value.IsCatalogCard) return;
        syncingRadioSelection = true;
        try { SelectedRadioStation = value.Station; }
        finally { syncingRadioSelection = false; }
    }

    partial void OnSelectedRadioStationChanged(RadioStation? value)
    {
        if (syncingRadioSelection) return;
        syncingRadioSelection = true;
        try { SelectedRadioCard = value is null ? null : RadioCards.FirstOrDefault(card => card.Id == value.Id && !card.IsCatalogCard); }
        finally { syncingRadioSelection = false; }
    }

    /// <summary>
    /// Fetches a card's logo once the card scrolls into view. Nothing is requested until the user has enabled the catalog, and
    /// a missing, slow or broken logo simply leaves the coloured initials tile.
    /// </summary>
    public async Task RequestRadioLogoAsync(RadioCardViewModel card)
    {
        if (!radioCatalogEnabled || radioLogos is null || card.LogoRequested || card.Logo is not null ||
            card.LogoUri is not { Scheme: "https" } uri) return;
        card.LogoRequested = true;
        CancellationToken token = radioLogoCancellation.Token;
        try
        {
            Avalonia.Media.Imaging.Bitmap? bitmap = await radioLogos.LoadAsync(uri, token);
            if (bitmap is null || token.IsCancellationRequested || !radioCatalogEnabled) return;
            radioLogoCache[uri] = bitmap;
            card.Logo = bitmap;
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { card.LogoRequested = false; }
    }

    /// <summary>The logo shown in the Now playing panel while a station with a downloaded logo plays; null shows the normal cover.</summary>
    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? radioNowPlayingLogo;

    private Uri? radioNowPlayingLogoUri;

    /// <summary>
    /// Follows the playing track: a station's logo replaces the cover, and anything else restores it. Like the cards, the logo is only
    /// fetched after the user enabled the catalog and only over https.
    /// </summary>
    private void UpdateRadioNowPlayingLogo(Track? track)
    {
        Uri? wanted = radioCatalogEnabled && track is not null && playingRadioStation is { } station &&
            station.Id == track.Id && station.Stream == track.Location && station.LogoUri is { Scheme: "https" } logo ? logo : null;
        if (wanted == radioNowPlayingLogoUri) return;
        radioNowPlayingLogoUri = wanted;
        RadioNowPlayingLogo = wanted is not null && radioLogoCache.TryGetValue(wanted, out Avalonia.Media.Imaging.Bitmap? cached) ? cached : null;
        if (wanted is not null && RadioNowPlayingLogo is null) _ = LoadRadioNowPlayingLogoAsync(wanted);
    }

    private async Task LoadRadioNowPlayingLogoAsync(Uri uri)
    {
        if (radioLogos is null) return;
        CancellationToken token = radioLogoCancellation.Token;
        try
        {
            Avalonia.Media.Imaging.Bitmap? bitmap = await radioLogos.LoadAsync(uri, token);
            if (bitmap is null || token.IsCancellationRequested || !radioCatalogEnabled) return;
            radioLogoCache[uri] = bitmap;
            if (radioNowPlayingLogoUri == uri) RadioNowPlayingLogo = bitmap;
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
    }

    private void NotifyRadioPlaybackState()
    {
        OnPropertyChanged(nameof(IsRadioPlayback)); OnPropertyChanged(nameof(ShowPlaybackPause));
        OnPropertyChanged(nameof(ShowRadioStop)); OnPropertyChanged(nameof(ShowPlaybackPlay)); OnPropertyChanged(nameof(CanSeekPlayback)); OnPropertyChanged(nameof(ShowSeekBar));
        OnPropertyChanged(nameof(DurationText));
    }
}

/// <summary>One entry of the Radio genre list: <see cref="Genre"/> is null for "All stations". <see cref="ChoiceName"/> is its text in the genre drop-down.</summary>
public sealed record RadioGenreRow(string? Genre, string Name, int Count, string? ChoiceName = null);

/// <summary>One entry of the Radio country drop-down: <see cref="Code"/> is null for "All countries".</summary>
public sealed record RadioCountryChoice(string? Code, string Name);
