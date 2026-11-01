using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Aibell.Application.Enrichment;
using Aibell.Application.Library;
using Aibell.Desktop.Localization;
using Aibell.Desktop.Services;
using Aibell.Domain.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public sealed partial class ArtworkCandidateRow(ReleaseCandidate release) : ObservableObject
{
    public ReleaseCandidate Release { get; } = release;

    public string Title => Release.Title;

    public string Details => string.Join(" · ", new[] { Release.Artist, Release.Year?.ToString(CultureInfo.InvariantCulture),
        Release.Tracks.Count > 0 ? Loc.Format("Artwork.Tracks", Release.Tracks.Count) : null }.Where(part => !string.IsNullOrWhiteSpace(part)));

    [ObservableProperty]
    private Bitmap? thumbnail;
}

/// <summary>
/// "Get Artwork…" for one track's album: asks the lookup plugins for matching releases, shows the ones that have a cover, and saves
/// the chosen cover as <c>cover.jpg</c> in the album folder. The search starts only when the window opens (the user asked for it),
/// nothing is downloaded in full until the user presses Save, and an existing cover is never replaced.
/// </summary>
public sealed partial class ArtworkLookupViewModel : ObservableObject, IDisposable
{
    private readonly Track track;
    private readonly IMetadataLookupService lookup;
    private readonly ICoverArtDownloader downloader;
    private readonly IAlbumCoverWriter writer;
    private readonly ICoverImageLoader? thumbnails;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(CanSearch))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private ArtworkCandidateRow? selected;

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string? savedPath;

    public ArtworkLookupViewModel(Track track, IMetadataLookupService lookup, ICoverArtDownloader downloader, IAlbumCoverWriter writer, ICoverImageLoader? thumbnails = null)
    {
        this.track = track ?? throw new ArgumentNullException(nameof(track));
        this.lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        this.downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.thumbnails = thumbnails;
    }

    public ObservableCollection<ArtworkCandidateRow> Candidates { get; } = [];

    public Track Track => track;

    public string Album => track.Album;

    /// <summary>The album artist when the tags have one, otherwise the track artist; it narrows the search.</summary>
    public string Artist => track.AlbumArtist.Length > 0 ? track.AlbumArtist : track.Artist;

    public string Heading => Artist.Length > 0 ? $"{Album} — {Artist}" : Album;

    public bool CanSearch => !IsBusy && SavedPath is null;

    public bool CanSave => !IsBusy && Selected is not null && SavedPath is null;

    public Task SearchAsync() => RunAsync(async token =>
    {
        Candidates.Clear();
        Selected = null;
        StatusMessage = Loc.Get("Artwork.Searching");
        MetadataLookupResult<ReleaseCandidate> result = await lookup.FindAlbumAsync(track.Album, Artist.Length > 0 ? Artist : null, includeTracks: false, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        switch (result.Status)
        {
            case MetadataLookupStatus.Found:
                foreach (ReleaseCandidate release in result.Items.Where(item => item.ArtworkUri is not null)) Candidates.Add(new ArtworkCandidateRow(release));
                if (Candidates.Count == 0) { StatusMessage = Loc.Get("Artwork.NoCoverArt"); return; }
                Selected = Candidates[0];
                StatusMessage = Candidates.Count == 1 ? Loc.Get("Artwork.FoundOne") : Loc.Format("Artwork.FoundMany", Candidates.Count);
                // Thumbnails appear as they arrive; they must not keep Save waiting.
                _ = LoadThumbnailsAsync(lifetime.Token);
                break;
            case MetadataLookupStatus.NotFound: StatusMessage = Loc.Get("Artwork.NotFound"); break;
            case MetadataLookupStatus.Disabled: StatusMessage = Loc.Get("Common.OnlineLookupOff"); break;
            case MetadataLookupStatus.NoProvider: StatusMessage = Loc.Get("Common.NoLookupPlugin"); break;
            default: StatusMessage = Loc.Format("Artwork.Failed", result.Detail ?? Loc.Get("Artwork.UnknownError")); break;
        }
    });

    public Task SaveAsync() => RunAsync(async token =>
    {
        if (Selected?.Release.ArtworkUri is not { } source) return;
        StatusMessage = Loc.Get("Artwork.Downloading");
        byte[] image = await downloader.DownloadAsync(source, token);
        token.ThrowIfCancellationRequested();
        string path = await writer.SaveAsync(track.Location, image, token);
        SavedPath = path;
        StatusMessage = Loc.Format("Artwork.Saved", Path.GetFileName(path), Path.GetDirectoryName(path));
    });

    public void Cancel() => lifetime.Cancel();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private async Task LoadThumbnailsAsync(CancellationToken token)
    {
        if (thumbnails is null) return;
        try
        {
            await Task.WhenAll(Candidates.ToArray().Select(async row =>
            {
                Bitmap? bitmap = await thumbnails.LoadAsync(row.Release.ArtworkUri!, token);
                if (!token.IsCancellationRequested) row.Thumbnail = bitmap;
            }));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (disposed || lifetime.IsCancellationRequested || IsBusy) return;
        IsBusy = true;
        try
        {
            await work(lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.Get("Common.Cancelled");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
