using Aibell.Desktop.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Aibell.Application.Discs;
using Aibell.Application.Enrichment;
using Aibell.Desktop.Services;
using Aibell.Domain.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public sealed record DiscTrackRow(int Number, string Title, string Artist, string Length);

/// <summary>
/// The Audio CD window: lists the drives, shows the tracks of the disc in the selected drive and plays them through the shared
/// player. Album, track, genre and cover details are fetched only when the user presses Look up and has allowed online lookups;
/// until then the tracks are named "Track N". Disc tracks are never written to the library. Shutdown cancels every operation.
/// </summary>
public sealed partial class DiscViewModel : ObservableObject, IDisposable
{
    private readonly IOpticalDriveService drives;
    private readonly IMetadataLookupService lookup;
    private readonly IDiscPlaybackHandoff handoff;
    private readonly ICoverImageLoader? covers;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private DiscToc? toc;
    private IReadOnlyList<Track> tracks = [];
    private bool disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    private OpticalDrive? selectedDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(CanPlayAll), nameof(CanLookUp), nameof(CanRefresh), nameof(CanShowArtist))]
    private bool isBusy;

    [ObservableProperty]
    private DiscTrackRow? selectedRow;

    [ObservableProperty]
    private ReleaseCandidate? selectedRelease;

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    private string albumTitle = Loc.Get("Disc.Title");

    [ObservableProperty]
    private string albumDetails = "";

    [ObservableProperty]
    private string artistDetails = "";

    [ObservableProperty]
    private Bitmap? cover;

    public DiscViewModel(IOpticalDriveService drives, IMetadataLookupService lookup, IDiscPlaybackHandoff handoff, ICoverImageLoader? covers = null)
    {
        this.drives = drives ?? throw new ArgumentNullException(nameof(drives));
        this.lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        this.handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
        this.covers = covers;
        Rows.CollectionChanged += (_, _) => NotifyState();
        Releases.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSeveralReleases));
    }

    public ObservableCollection<OpticalDrive> Drives { get; } = [];
    public ObservableCollection<DiscTrackRow> Rows { get; } = [];
    public ObservableCollection<ReleaseCandidate> Releases { get; } = [];

    public bool HasDrives => Drives.Count > 0;
    public bool HasDisc => Rows.Count > 0;
    public bool HasSeveralReleases => Releases.Count > 1;
    public bool CanRefresh => !IsBusy && SelectedDrive is not null;
    public bool CanPlay => !IsBusy && SelectedRow is not null;
    public bool CanPlayAll => !IsBusy && HasDisc;
    public bool CanLookUp => !IsBusy && HasDisc && lookup.IsAvailable;
    public bool CanShowArtist => !IsBusy && ArtistName is not null && lookup.IsAvailable;
    public string LookupHint => lookup.IsAvailable
        ? Loc.Get("Disc.LookupHintOn")
        : Loc.Get("Disc.LookupHintOff");

    private string? ArtistName => SelectedRelease?.Artist is { Length: > 0 } name ? name : tracks is [{ Artist.Length: > 0 } first, ..] ? first.Artist : null;

    /// <summary>Re-evaluates what depends on settings that can change while the window is open.</summary>
    public void NotifyCapabilities()
    {
        OnPropertyChanged(nameof(CanLookUp));
        OnPropertyChanged(nameof(CanShowArtist));
        OnPropertyChanged(nameof(LookupHint));
    }

    public Task LoadAsync() => RunAsync(async token =>
    {
        Drives.Clear();
        foreach (OpticalDrive drive in drives.ListDrives()) Drives.Add(drive);
        OnPropertyChanged(nameof(HasDrives));
        SelectedDrive = Drives.Count > 0 ? Drives[0] : null;
        if (SelectedDrive is null) { StatusMessage = Loc.Get("Disc.NoDrive"); return; }
        await ReadDiscAsync(SelectedDrive, token);
    });

    public Task RefreshAsync() => RunAsync(token => SelectedDrive is { } drive ? ReadDiscAsync(drive, token) : Task.CompletedTask);

    public Task LookUpAsync() => RunAsync(async token =>
    {
        if (toc is null || SelectedDrive is null) return;
        StatusMessage = Loc.Get("Disc.LookingUp");
        MetadataLookupResult<ReleaseCandidate> result = await lookup.FindDiscAsync(toc, token);
        token.ThrowIfCancellationRequested();
        Releases.Clear();
        foreach (ReleaseCandidate release in result.Items) Releases.Add(release);
        switch (result.Status)
        {
            case MetadataLookupStatus.Found:
                SelectedRelease = Releases[0];
                await ApplyReleaseAsync(Releases[0], token);
                StatusMessage = Releases.Count == 1 ? Loc.Get("Disc.FoundOne") : Loc.Format("Disc.FoundMany", Releases.Count);
                break;
            case MetadataLookupStatus.NotFound: StatusMessage = Loc.Get("Disc.NotKnown"); break;
            case MetadataLookupStatus.Disabled: StatusMessage = Loc.Get("Common.OnlineLookupOff"); break;
            case MetadataLookupStatus.NoProvider: StatusMessage = Loc.Get("Common.NoLookupPlugin"); break;
            default: StatusMessage = Loc.Format("Disc.LookupFailed", result.Detail ?? Loc.Get("Artwork.UnknownError")); break;
        }
    });

    /// <summary>Shows another candidate chosen from the list.</summary>
    public Task ChooseReleaseAsync(ReleaseCandidate release) => RunAsync(async token =>
    {
        SelectedRelease = release;
        await ApplyReleaseAsync(release, token);
    });

    public Task ShowArtistAsync() => RunAsync(async token =>
    {
        if (ArtistName is not { } name) return;
        StatusMessage = Loc.Format("Disc.ArtistLooking", name);
        MetadataLookupResult<ArtistInfo> result = await lookup.FindArtistAsync(name, token);
        token.ThrowIfCancellationRequested();
        if (result.Status != MetadataLookupStatus.Found)
        {
            ArtistDetails = "";
            StatusMessage = result.Status == MetadataLookupStatus.NotFound ? Loc.Format("Disc.ArtistNoMatch", name) : Loc.Format("Disc.ArtistFailed", result.Detail ?? result.Status.ToString());
            return;
        }
        ArtistDetails = Describe(result.Items[0]);
        StatusMessage = Loc.Get("Disc.ArtistFrom");
    });

    public Task PlayAsync(DiscTrackRow? row) => RunAsync(async token =>
    {
        if (row is null) return;
        int index = Rows.IndexOf(row);
        if (index < 0) return;
        await handoff.PlayDiscAsync(tracks, index, token);
        StatusMessage = Loc.Format("Disc.PlayingTrack", row.Number);
    });

    public Task PlayAllAsync() => RunAsync(async token =>
    {
        if (tracks.Count == 0) return;
        await handoff.PlayDiscAsync(tracks, 0, token);
        StatusMessage = Loc.Get("Disc.PlayingDisc");
    });

    public void CancelOperation() => operation?.Cancel();

    public async Task ShutdownAsync()
    {
        await lifetime.CancelAsync();
        if (operation is { } running) await running.CancelAsync();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private async Task ReadDiscAsync(OpticalDrive drive, CancellationToken token)
    {
        StatusMessage = Loc.Get("Disc.Reading");
        DiscToc? read = await drives.ReadTocAsync(drive, token);
        token.ThrowIfCancellationRequested();
        Releases.Clear();
        SelectedRelease = null;
        ArtistDetails = "";
        Cover = null;
        toc = read;
        if (read is null)
        {
            tracks = [];
            Rows.Clear();
            AlbumTitle = Loc.Get("Disc.Title");
            AlbumDetails = "";
            StatusMessage = Loc.Format("Disc.NoDisc", drive.DisplayName);
            NotifyState();
            return;
        }
        ShowTracks(DiscTracks.Build(drive, read, Loc.Get("Disc.Title"), titles: GenericTitles(read)));
        AlbumTitle = Loc.Get("Disc.Title");
        AlbumDetails = Loc.Format("Disc.TracksAndLength", read.TrackCount, FormatLength(TimeSpan.FromSeconds((read.LeadOutOffset - read.TrackOffsets[0]) / (double)DiscToc.SectorsPerSecond)));
        StatusMessage = lookup.IsAvailable ? Loc.Format("Disc.TracksChooseLookup", read.TrackCount) : Loc.Format("Disc.TracksOnly", read.TrackCount);
        NotifyState();
    }

    private async Task ApplyReleaseAsync(ReleaseCandidate release, CancellationToken token)
    {
        if (toc is null || SelectedDrive is null) return;
        string[] titles = [.. Enumerable.Range(0, toc.TrackCount).Select(index => release.Tracks.FirstOrDefault(track => track.Number == index + 1)?.Title ?? Loc.Format("Disc.TrackN", toc.FirstTrackNumber + index))];
        string[] artists = [.. Enumerable.Range(0, toc.TrackCount).Select(index => release.Tracks.FirstOrDefault(track => track.Number == index + 1)?.Artist ?? "")];
        ShowTracks(DiscTracks.Build(SelectedDrive, toc, release.Title, release.Artist ?? "", release.Genre ?? "", release.Year ?? 0, titles, artists));
        AlbumTitle = release.Title;
        AlbumDetails = string.Join(" · ", new[] { release.Artist, release.Year?.ToString(CultureInfo.InvariantCulture), release.Genre }.Where(part => !string.IsNullOrWhiteSpace(part)));
        ArtistDetails = "";
        Cover = null;
        if (release.ArtworkUri is { } artwork && covers is not null)
        {
            Bitmap? loaded = await covers.LoadAsync(artwork, token);
            token.ThrowIfCancellationRequested();
            Cover = loaded;
        }
        NotifyState();
    }

    private static string[] GenericTitles(DiscToc disc) => [.. Enumerable.Range(0, disc.TrackCount).Select(index => Loc.Format("Disc.TrackN", disc.FirstTrackNumber + index))];

    private void ShowTracks(IReadOnlyList<Track> next)
    {
        int selected = SelectedRow is null ? -1 : Rows.IndexOf(SelectedRow);
        tracks = next;
        Rows.Clear();
        foreach (Track track in next) Rows.Add(new DiscTrackRow(track.TrackNumber, track.Title, track.Artist, FormatLength(track.Duration)));
        SelectedRow = selected >= 0 && selected < Rows.Count ? Rows[selected] : null;
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasDisc));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanPlayAll));
        OnPropertyChanged(nameof(CanLookUp));
        OnPropertyChanged(nameof(CanShowArtist));
        OnPropertyChanged(nameof(LookupHint));
    }

    partial void OnSelectedRowChanged(DiscTrackRow? value) => OnPropertyChanged(nameof(CanPlay));

    partial void OnSelectedReleaseChanged(ReleaseCandidate? value) => OnPropertyChanged(nameof(CanShowArtist));

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (disposed || lifetime.IsCancellationRequested || IsBusy) return;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        IsBusy = true;
        try
        {
            await work(operation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.Get("Common.Cancelled");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            operation.Dispose();
            operation = null;
            IsBusy = false;
        }
    }

    private static string Describe(ArtistInfo info)
    {
        List<string> lines = [info.Name + (string.IsNullOrWhiteSpace(info.Disambiguation) ? "" : " (" + info.Disambiguation + ")")];
        string facts = string.Join(" · ", new[] { info.Kind, info.Area }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (facts.Length > 0) lines.Add(facts);
        if (info.BeginYear is { } begin) lines.Add(Loc.Format("Disc.ActiveRange", begin.ToString(CultureInfo.InvariantCulture), info.EndYear is { } end ? end.ToString(CultureInfo.InvariantCulture) : Loc.Get("Disc.ActivePresent")));
        if (info.Genres is { Count: > 0 } genres) lines.Add(Loc.Format("Disc.Genres", string.Join(", ", genres)));
        if (info.Links is { Count: > 0 } links) lines.AddRange(links.Select(link => link.AbsoluteUri));
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatLength(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes}:{value.Seconds:00}";
}
