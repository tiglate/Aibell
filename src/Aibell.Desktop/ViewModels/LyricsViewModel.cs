using System.Collections.ObjectModel;
using Aibell.Application.Lyrics;
using Aibell.Desktop.Localization;
using Aibell.Domain.Lyrics;
using Aibell.Domain.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public sealed partial class LyricLineViewModel(LyricLine line, bool synced) : ObservableObject
{
    public string Text { get; } = line.Text;

    /// <summary>Synced lyrics dim every line but the current one.</summary>
    public bool IsDim { get; } = synced;

    public TimeSpan? Start { get; } = line.Start;

    [ObservableProperty]
    private bool isCurrent;
}

/// <summary>
/// The lyrics panel's state for the track that is playing. Lookups run on a worker thread and are cancelled when the track changes;
/// a result that arrives after a skip is dropped, so the panel never shows another track's lyrics.
/// </summary>
public sealed partial class LyricsViewModel(LyricsService service, Action<Action>? post = null) : ObservableObject, IDisposable
{
    private readonly Action<Action> post = post ?? (action => Dispatcher.UIThread.Post(action));
    private CancellationTokenSource? loading;
    private int version;
    private bool enabled = true;
    private TimeSpan[] starts = [];
    private int currentIndex = -1;

    public LyricsService Service { get; } = service;

    public ObservableCollection<LyricLineViewModel> Lines { get; } = [];

    /// <summary>The track the panel is showing lyrics for.</summary>
    public Track? Track { get; private set; }

    public TrackLyrics? Lyrics { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string statusText = "";

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private bool isSynced;

    [ObservableProperty]
    private bool hasLines;

    [ObservableProperty]
    private string sourceText = "";

    [ObservableProperty]
    private bool canEdit;

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>Raised on the UI thread with the index of the line that is now being sung, or -1.</summary>
    public event EventHandler<int>? CurrentLineChanged;

    public void SetEnabled(bool value)
    {
        if (enabled == value) return;
        enabled = value;
        if (!value) { Reset(null); return; }
        Track? track = Track;
        Track = null;
        SetTrack(track);
    }

    /// <summary>Called for every playback update; only a change of track starts a lookup.</summary>
    public void SetTrack(Track? track)
    {
        if (track?.Id == Track?.Id && (track is not null || Lines.Count == 0)) return;
        Reset(track);
        if (!enabled) return;
        if (track is null) { StatusText = Loc.Get("Lyrics.NoTrack"); return; }
        if (!IsEligible(track)) { StatusText = Loc.Get("Lyrics.Unavailable"); return; }
        CanEdit = true;
        IsLoading = true;
        StatusText = Loc.Get("Lyrics.Loading");
        Load(track);
    }

    // Library tracks carry their music folder in Source; these markers are the items that are not songs from a file.
    private static readonly string[] NonLibrarySources = ["Radio", "Audio CD", "MPD", "Podcast"];

    /// <summary>Whether the item can have lyrics: a local file, not radio, a disc, MPD or a podcast episode.</summary>
    public static bool IsEligible(Track track) =>
        LyricsService.CanHaveLyrics(track) && !NonLibrarySources.Contains(track.Source, StringComparer.Ordinal);

    /// <summary>Shows lyrics the user just chose or typed for the current track; null shows "no lyrics".</summary>
    public void Show(Track track, TrackLyrics? lyrics)
    {
        if (Track?.Id != track.Id) return;
        Apply(lyrics is null ? new LyricsResult(LyricsStatus.NotFound) : new LyricsResult(LyricsStatus.Found, lyrics));
    }

    /// <summary>Looks the current track up again, ignoring a remembered miss.</summary>
    public async Task RefreshAsync()
    {
        if (Track is not { } track || !enabled) return;
        await Service.ForgetAsync(track).ConfigureAwait(true);
        Track = null;
        SetTrack(track);
    }

    public void SetPosition(TimeSpan position)
    {
        if (!IsSynced || starts.Length == 0) return;
        int index = IndexAt(starts, position);
        if (index == currentIndex) return;
        if (currentIndex >= 0 && currentIndex < Lines.Count) Lines[currentIndex].IsCurrent = false;
        currentIndex = index;
        if (index >= 0) Lines[index].IsCurrent = true;
        CurrentLineChanged?.Invoke(this, index);
    }

    /// <summary>The index of the last line that starts at or before <paramref name="position"/>, or -1 before the first line.</summary>
    internal static int IndexAt(TimeSpan[] starts, TimeSpan position)
    {
        int low = 0, high = starts.Length - 1, found = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (starts[middle] <= position) { found = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return found;
    }

    public void Dispose()
    {
        Cancel();
    }

    private void Load(Track track)
    {
        CancellationTokenSource source = new();
        loading = source;
        int mine = ++version;
        _ = Task.Run(async () =>
        {
            LyricsResult result;
            try
            {
                result = await Service.ResolveAsync(track, source.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                post(() => { if (mine == version) { IsLoading = false; StatusText = Loc.Get("Lyrics.Error"); } });
                return;
            }
            post(() => { if (mine == version && !source.IsCancellationRequested) Apply(result); });
        });
    }

    private void Apply(LyricsResult result)
    {
        IsLoading = false;
        Lines.Clear();
        starts = [];
        currentIndex = -1;
        Lyrics = result.Lyrics;
        if (result.Lyrics is { IsEmpty: false } lyrics)
        {
            foreach (LyricLine line in lyrics.Lines) Lines.Add(new LyricLineViewModel(line, lyrics.IsSynced));
            starts = lyrics.IsSynced ? [.. lyrics.Lines.Select(line => line.Start ?? TimeSpan.Zero)] : [];
            IsSynced = lyrics.IsSynced;
            HasLines = true;
            StatusText = "";
            SourceText = Describe(lyrics);
            CurrentLineChanged?.Invoke(this, -1);
            return;
        }
        IsSynced = false;
        HasLines = false;
        SourceText = "";
        StatusText = result.Lyrics is { IsInstrumental: true } ? Loc.Get("Lyrics.Instrumental") : result.Status switch
        {
            LyricsStatus.Disabled => "",
            LyricsStatus.NoProvider => Loc.Get("Lyrics.NoProvider"),
            LyricsStatus.Unavailable => Loc.Get("Lyrics.Unavailable"),
            _ => Loc.Get("Lyrics.NotFound")
        };
    }

    private static string Describe(TrackLyrics lyrics)
    {
        string origin = lyrics.Origin switch
        {
            LyricsOrigin.Embedded => Loc.Get("Lyrics.FromFile"),
            LyricsOrigin.UserWritten => Loc.Get("Lyrics.FromYou"),
            LyricsOrigin.UserSelected => Loc.Get("Lyrics.FromSearch"),
            _ => lyrics.Source.Length > 0 ? lyrics.Source : Loc.Get("Lyrics.FromOnline")
        };
        return lyrics.IsSynced ? origin + " · " + Loc.Get("Lyrics.Synced") : origin;
    }

    private void Reset(Track? track)
    {
        Cancel();
        Track = track;
        Lyrics = null;
        Lines.Clear();
        starts = [];
        currentIndex = -1;
        IsSynced = false;
        HasLines = false;
        IsLoading = false;
        CanEdit = false;
        SourceText = "";
        StatusText = "";
        CurrentLineChanged?.Invoke(this, -1);
    }

    private void Cancel()
    {
        version++;
        loading?.Cancel();
        loading?.Dispose();
        loading = null;
    }
}
