using System.Diagnostics;
using Aibell.Application.Playback;
using Aibell.Domain.Media;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Guid? notificationTrack;
    private CancellationTokenSource? notificationCancellation;
    private Task pendingNotification = Task.CompletedTask;
    public bool CanShowDesktopNotifications => desktopNotifications is not null;
    public bool ShowDesktopNotifications { get; private set; }
    public Task WaitForDesktopNotificationsAsync() => pendingNotification;

    public void CancelDesktopNotifications()
    {
        notificationCancellation?.Cancel(); notificationCancellation?.Dispose(); notificationCancellation = null;
    }

    private void UpdateDesktopNotification(PlaybackSnapshot snapshot)
    {
        if (snapshot.Status != PlaybackStatus.Playing || snapshot.Track is not { } track || notificationTrack == track.Id) return;
        notificationTrack = track.Id;
        CancelDesktopNotifications();
        if (!ShowDesktopNotifications || desktopNotifications is null) return;
        TrackMetadata metadata = editedMetadata.GetValueOrDefault(track.Id) ?? TrackMetadata.FromTrack(refreshedTracks.GetValueOrDefault(track.Id) ?? track);
        notificationCancellation = new();
        pendingNotification = SendDesktopNotificationAsync(pendingNotification,
            new(metadata.Title, metadata.Artist, metadata.Album, artworkSource.Location == track.Location ? ArtworkLocation : null), notificationCancellation.Token);
    }

    private async Task SendDesktopNotificationAsync(Task previous, NowPlayingNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            await previous;
            cancellationToken.ThrowIfCancellationRequested();
            await desktopNotifications!.ShowAsync(notification, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Trace.WriteLine($"Desktop notification unavailable: {exception.Message}"); }
    }
}
