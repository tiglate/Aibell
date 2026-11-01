namespace Aibell.Application.Playback;

public sealed record NowPlayingNotification(string Title, string Artist, string Album, Uri? Artwork = null);

public interface IDesktopNotifications
{
    Task ShowAsync(NowPlayingNotification notification, CancellationToken cancellationToken = default);
}
