using Aibell.Desktop.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using Aibell.Desktop.Services;
using Aibell.Application.Podcasts;
using Aibell.Domain.Podcasts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public enum PodcastEpisodeState
{
    NotDownloaded,
    Downloading,
    Downloaded,
    Queued,
    Failed,
    Cancelled,
    Missing,
    Deleting
}

/// <summary>An observable list that can swap its whole content with a single Reset notification (hundreds of rows).</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (T item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}

public sealed partial class PodcastSubscriptionItem(PodcastSubscription subscription) : ObservableObject
{
    public PodcastSubscription Subscription { get; } = subscription;
    public Guid Id => Subscription.Id;
    public string Title => Subscription.Title;

    /// <summary>Episodes a refresh found that the user has not played, downloaded or dismissed yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private int newCount;

    public string Detail => (Subscription.LastRefreshed is { } time
        ? Loc.Format("Pod.Updated", time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : Loc.Get("Pod.NeverRefreshed"))
        + (NewCount > 0 ? Loc.Format("Pod.NewSuffix", NewCount) : "");
}

public sealed partial class PodcastEpisodeItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(CanDownload), nameof(CanCancel), nameof(CanPlay), nameof(DownloadText), nameof(CanResume),
        nameof(PlayText), nameof(CanStartOver), nameof(CanDeleteDownload), nameof(ShowPlay), nameof(ShowStartOver), nameof(IsDownloading))]
    private PodcastEpisodeState state;

    /// <summary>True while the shared player is actively playing this episode's file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(ShowPlay), nameof(ShowStartOver))]
    private bool isPlaying;

    /// <summary>Decoded thumbnail of this episode's own cover, or null (the placeholder is shown instead).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover), nameof(ShowCoverPlaceholder), nameof(CoverAutomationName))]
    private Avalonia.Media.Imaging.Bitmap? cover;

    internal bool CoverRequested { get; set; }
    public Uri? CoverUrl => Episode.ImageUrl;
    public bool HasCover => Cover is not null;
    public bool ShowCoverPlaceholder => Cover is null;
    public string CoverAutomationName => HasCover ? Loc.Get("Pod.CoverAutomation") : Loc.Get("Pod.NoCoverAutomation");
    /// <summary>Bytes written by the current download attempt and the expected total, when known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadPercent), nameof(DownloadProgressText), nameof(StateText))]
    private long downloadReceived;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadPercent), nameof(DownloadProgressText), nameof(DownloadIndeterminate), nameof(StateText))]
    private long? downloadTotal;

    public bool IsDownloading => State == PodcastEpisodeState.Downloading;
    public bool DownloadIndeterminate => DownloadTotal is not > 0;
    public double DownloadPercent => DownloadTotal is > 0 and { } total ? Math.Clamp(DownloadReceived * 100.0 / total, 0, 100) : 0;
    public string DownloadProgressText => DownloadTotal is > 0 and { } total
        ? Loc.Format("Pod.SizeOf", FormatSize(DownloadReceived), FormatSize(total), DownloadPercent.ToString("0", CultureInfo.CurrentCulture))
        : DownloadReceived > 0 ? FormatSize(DownloadReceived) : Loc.Get("Pod.Starting");

    /// <summary>Show notes as plain text; HTML is stripped and nothing is rendered.</summary>
    /// <summary>Found by a refresh and not yet played, downloaded or dismissed.</summary>
    [ObservableProperty]
    private bool isNew;

    public string NotesText => PodcastNotes.ToPlainText(Episode.Description);
    public bool HasNotes => NotesText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotesToggleText))]
    private bool notesExpanded;

    public string NotesToggleText => NotesExpanded ? Loc.Get("Pod.HideNotes") : Loc.Get("Pod.ShowNotes");

    public bool CanPause => IsPlaying;
    public bool ShowPlay => CanPlay && !IsPlaying;
    public bool ShowStartOver => CanStartOver && !IsPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private string error = "";

    public PodcastEpisodeItem(StoredPodcastEpisode episode, PodcastEpisodeState state)
    {
        Episode = episode; this.state = state; isNew = episode.IsNew;
    }

    public StoredPodcastEpisode Episode { get; private set; }
    public string Title => Episode.Title;
    public string EpisodeId => Episode.EpisodeId;
    public string Meta => string.Join(" · ", new[]
    {
        Episode.Published?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
        Episode.Duration is { } duration ? FormatDuration(duration) : null,
        Episode.Length is { } length and > 0 ? FormatSize(length) : null
    }.Where(part => part is not null));
    public string StateText => State switch
    {
        PodcastEpisodeState.Downloading => Loc.Format("Pod.State.Downloading", DownloadProgressText),
        PodcastEpisodeState.Queued => Loc.Get("Pod.State.Queued"),
        PodcastEpisodeState.Downloaded => Loc.Get("Pod.State.Downloaded") + (Episode.DownloadedBytes is { } bytes ? " · " + FormatSize(bytes) : ""),
        PodcastEpisodeState.Failed => Loc.Format("Pod.State.Failed", Error),
        PodcastEpisodeState.Cancelled => Loc.Get("Pod.State.Cancelled"),
        PodcastEpisodeState.Missing => Loc.Get("Pod.State.Missing"),
        PodcastEpisodeState.Deleting => Loc.Get("Pod.State.Deleting"),
        _ => Loc.Get("Pod.State.NotDownloaded")
    };
    public string SavedPositionText => Episode.SavedProgress is { } progress
        ? Loc.Format("Pod.SavedPosition", FormatDuration(progress.Position), FormatDuration(progress.Duration)) : Loc.Get("Pod.NoSavedPosition");
    public bool CanResume => CanPlay && Episode.SavedProgress is { CanResume: true };
    public bool CanDeleteDownload => State is PodcastEpisodeState.Downloaded or PodcastEpisodeState.Missing;
    public int Rating => Math.Clamp(Episode.Rating, 0, 5);
    public string RatingText => new string('★', Rating) + new string('☆', 5 - Rating);
    public string RatingAutomationName => Rating == 0 ? Loc.Get("Pod.RatingUnrated") : Loc.Format("Pod.RatingStars", Rating);
    public string PlayText => CanResume ? Loc.Get("Pod.Resume") : Loc.Get("Playback.Play");
    public bool CanStartOver => CanResume;
    public bool HasProgress => Episode.SavedProgress is { Duration.Ticks: > 0 };
    /// <summary>Saved position as 0–100, derived from the stored (or latest automatic) progress; drives the per-row bar.</summary>
    public double ProgressPercent => Episode.SavedProgress is { Duration.Ticks: > 0 } progress
        ? Math.Clamp(progress.Position.TotalSeconds / progress.Duration.TotalSeconds * 100, 0, 100) : 0;
    public string ProgressAutomationName => HasProgress ? Loc.Format("Pod.ProgressPercent", ProgressPercent.ToString("0", CultureInfo.CurrentCulture)) : Loc.Get("Pod.NoProgress");
    public string DownloadText => State is PodcastEpisodeState.Failed or PodcastEpisodeState.Cancelled or PodcastEpisodeState.Missing ? Loc.Get("Pod.Retry") : Loc.Get("Pod.Download");
    public bool CanDownload => State is PodcastEpisodeState.NotDownloaded or PodcastEpisodeState.Failed or PodcastEpisodeState.Cancelled or PodcastEpisodeState.Missing;
    public bool CanCancel => State is PodcastEpisodeState.Downloading or PodcastEpisodeState.Queued;
    public bool CanPlay => State == PodcastEpisodeState.Downloaded;

    internal void Update(StoredPodcastEpisode episode)
    {
        if (episode.ImageUrl != Episode.ImageUrl) { Cover = null; CoverRequested = false; }
        Episode = episode; IsNew = episode.IsNew;
        OnPropertyChanged(nameof(NotesText)); OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Meta)); OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(SavedPositionText)); OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(PlayText)); OnPropertyChanged(nameof(CanStartOver)); OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressPercent)); OnPropertyChanged(nameof(ProgressAutomationName));
        NotifyRating();
    }

    /// <summary>Applies only the saved progress to the row's latest snapshot; stars, title and download state stay as they are.</summary>
    internal void ApplyProgress(PodcastPlaybackProgress? progress) => Update(Episode with { SavedProgress = progress });

    /// <summary>Applies only the star rating to the row's latest snapshot; progress, title and download state stay as they are.</summary>
    internal void ApplyRating(int rating)
    {
        Episode = Episode with { Rating = rating };
        NotifyRating();
    }

    private void NotifyRating()
    {
        OnPropertyChanged(nameof(Rating)); OnPropertyChanged(nameof(RatingText)); OnPropertyChanged(nameof(RatingAutomationName));
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}" : $"{duration.Minutes}:{duration.Seconds:00}";
    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1048576d).ToString("0.#", CultureInfo.CurrentCulture) + " MB" : Math.Max(1, bytes / 1024).ToString(CultureInfo.CurrentCulture) + " KB";
}

/// <summary>
/// Drives the standalone Podcasts window. Opening it only reads the local catalog; the network is touched solely by
/// <see cref="SubscribeAsync"/>, <see cref="RefreshSelectedAsync"/> and <see cref="DownloadAsync"/>, each started by the user.
/// </summary>
public sealed partial class PodcastsViewModel : ObservableObject, IDisposable
{
    private readonly PodcastService service;
    private readonly IPodcastPlaybackHandoff? handoff;
    private readonly Dictionary<(Guid, string), PodcastEpisodeItem> inFlight = [];
    private readonly HashSet<Task> running = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim ratingGate = new(1, 1);
    private CancellationTokenSource? feedOperation;
    private CancellationTokenSource? positionOperation;
    private Task episodeLoad = Task.CompletedTask;
    private int selectionVersion;
    private int positionSelectionVersion;
    private int catalogVersion;
    private bool shutdown;
    private bool disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubscribe))]
    private string feedUrl = "";

    [ObservableProperty]
    private string statusMessage = Loc.Get("Pod.EnterFeed");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubscribe), nameof(CanRefresh), nameof(CanRemove), nameof(IsWorking), nameof(CanRefreshAll))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh), nameof(CanRemove), nameof(SelectedTitle), nameof(SelectedDescription))]
    private PodcastSubscriptionItem? selectedSubscription;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResumeSelected), nameof(CanSaveSelectedPosition))]
    private PodcastEpisodeItem? selectedEpisode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResumeSelected), nameof(CanSaveSelectedPosition), nameof(IsWorking))]
    private bool isPositionBusy;

    public bool IsWorking => IsBusy || IsPositionBusy;

    public bool CanResumeSelected => !shutdown && !IsPositionBusy && handoff is not null && SelectedEpisode is { CanResume: true };
    public bool CanSaveSelectedPosition => !shutdown && !IsPositionBusy && service.SupportsPositions && handoff is IPodcastPositionSource && SelectedEpisode is { CanPlay: true };

    public ObservableCollection<PodcastSubscriptionItem> Subscriptions { get; } = [];

    /// <summary>The episodes of the selected show that match <see cref="SearchText"/>. The full cached list is kept in memory.</summary>
    public BulkObservableCollection<PodcastEpisodeItem> Episodes { get; } = [];

    private List<PodcastEpisodeItem> cachedEpisodes = [];
    private bool applyingFilter;

    [ObservableProperty]
    private string searchText = "";

    /// <summary>Shows only episodes whose audio is downloaded. Combines with the title search.</summary>
    [ObservableProperty]
    private bool downloadedOnly;

    partial void OnDownloadedOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFiltered));
        ApplyEpisodeFilter();
    }

    public bool IsFiltered => HasSearch || DownloadedOnly;
    public bool HasSearch => SearchText.Trim().Length > 0;
    public bool HasNoMatches => cachedEpisodes.Count > 0 && Episodes.Count == 0;
    public string EpisodeCountText => cachedEpisodes.Count == 0 ? ""
        : IsFiltered ? Loc.Format("Pod.EpisodeCountFiltered", Episodes.Count, cachedEpisodes.Count)
        : Loc.Plural("Pod.EpisodeCount", cachedEpisodes.Count);
    public string NoMatchesText => DownloadedOnly
        ? (HasSearch ? Loc.Format("Pod.NoDownloadedMatch", SearchText.Trim()) : Loc.Get("Pod.NoneDownloaded"))
        : Loc.Format("Pod.NoMatch", SearchText.Trim(), cachedEpisodes.Count);

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearch)); OnPropertyChanged(nameof(IsFiltered));
        ApplyEpisodeFilter();
    }

    /// <summary>Clears the title search and shows every cached episode of the selected show again.</summary>
    public void ClearSearch() => SearchText = "";

    /// <summary>Clears the title search and the downloaded-only filter.</summary>
    public void ClearFilters() { SearchText = ""; DownloadedOnly = false; }

    // Filtering is an in-memory view change: it never reaches the network, and it must not cancel a Play/Resume or
    // Save that is already underway, so selection changes made here skip the position invalidation.
    private void ApplyEpisodeFilter()
    {
        string query = SearchText.Trim();
        PodcastEpisodeItem? keep = SelectedEpisode;
        applyingFilter = true;
        try
        {
            bool downloaded = DownloadedOnly;
            Episodes.ReplaceAll(query.Length == 0 && !downloaded ? cachedEpisodes
                : cachedEpisodes.Where(item => (!downloaded || item.State == PodcastEpisodeState.Downloaded) &&
                    (query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))));
            SelectedEpisode = keep is not null && Episodes.Contains(keep) ? keep : null;
        }
        finally { applyingFilter = false; }
        OnPropertyChanged(nameof(HasEpisodes)); OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(EpisodeCountText)); OnPropertyChanged(nameof(NoMatchesText));
        OnPropertyChanged(nameof(CanResumeSelected)); OnPropertyChanged(nameof(CanSaveSelectedPosition));
    }

    partial void OnSelectedEpisodeChanged(PodcastEpisodeItem? oldValue, PodcastEpisodeItem? newValue)
    {
        if (!applyingFilter) InvalidatePositionSelection();
        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedEpisodeStateChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedEpisodeStateChanged;
    }

    private void OnSelectedEpisodeStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(PodcastEpisodeItem.CanPlay) or nameof(PodcastEpisodeItem.CanResume))
        {
            OnPropertyChanged(nameof(CanResumeSelected));
            OnPropertyChanged(nameof(CanSaveSelectedPosition));
        }
    }

    public bool CanSubscribe => !IsBusy && !shutdown && FeedUrl.Trim().Length > 0;
    public bool CanRefresh => !IsBusy && !shutdown && SelectedSubscription is not null;
    public bool CanRemove => CanRefresh;
    public bool HasSubscriptions => Subscriptions.Count > 0;
    public bool HasEpisodes => cachedEpisodes.Count > 0;

    public PodcastsViewModel(PodcastService service, IPodcastPlaybackHandoff? handoff = null, ICoverImageLoader? covers = null)
    {
        this.covers = covers;
        ArgumentNullException.ThrowIfNull(service);
        this.service = service; this.handoff = handoff;
        // Automatic progress is saved with the pane hidden or filtered, so listen for the app's lifetime rather than the view's.
        if (handoff is IPodcastCatalogNotifications catalog) { catalogNotifications = catalog; catalog.PodcastCatalogRefreshed += OnCatalogRefreshed; }
        if (handoff is IPodcastPlaybackControl playback) { control = playback; playback.PodcastPlayingChanged += OnPlayingChanged; }
        if (handoff is IPodcastProgressNotifications notifications) { progressNotifications = notifications; notifications.PositionSaved += OnPositionSaved; }
    }

    private readonly IPodcastProgressNotifications? progressNotifications;
    private readonly IPodcastPlaybackControl? control;
    private readonly ICoverImageLoader? covers;
    private readonly IPodcastCatalogNotifications? catalogNotifications;

    /// <summary>Speeds offered for podcast playback, in the order shown.</summary>
    public static IReadOnlyList<string> SpeedLabels { get; } = ["1x", "1.25x", "1.5x", "2x"];
    private static readonly double[] SpeedValues = [1.0, 1.25, 1.5, 2.0];

    public bool CanChooseSpeed => control is not null;

    /// <summary>Index into <see cref="SpeedLabels"/>. Choosing one applies to the playing episode and to later episodes.</summary>
    public int SelectedSpeedIndex
    {
        get => control is null ? 0 : Math.Max(0, Array.IndexOf(SpeedValues, control.PodcastPlaybackRate));
        set
        {
            if (control is null || shutdown || value < 0 || value >= SpeedValues.Length || value == SelectedSpeedIndex) return;
            _ = SetSpeedAsync(SpeedValues[value]);
        }
    }

    private async Task SetSpeedAsync(double rate)
    {
        try { await control!.SetPodcastPlaybackRateAsync(rate, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!shutdown) StatusMessage = Loc.Format("Pod.SpeedFailed", exception.Message); }
        finally
        {
            OnPropertyChanged(nameof(SelectedSpeedIndex));
            if (!shutdown) StatusMessage = Loc.Format("Pod.SpeedSet", SpeedLabels[SelectedSpeedIndex]);
        }
    }

    /// <summary>Loads a row's cover when it first becomes visible. The row keeps the placeholder when it has no cover or loading fails.</summary>
    public async Task EnsureCoverAsync(PodcastEpisodeItem? item)
    {
        if (item is null || covers is null || shutdown || item.Cover is not null || item.CoverRequested || item.CoverUrl is not { } url) return;
        item.CoverRequested = true;
        try
        {
            Avalonia.Media.Imaging.Bitmap? bitmap = await covers.LoadAsync(url, lifetime.Token);
            // The row may have been recycled for a different cover while the load was pending.
            if (!shutdown && bitmap is not null && item.CoverUrl == url) item.Cover = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* A cover is decoration; the placeholder stays. */ }
    }

    private void OnPlayingChanged(object? sender, EventArgs args) => RefreshPlaying();

    /// <summary>Marks the row whose download the shared player is playing; compares stored paths only, so it is cheap for long lists.</summary>
    private void RefreshPlaying()
    {
        if (shutdown) return;
        string? playing = control?.PodcastPlayingFile?.LocalPath;
        foreach (PodcastEpisodeItem item in cachedEpisodes)
            item.IsPlaying = playing is not null && item.Episode.LocalPath is { } path && string.Equals(path, playing,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Pauses the shared player when it is playing this row's episode.</summary>
    public Task PauseAsync(PodcastEpisodeItem? item) => RunTracked(async () =>
    {
        if (item is not { IsPlaying: true } || control?.PodcastPlayingFile is not { } file || shutdown) return;
        try { await control.PausePodcastAsync(file, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { StatusMessage = Loc.Format("Pod.PauseFailed", exception.Message); }
    });

    // Raised from the progress worker thread.
    private void OnPositionSaved(object? sender, StoredPodcastEpisode saved) =>
        Dispatcher.UIThread.Post(() => ApplyAutomaticProgress(saved));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (catalogNotifications is not null) catalogNotifications.PodcastCatalogRefreshed -= OnCatalogRefreshed;
        if (control is not null) control.PodcastPlayingChanged -= OnPlayingChanged;
        if (progressNotifications is not null) progressNotifications.PositionSaved -= OnPositionSaved;
        if (SelectedEpisode is { } selected) selected.PropertyChanged -= OnSelectedEpisodeStateChanged;
        shutdown = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }
    public string SelectedTitle => SelectedSubscription?.Title ?? Loc.Get("Pod.NoneSelected");
    public string SelectedDescription => SelectedSubscription?.Subscription.Description ?? "";

    partial void OnSelectedSubscriptionChanged(PodcastSubscriptionItem? value)
    {
        InvalidatePositionSelection();
        SelectedEpisode = null;
        episodeLoad = RunTracked(() => LoadSelectedEpisodesAsync(value));
    }

    private void InvalidatePositionSelection()
    {
        positionSelectionVersion++;
        try { positionOperation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private bool CanPublishPositionResult(int version) => !shutdown && version == positionSelectionVersion;

    /// <summary>Completes when the episode list for the current selection has been read from the cache.</summary>
    public Task WhenEpisodesLoadedAsync() => episodeLoad;

    /// <summary>Reads cached subscriptions only. Safe offline.</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default) => RunTracked(async () =>
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        try { await ReloadSubscriptionsAsync(SelectedSubscription?.Id, linked.Token); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        { if (!shutdown) StatusMessage = Loc.Format("Pod.CatalogFailed", exception.Message); }
    });

    public Task SubscribeAsync() => RunFeedOperationAsync(async token =>
    {
        string text = FeedUrl;
        if (PodcastSubscription.NormalizeFeedUrl(text) is null)
        { StatusMessage = Loc.Get("Pod.BadUrl"); return; }
        StatusMessage = Loc.Get("Pod.Fetching");
        PodcastSubscribeResult result = await service.SubscribeAsync(text, token);
        if (shutdown) return;
        FeedUrl = "";
        await ReloadSubscriptionsAsync(result.Subscription.Id, lifetime.Token);
        if (shutdown) return;
        StatusMessage = result.Added
            ? Loc.Format("Pod.Subscribed", result.Subscription.Title, result.EpisodeCount, IssueSuffix(result.Issues))
            : Loc.Format("Pod.AlreadySubscribed", result.Subscription.Title);
    }, Loc.Get("Pod.Op.Subscribe"));

    public Task RefreshSelectedAsync() => RunFeedOperationAsync(async token =>
    {
        if (SelectedSubscription is not { } selected) return;
        StatusMessage = Loc.Format("Pod.Refreshing", selected.Title);
        PodcastRefreshResult result = await service.RefreshAsync(selected.Id, token);
        if (shutdown) return;
        await ReloadSubscriptionsAsync(selected.Id, lifetime.Token);
        if (shutdown) return;
        StatusMessage = result.NotModified ? Loc.Format("Pod.NoChanges", selected.Title)
            : Loc.Format("Pod.Refreshed", result.Subscription.Title, result.NewEpisodes, result.EpisodeCount, IssueSuffix(result.Issues));
    }, Loc.Get("Pod.Op.Refresh"));

    public bool CanRefreshAll => !IsBusy && !shutdown && Subscriptions.Count > 0;

    /// <summary>Checks every subscription for new episodes, one after another.</summary>
    public Task RefreshAllAsync() => RunFeedOperationAsync(async token =>
    {
        Progress<PodcastRefreshAllProgress> progress = new(report =>
        { if (!shutdown) StatusMessage = Loc.Format("Pod.RefreshingOf", report.Position, report.Total, report.Title); });
        Guid? keep = SelectedSubscription?.Id;
        PodcastRefreshAllResult result = await service.RefreshAllAsync(progress, token);
        if (shutdown) return;
        await ReloadSubscriptionsAsync(keep, lifetime.Token);
        if (shutdown) return;
        string summary = result.NewEpisodes == 0 ? Loc.Get("Pod.NoNewEpisodes") : Loc.Plural("Pod.NewEpisodes", result.NewEpisodes);
        StatusMessage = Loc.Format("Pod.Checked", result.Refreshed, result.Subscriptions, summary,
            result.Failures.Count == 0 ? "" : Loc.Format("Pod.FailedSuffix", result.Failures.Count, string.Join("; ", result.Failures.Take(3).Select(failure => failure.Title))));
    }, Loc.Get("Pod.Op.RefreshAll"));

    public bool HasNewEpisodes => cachedEpisodes.Any(item => item.IsNew);
    public bool CanDownloadNew => !shutdown && cachedEpisodes.Any(item => item.IsNew && item.CanDownload);
    public const int MaximumNewDownloadsPerRequest = 10;

    /// <summary>Queues the newest episodes marked new (at most ten per request, so a long backlog is never downloaded by accident).</summary>
    public async Task DownloadNewAsync()
    {
        if (shutdown || SelectedSubscription is not { } owner) return;
        List<PodcastEpisodeItem> candidates = cachedEpisodes.Where(item => item.IsNew && item.CanDownload && item.Episode.SubscriptionId == owner.Id).ToList();
        List<PodcastEpisodeItem> chosen = candidates.Take(MaximumNewDownloadsPerRequest).ToList();
        foreach (PodcastEpisodeItem item in chosen) TrackTask(EnqueueDownloadAsync(item));
        await MarkSeenAsync(chosen);
        StatusMessage = chosen.Count == 0 ? Loc.Get("Pod.NoNewToDownload")
            : candidates.Count > chosen.Count ? Loc.Format("Pod.QueuedPartial", chosen.Count, candidates.Count)
            : Loc.Plural("Pod.QueuedNew", chosen.Count);
    }

    /// <summary>Dismisses one episode's "new" mark.</summary>
    public Task MarkSeenAsync(PodcastEpisodeItem? item) => item is null ? Task.CompletedTask : MarkSeenAsync([item]);

    private async Task MarkSeenAsync(IReadOnlyList<PodcastEpisodeItem> items)
    {
        if (shutdown) return;
        PodcastEpisodeItem[] fresh = items.Where(item => item.IsNew).ToArray();
        if (fresh.Length == 0) return;
        foreach (IGrouping<Guid, PodcastEpisodeItem> group in fresh.GroupBy(item => item.Episode.SubscriptionId))
        {
            try { await service.MarkEpisodesSeenAsync(group.Key, group.Select(item => item.EpisodeId).ToArray(), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) { if (!shutdown) StatusMessage = Loc.Format("Pod.NewUpdateFailed", exception.Message); return; }
            foreach (PodcastEpisodeItem item in group) item.IsNew = false;
            if (Subscriptions.FirstOrDefault(subscription => subscription.Id == group.Key) is { } owner)
                owner.NewCount = Math.Max(0, owner.NewCount - group.Count());
        }
    }

    /// <summary>Dismisses every "new" mark of the selected podcast.</summary>
    public async Task MarkAllSeenAsync()
    {
        if (shutdown || SelectedSubscription is not { } owner) return;
        try { await service.MarkEpisodesSeenAsync(owner.Id, null, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) { if (!shutdown) StatusMessage = Loc.Format("Pod.NewUpdateFailed", exception.Message); return; }
        foreach (PodcastEpisodeItem item in cachedEpisodes) item.IsNew = false;
        owner.NewCount = 0;
        StatusMessage = Loc.Format("Pod.MarkedSeen", owner.Title);
    }

    // The hourly check runs without this view; pick up what it found. Nothing is touched while the user is working
    // (a feed operation or a play/save handoff), and an unchanged catalog never resets the list or the selection.
    private void OnCatalogRefreshed(object? sender, PodcastRefreshAllResult result)
    {
        if (result.NewEpisodes == 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (shutdown || IsBusy || IsPositionBusy) return;
            TrackTask(ApplyBackgroundRefreshAsync());
        });
    }

    private async Task ApplyBackgroundRefreshAsync()
    {
        IReadOnlyDictionary<Guid, int> counts;
        try { counts = await service.CountNewEpisodesAsync(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { return; }
        if (shutdown || IsBusy || IsPositionBusy) return;
        PodcastSubscriptionItem? selected = SelectedSubscription;
        int before = selected?.NewCount ?? 0;
        foreach (PodcastSubscriptionItem item in Subscriptions) item.NewCount = counts.GetValueOrDefault(item.Id);
        // Re-read only the visible show's episodes, keeping the selection by identity.
        if (selected is not null && selected.NewCount != before) episodeLoad = RunTracked(() => LoadSelectedEpisodesAsync(selected));
    }

    public Task RemoveSelectedAsync() => RunFeedOperationAsync(async token =>
    {
        if (SelectedSubscription is not { } selected) return;
        int kept = await service.RemoveAsync(selected.Id, token);
        if (shutdown) return;
        await ReloadSubscriptionsAsync(null, lifetime.Token);
        if (shutdown) return;
        StatusMessage = Loc.Format("Pod.Removed", selected.Title, kept > 0 ? Loc.Format("Pod.KeptFiles", kept) : "");
    }, Loc.Get("Pod.Op.Remove"));

    /// <summary>Cancels feed/position work. Episode downloads are cancelled individually.</summary>
    public void CancelFeedOperation()
    {
        try { feedOperation?.Cancel(); } catch (ObjectDisposedException) { }
        try { positionOperation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>How many episodes download at once; the rest wait their turn in the order they were requested.</summary>
    public const int MaximumConcurrentDownloads = 2;
    private readonly LinkedList<PodcastEpisodeItem> waiting = [];
    private int activeDownloads;

    public int QueuedDownloadCount => waiting.Count;

    public async Task DownloadAsync(PodcastEpisodeItem? item)
    {
        if (item is null || shutdown || !item.CanDownload || SelectedSubscription is not { } owner || item.Episode.SubscriptionId != owner.Id) return;
        _ = MarkSeenAsync(item);
        await EnqueueDownloadAsync(item);
    }

    private async Task EnqueueDownloadAsync(PodcastEpisodeItem item)
    {
        (Guid, string) key = (item.Episode.SubscriptionId, item.EpisodeId);
        inFlight[key] = item; item.Error = "";
        if (activeDownloads >= MaximumConcurrentDownloads)
        {
            item.State = PodcastEpisodeState.Queued;
            waiting.AddLast(item);
            OnPropertyChanged(nameof(QueuedDownloadCount));
            StatusMessage = Loc.Format("Pod.QueuedWaiting", item.Title, waiting.Count);
            return;
        }
        await StartDownloadAsync(item);
    }

    private async Task StartDownloadAsync(PodcastEpisodeItem item)
    {
        (Guid, string) key = (item.Episode.SubscriptionId, item.EpisodeId);
        activeDownloads++;
        item.DownloadReceived = 0; item.DownloadTotal = item.Episode.Length;
        item.State = PodcastEpisodeState.Downloading;
        StatusMessage = Loc.Format("Pod.Downloading", item.Title);
        Task work = DownloadCoreAsync(item, key);
        TrackTask(work);
        await work;
    }

    private void PumpDownloadQueue()
    {
        while (!shutdown && activeDownloads < MaximumConcurrentDownloads && waiting.First is { } next)
        {
            waiting.RemoveFirst();
            if (next.Value.State != PodcastEpisodeState.Queued) continue;
            TrackTask(StartDownloadAsync(next.Value));
        }
        OnPropertyChanged(nameof(QueuedDownloadCount));
    }

    private async Task DownloadCoreAsync(PodcastEpisodeItem item, (Guid, string) key)
    {
        try
        {
            // Created on the UI thread, so reports arrive there. A late report after completion or cancel is ignored.
            Progress<PodcastDownloadProgress> progress = new(report =>
            {
                if (shutdown || item.State != PodcastEpisodeState.Downloading) return;
                item.DownloadTotal = report.Total ?? item.Episode.Length;
                item.DownloadReceived = report.Received;
            });
            StoredPodcastEpisode stored = await service.DownloadAsync(key.Item1, key.Item2, progress, lifetime.Token);
            if (shutdown) return;
            item.Update(stored); item.State = PodcastEpisodeState.Downloaded;
            StatusMessage = Loc.Format("Pod.DownloadedDone", item.Title);
        }
        catch (OperationCanceledException)
        {
            if (shutdown) return;
            item.State = PodcastEpisodeState.Cancelled;
            StatusMessage = Loc.Format("Pod.DownloadCancelled", item.Title);
        }
        catch (Exception exception)
        {
            if (shutdown) return;
            item.Error = exception.Message; item.State = PodcastEpisodeState.Failed;
            StatusMessage = Loc.Format("Pod.DownloadFailed", item.Title, exception.Message);
        }
        finally { inFlight.Remove(key); activeDownloads--; PumpDownloadQueue(); }
    }

    public void CancelDownload(PodcastEpisodeItem? item)
    {
        if (item is not { CanCancel: true }) return;
        if (item.State == PodcastEpisodeState.Queued)
        {
            // It has not started, so there is nothing to abort: just leave the line.
            waiting.Remove(item);
            inFlight.Remove((item.Episode.SubscriptionId, item.EpisodeId));
            item.State = PodcastEpisodeState.Cancelled;
            OnPropertyChanged(nameof(QueuedDownloadCount));
            StatusMessage = Loc.Format("Pod.RemovedFromQueue", item.Title);
            return;
        }
        service.CancelDownload(item.Episode.SubscriptionId, item.EpisodeId);
    }

    /// <summary>Normal Play: always asks to resume. The service rereads the stored position, so a stale row cannot hide automatic progress.</summary>
    public Task PlayAsync(PodcastEpisodeItem? item) => RunTracked(() => PlayCoreAsync(item, true));

    /// <summary>Explicit start over: plays from the beginning and leaves the stored position as the user's choice until playback overwrites it.</summary>
    public Task StartOverAsync(PodcastEpisodeItem? item) => RunTracked(() => PlayCoreAsync(item, false));

    public Task ResumeSelectedAsync() => RunTracked(() => PlayCoreAsync(SelectedEpisode, true));

    private async Task PlayCoreAsync(PodcastEpisodeItem? item, bool resume)
    {
        if (item is null || shutdown || IsPositionBusy || item.State != PodcastEpisodeState.Downloaded ||
            SelectedSubscription is not { } owner || item.Episode.SubscriptionId != owner.Id) return;
        if (handoff is null) { StatusMessage = Loc.Get("Pod.NoPlayback"); return; }
        _ = MarkSeenAsync(item);
        IsPositionBusy = true;
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        positionOperation = operation;
        int version = positionSelectionVersion;
        try
        {
            if (service.ResolvePlayableFile(item.Episode) is null)
            {
                item.State = PodcastEpisodeState.Missing;
                StatusMessage = Loc.Get("Pod.FileMissing");
                return;
            }
            StatusMessage = resume ? Loc.Format("Pod.Resuming", item.Title) : Loc.Format("Pod.Opening", item.Title);
            await service.PlayAsync(owner.Subscription, item.Episode, handoff, resume, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!CanPublishPositionResult(version)) return;
            StatusMessage = resume ? Loc.Format("Pod.ResumedFromSaved", item.Title) : Loc.Format("Pod.SentFromStart", item.Title);
        }
        catch (OperationCanceledException) { if (CanPublishPositionResult(version)) StatusMessage = Loc.Get("Pod.PlaybackCancelled"); }
        catch (Exception exception) { if (CanPublishPositionResult(version)) StatusMessage = Loc.Format("Pod.PlayFailed", exception.Message); }
        finally { positionOperation = null; if (!shutdown) IsPositionBusy = false; }
    }

    public Task SaveSelectedPositionAsync() => RunTracked(async () =>
    {
        if (!CanSaveSelectedPosition || SelectedEpisode is not { } selected || handoff is not IPodcastPositionSource source) return;
        IsPositionBusy = true;
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        positionOperation = operation;
        int version = positionSelectionVersion;
        try
        {
            StatusMessage = Loc.Format("Pod.SavingPosition", selected.Title);
            StoredPodcastEpisode updated = await service.SavePositionAsync(selected.Episode, source, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!CanPublishPositionResult(version) || lifetime.IsCancellationRequested) return;
            selected.Update(updated);
            OnPropertyChanged(nameof(CanResumeSelected));
            StatusMessage = Loc.Format("Pod.SavedPositionDone", selected.Title);
        }
        catch (OperationCanceledException) { if (CanPublishPositionResult(version)) StatusMessage = Loc.Get("Pod.PositionCancelled"); }
        catch (Exception exception) { if (CanPublishPositionResult(version)) StatusMessage = Loc.Format("Pod.PositionFailed", exception.Message); }
        finally { positionOperation = null; if (!shutdown) IsPositionBusy = false; }
    });

    /// <summary>
    /// Merges one persisted automatic-progress save into the matching row (visible, scrolled out or filtered out of view).
    /// Only progress is taken, and only while the row still shows the same download, so a late notification can never
    /// resurrect a bar after the file was deleted or downloaded again. Call on the UI thread.
    /// </summary>
    public void ApplyAutomaticProgress(StoredPodcastEpisode saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (shutdown) return;
        PodcastEpisodeItem? row = cachedEpisodes.FirstOrDefault(item => item.Episode.SubscriptionId == saved.SubscriptionId && item.EpisodeId == saved.EpisodeId);
        if (row is null || saved.SavedProgress is null) return;
        StoredPodcastEpisode current = row.Episode;
        if (current.LocalPath != saved.LocalPath || current.DownloadedAt != saved.DownloadedAt || current.DownloadedBytes != saved.DownloadedBytes) return;
        row.ApplyProgress(saved.SavedProgress);
    }

    public bool SupportsRatings => service.SupportsRatings;
    public bool SupportsFileRemoval => service.SupportsDeletion && handoff is IPodcastPositionSource;

    /// <summary>Sets 0–5 stars (0 clears). Writes are serialized so the last accepted choice wins; the row keeps its latest progress.</summary>
    public Task SetRatingAsync(PodcastEpisodeItem? item, int rating) => RunTracked(async () =>
    {
        if (item is null || shutdown || rating is < 0 or > 5) return;
        if (!service.SupportsRatings) { StatusMessage = Loc.Get("Pod.RatingsUnavailable"); return; }
        try
        {
            await ratingGate.WaitAsync(lifetime.Token);
            try
            {
                StoredPodcastEpisode saved = await service.SetRatingAsync(item.Episode, rating, lifetime.Token);
                if (shutdown) return;
                item.ApplyRating(saved.Rating);
                StatusMessage = rating == 0 ? Loc.Format("Pod.RatingCleared", item.Title)
                    : Loc.Plural("Pod.RatingSet", rating, item.Title);
            }
            finally { ratingGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!shutdown) StatusMessage = Loc.Format("Pod.RatingFailed", exception.Message); }
    });

    /// <summary>Why removing this download right now would be refused, or null when allowed. Checked before asking for confirmation.</summary>
    public async Task<string?> FileRemovalBlockReasonAsync(PodcastEpisodeItem? item)
    {
        if (item is null || shutdown || !item.CanDeleteDownload) return Loc.Get("Pod.NoFileToDelete");
        if (!SupportsFileRemoval) return Loc.Get("Pod.DeleteUnavailable");
        if (IsPositionBusy) return Loc.Get("Pod.WaitPosition");
        try
        {
            PodcastPlaybackSnapshot? playing = await ((IPodcastPositionSource)handoff!).GetPodcastPlaybackAsync(lifetime.Token);
            if (playing is { CanSavePosition: true } && service.ResolvePlayableFile(item.Episode) is { } file && file == playing.File)
                return Loc.Get("Pod.InPlayer");
        }
        catch (OperationCanceledException) { return Loc.Get("Common.Cancelled"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return exception.Message; }
        return null;
    }

    /// <summary>Removes only the downloaded file after the caller has confirmed. The episode, subscription and stars are kept.</summary>
    public Task DeleteDownloadAsync(PodcastEpisodeItem? item) => RunTracked(async () =>
    {
        if (item is null || shutdown || !item.CanDeleteDownload || !SupportsFileRemoval || handoff is not IPodcastPositionSource source) return;
        PodcastEpisodeState before = item.State;
        item.State = PodcastEpisodeState.Deleting;
        StatusMessage = Loc.Format("Pod.Deleting", item.Title);
        try
        {
            StoredPodcastEpisode cleared = await service.DeleteDownloadAsync(item.Episode, source, lifetime.Token);
            if (shutdown) return;
            item.Update(cleared);
            item.State = PodcastEpisodeState.NotDownloaded;
            OnPropertyChanged(nameof(CanResumeSelected)); OnPropertyChanged(nameof(CanSaveSelectedPosition));
            StatusMessage = Loc.Format("Pod.Deleted", item.Title);
        }
        catch (Exception exception)
        {
            if (shutdown) return;
            // The file move, database change and final unlink are separate steps, so show what really happened.
            await RefreshItemAsync(item, before);
            StatusMessage = exception is OperationCanceledException ? Loc.Format("Pod.DeleteCancelled", item.Title)
                : Loc.Format("Pod.DeleteFailed", exception.Message);
        }
    });

    private async Task RefreshItemAsync(PodcastEpisodeItem item, PodcastEpisodeState fallback)
    {
        try
        {
            StoredPodcastEpisode? current = (await service.ListEpisodesAsync(item.Episode.SubscriptionId, CancellationToken.None))
                .FirstOrDefault(episode => episode.EpisodeId == item.EpisodeId);
            if (shutdown) return;
            if (current is null) { item.State = fallback; return; }
            item.Update(current);
            item.State = StateOf(current);
        }
        catch (Exception) { if (!shutdown) item.State = fallback; }
    }

    private PodcastEpisodeState StateOf(StoredPodcastEpisode episode)
    {
        try
        {
            return episode.LocalPath is null ? PodcastEpisodeState.NotDownloaded
                : service.ResolvePlayableFile(episode) is null ? PodcastEpisodeState.Missing : PodcastEpisodeState.Downloaded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return PodcastEpisodeState.Missing; // one unsafe path must not hide the whole cached list
        }
    }

    /// <summary>Cancels all work and waits for it. Safe to call repeatedly.</summary>
    public async Task ShutdownAsync()
    {
        shutdown = true;
        // Signal owned operations before asynchronous lifetime callbacks can race a completed read.
        Task positionCancellation = CancelAsync(positionOperation);
        Task feedCancellation = CancelAsync(feedOperation);
        try { await lifetime.CancelAsync(); } catch (ObjectDisposedException) { }
        await Task.WhenAll(positionCancellation, feedCancellation);
        while (true)
        {
            Task[] pending;
            lock (running) pending = running.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending); } catch (Exception) { /* operations report their own failures */ }
        }
    }

    private static Task CancelAsync(CancellationTokenSource? source)
    {
        try { return source?.CancelAsync() ?? Task.CompletedTask; }
        catch (ObjectDisposedException) { return Task.CompletedTask; }
    }

    private Task RunTracked(Func<Task> operation)
    {
        if (shutdown) return Task.CompletedTask;
        Task work = operation();
        TrackTask(work);
        return work;
    }

    private async Task RunFeedOperationAsync(Func<CancellationToken, Task> operation, string name)
    {
        if (IsBusy || shutdown) return;
        IsBusy = true;
        using CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        feedOperation = source;
        Task work = Guarded();
        TrackTask(work);
        await work;
        async Task Guarded()
        {
            try { await operation(source.Token); }
            catch (OperationCanceledException) { if (!shutdown) StatusMessage = Loc.Format("Pod.OpCancelled", name); }
            catch (Exception exception) { if (!shutdown) StatusMessage = Loc.Format("Pod.OpFailed", name, exception.Message); }
            finally { feedOperation = null; if (!shutdown) IsBusy = false; }
        }
    }

    private void TrackTask(Task task)
    {
        lock (running) running.Add(task);
        _ = task.ContinueWith(done => { lock (running) running.Remove(done); }, TaskScheduler.Default);
    }

    private async Task ReloadSubscriptionsAsync(Guid? select, CancellationToken cancellationToken)
    {
        int version = ++catalogVersion;
        IReadOnlyList<PodcastSubscription> subscriptions = await service.ListSubscriptionsAsync(cancellationToken);
        if (shutdown || cancellationToken.IsCancellationRequested || version != catalogVersion) return;
        IReadOnlyDictionary<Guid, int> newCounts = await service.CountNewEpisodesAsync(cancellationToken);
        if (shutdown || cancellationToken.IsCancellationRequested || version != catalogVersion) return;
        Subscriptions.Clear();
        foreach (PodcastSubscription subscription in subscriptions)
            Subscriptions.Add(new(subscription) { NewCount = newCounts.GetValueOrDefault(subscription.Id) });
        OnPropertyChanged(nameof(HasSubscriptions)); OnPropertyChanged(nameof(CanRefreshAll));
        PodcastSubscriptionItem? match = Subscriptions.FirstOrDefault(item => item.Id == select);
        if (match is not null && SelectedSubscription?.Id == match.Id)
        {
            SelectedSubscription = null; // force the episode list to reload from the refreshed cache
        }
        SelectedSubscription = match ?? Subscriptions.FirstOrDefault();
        if (SelectedSubscription is null) PublishEpisodes([]);
    }

    private async Task LoadSelectedEpisodesAsync(PodcastSubscriptionItem? subscription)
    {
        int version = ++selectionVersion;
        if (shutdown) return;
        if (subscription is null) { PublishEpisodes([]); return; }
        try
        {
            IReadOnlyList<StoredPodcastEpisode> stored = await service.ListEpisodesAsync(subscription.Id, lifetime.Token);
            if (shutdown || lifetime.IsCancellationRequested || version != selectionVersion) return;
            string? select = SelectedEpisode?.EpisodeId;
            List<PodcastEpisodeItem> items = new(stored.Count);
            foreach (StoredPodcastEpisode episode in stored)
            {
                if (inFlight.TryGetValue((episode.SubscriptionId, episode.EpisodeId), out PodcastEpisodeItem? active))
                { active.Update(episode); items.Add(active); continue; }
                items.Add(new(episode, StateOf(episode)));
            }
            PublishEpisodes(items);
            SelectedEpisode = Episodes.FirstOrDefault(item => item.EpisodeId == select);
            OnPropertyChanged(nameof(CanResumeSelected));
            OnPropertyChanged(nameof(CanSaveSelectedPosition));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        { if (!shutdown && version == selectionVersion) StatusMessage = Loc.Format("Pod.CachedFailed", exception.Message); }
    }

    // A download finishing or a file being deleted must move the row in or out of the downloaded-only view.
    private void OnEpisodeStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (shutdown) return;
        if (args.PropertyName is nameof(PodcastEpisodeItem.IsNew) or nameof(PodcastEpisodeItem.State))
        { OnPropertyChanged(nameof(HasNewEpisodes)); OnPropertyChanged(nameof(CanDownloadNew)); }
        if (DownloadedOnly && args.PropertyName == nameof(PodcastEpisodeItem.State)) ApplyEpisodeFilter();
    }

    private void PublishEpisodes(List<PodcastEpisodeItem> items)
    {
        foreach (PodcastEpisodeItem item in items) item.PropertyChanged += OnEpisodeStateChanged;
        cachedEpisodes = items;
        OnPropertyChanged(nameof(HasNewEpisodes)); OnPropertyChanged(nameof(CanDownloadNew));
        RefreshPlaying();
        ApplyEpisodeFilter();
    }

    private static string IssueSuffix(IReadOnlyList<PodcastFeedIssue> issues) =>
        issues.Count == 0 ? "" : Loc.Plural("Pod.Issues", issues.Count);
}
