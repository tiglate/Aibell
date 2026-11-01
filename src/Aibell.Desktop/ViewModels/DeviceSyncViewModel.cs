using System.Collections.ObjectModel;
using System.Globalization;
using Aibell.Application.Devices;
using Aibell.Desktop.Localization;
using Aibell.Domain.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public enum DeviceSyncSource { Selected, Visible, Library }

public sealed record DeviceSyncSourceChoice(DeviceSyncSource Kind, string Name);

/// <summary>
/// The "Sync to device" window. The user picks a volume and which tracks to copy, previews what would happen (nothing touches the
/// device yet), then starts the copy. Changing the volume, the source or an option throws the preview away, so a sync always runs
/// exactly the plan that was shown. Files are only added; see <see cref="IDeviceSyncService"/>.
/// </summary>
public sealed partial class DeviceSyncViewModel : ObservableObject, IDisposable
{
    private readonly IRemovableVolumeService volumes;
    private readonly IDeviceSyncService sync;
    private readonly IDeviceTrackSource tracks;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private DeviceSyncPlan? plan;
    private bool disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreview), nameof(CanSync), nameof(CanCancel), nameof(CanChange))]
    private bool isBusy;

    [ObservableProperty]
    private RemovableVolume? selectedVolume;

    [ObservableProperty]
    private DeviceSyncSourceChoice selectedSource;

    [ObservableProperty]
    private bool replaceDifferent;

    [ObservableProperty]
    private bool writePlaylist = true;

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    private string summary = "";

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private bool showProgress;

    public DeviceSyncViewModel(IRemovableVolumeService volumes, IDeviceSyncService sync, IDeviceTrackSource tracks)
    {
        this.volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        this.sync = sync ?? throw new ArgumentNullException(nameof(sync));
        this.tracks = tracks ?? throw new ArgumentNullException(nameof(tracks));
        Sources =
        [
            new(DeviceSyncSource.Selected, Loc.Get("Sync.Source.Selected")),
            new(DeviceSyncSource.Visible, Loc.Get("Sync.Source.Visible")),
            new(DeviceSyncSource.Library, Loc.Get("Sync.Source.Library"))
        ];
        selectedSource = Sources[1];
    }

    public ObservableCollection<RemovableVolume> Volumes { get; } = [];

    public IReadOnlyList<DeviceSyncSourceChoice> Sources { get; }

    public bool HasVolumes => Volumes.Count > 0;

    public bool CanChange => !IsBusy;

    public bool CanPreview => !IsBusy && SelectedVolume is not null;

    public bool CanSync => !IsBusy && plan is { Fits: true } p && (p.CopyCount > 0 || (p.Options.ReplaceDifferent && p.ConflictCount > 0));

    public bool CanCancel => IsBusy;

    public Task LoadAsync() => RunAsync(token =>
    {
        ReloadVolumes();
        StatusMessage = HasVolumes ? Loc.Get("Sync.ChooseSource") : Loc.Get("Sync.NoVolumes");
        return Task.CompletedTask;
    });

    public Task RefreshVolumesAsync() => RunAsync(token =>
    {
        ReloadVolumes();
        StatusMessage = HasVolumes ? Loc.Plural("Sync.VolumesFound", Volumes.Count) : Loc.Get("Sync.NoVolumes");
        return Task.CompletedTask;
    });

    public Task PreviewAsync() => RunAsync(async token =>
    {
        if (SelectedVolume is not { } volume) return;
        StatusMessage = Loc.Get("Sync.Planning");
        IReadOnlyList<Track> chosen = SelectedSource.Kind switch
        {
            DeviceSyncSource.Selected => tracks.SelectedTracks(),
            DeviceSyncSource.Library => await tracks.AllTracksAsync(token),
            _ => tracks.VisibleTracks()
        };
        if (chosen.Count == 0) { SetPlan(null); StatusMessage = Loc.Get("Sync.NoTracks"); return; }
        DeviceSyncOptions options = new(WritePlaylist: WritePlaylist, ReplaceDifferent: ReplaceDifferent);
        DeviceSyncPlan made = await Task.Run(() => sync.Plan(volume, chosen, options), token);
        token.ThrowIfCancellationRequested();
        SetPlan(made);
        StatusMessage = made.Fits ? Loc.Get("Sync.PreviewReady") : Loc.Get("Sync.TooBig");
    });

    public Task StartAsync() => RunAsync(async token =>
    {
        if (plan is not { } current || !CanSyncCore(current)) return;
        ShowProgress = true;
        ProgressPercent = 0;
        StatusMessage = Loc.Get("Sync.Copying");
        Progress<DeviceSyncProgress> progress = new(report =>
        {
            ProgressPercent = report.BytesTotal > 0 ? Math.Clamp(report.BytesCopied * 100.0 / report.BytesTotal, 0, 100) : 100;
            StatusMessage = report.Current is null ? Loc.Get("Sync.Finishing")
                : Loc.Format("Sync.Progress", report.Completed + 1, report.Total, report.Current);
        });
        DeviceSyncResult result = await sync.ExecuteAsync(current, progress, token);
        // The device now differs from the plan, so the old preview no longer describes it.
        SetPlan(null);
        Summary = Describe(result);
        StatusMessage = result.Cancelled ? Loc.Get("Sync.Cancelled") : result.Failures.Count > 0 ? Loc.Format("Sync.DoneWithFailures", result.Failures.Count) : Loc.Get("Sync.Done");
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

    partial void OnSelectedVolumeChanged(RemovableVolume? value) { SetPlan(null); OnPropertyChanged(nameof(CanPreview)); }

    partial void OnSelectedSourceChanged(DeviceSyncSourceChoice value) => SetPlan(null);

    partial void OnReplaceDifferentChanged(bool value) => SetPlan(null);

    partial void OnWritePlaylistChanged(bool value) => SetPlan(null);

    private static bool CanSyncCore(DeviceSyncPlan p) => p.Fits && (p.CopyCount > 0 || (p.Options.ReplaceDifferent && p.ConflictCount > 0));

    private void ReloadVolumes()
    {
        string? previous = SelectedVolume?.Id;
        Volumes.Clear();
        foreach (RemovableVolume volume in volumes.List()) Volumes.Add(volume);
        OnPropertyChanged(nameof(HasVolumes));
        SelectedVolume = Volumes.FirstOrDefault(volume => volume.Id == previous) ?? Volumes.FirstOrDefault();
        SetPlan(null);
    }

    private void SetPlan(DeviceSyncPlan? value)
    {
        plan = value;
        Summary = value is null ? "" : Describe(value);
        if (value is null) ShowProgress = false;
        OnPropertyChanged(nameof(CanSync));
    }

    private static string Describe(DeviceSyncPlan p)
    {
        List<string> lines =
        [
            Loc.Format("Sync.Summary.ToCopy", p.CopyCount, FormatBytes(p.BytesToCopy), FormatBytes(p.Volume.FreeBytes)),
            Loc.Format("Sync.Summary.UpToDate", p.UpToDateCount)
        ];
        if (p.ConflictCount > 0) lines.Add(p.Options.ReplaceDifferent ? Loc.Format("Sync.Summary.Replace", p.ConflictCount) : Loc.Format("Sync.Summary.Conflicts", p.ConflictCount));
        foreach (IGrouping<string, SyncExclusion> group in p.Excluded.GroupBy(exclusion => exclusion.Reason).OrderBy(group => group.Key, StringComparer.Ordinal))
            lines.Add(group.Key switch
            {
                "Missing" => Loc.Format("Sync.Summary.ExcludedMissing", group.Count()),
                "Unsupported" => Loc.Format("Sync.Summary.ExcludedUnsupported", group.Count()),
                "NotAFile" => Loc.Format("Sync.Summary.ExcludedNotAFile", group.Count()),
                _ => Loc.Format("Sync.Summary.ExcludedOther", group.Count())
            });
        if (!p.Fits) lines.Add(Loc.Format("Sync.Summary.NoRoom", FormatBytes(p.BytesToCopy - p.Volume.FreeBytes)));
        return string.Join(Environment.NewLine, lines);
    }

    private static string Describe(DeviceSyncResult r)
    {
        List<string> lines = [Loc.Format("Sync.Result.Copied", r.Copied + r.Replaced, r.UpToDate)];
        if (r.Conflicts > 0) lines.Add(Loc.Format("Sync.Result.Conflicts", r.Conflicts));
        if (r.PlaylistPath is { } playlist) lines.Add(Loc.Format("Sync.Result.Playlist", playlist));
        lines.AddRange(r.Failures.Take(5).Select(failure => Loc.Format("Sync.Result.Failure", failure)));
        if (r.Failures.Count > 5) lines.Add(Loc.Format("Sync.Result.MoreFailures", r.Failures.Count - 5));
        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024d:0.#} KiB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1048576d:0.#} MiB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1073741824d:0.##} GiB")
    };

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
            OnPropertyChanged(nameof(CanSync));
        }
    }
}
