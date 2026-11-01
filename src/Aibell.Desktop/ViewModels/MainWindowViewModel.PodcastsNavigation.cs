namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    // Podcasts, Audio CD, Devices and Audiobooks are overlays on the underlying Music/Radio view, so leaving any of them
    // restores the exact library state. At most one of them is active at a time.
    private enum OverlayView { None, Podcasts, Disc, Devices, Audiobooks }

    private OverlayView overlay;

    public bool IsPodcastsView => overlay == OverlayView.Podcasts;
    public bool IsDiscView => overlay == OverlayView.Disc;
    public bool IsDevicesView => overlay == OverlayView.Devices;
    public bool IsAudiobooksView => overlay == OverlayView.Audiobooks;
    public bool CanSearchLibrary => overlay == OverlayView.None;

    /// <summary>Shows the embedded Podcasts view without touching the Music or Radio view state.</summary>
    public void ShowPodcastsView() => ShowOverlay(OverlayView.Podcasts, "Podcasts.");

    /// <summary>Shows the embedded Audio CD view without touching the Music or Radio view state.</summary>
    public void ShowDiscView() => ShowOverlay(OverlayView.Disc, "Audio CD.");

    /// <summary>Shows the embedded Devices (sync) view without touching the Music or Radio view state.</summary>
    public void ShowDevicesView() => ShowOverlay(OverlayView.Devices, "Devices.");

    /// <summary>Shows the embedded Audiobooks shelf without touching the Music or Radio view state.</summary>
    public void ShowAudiobooksView() => ShowOverlay(OverlayView.Audiobooks, "Audiobooks.");

    private void ShowOverlay(OverlayView view, string status)
    {
        if (overlay == view) return;
        overlay = view;
        NotifyOverlayChanged();
        StatusMessage = status;
    }

    /// <summary>
    /// Leaves the active overlay and returns to the Music view that was active before it. Returns false when a Radio view
    /// was underneath, in which case the caller performs the regular Music selection.
    /// </summary>
    public bool TryReturnFromOverlayToMusic()
    {
        if (overlay == OverlayView.None) return false;
        if (activeView == LibraryViewKind.Radio) return false;
        overlay = OverlayView.None;
        NotifyOverlayChanged();
        return true;
    }

    private void NotifyOverlayChanged()
    {
        OnPropertyChanged(nameof(IsPodcastsView)); OnPropertyChanged(nameof(IsDiscView)); OnPropertyChanged(nameof(IsDevicesView)); OnPropertyChanged(nameof(IsAudiobooksView)); OnPropertyChanged(nameof(CanSearchLibrary));
        OnPropertyChanged(nameof(IsMusicView)); OnPropertyChanged(nameof(IsRadioView));
        OnPropertyChanged(nameof(IsColumnBrowserVisible)); OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(CanPlay));
    }
}
