namespace Aibell.Desktop.Services;

/// <summary>Lets the Podcasts view show and control what the shared player is currently playing.</summary>
public interface IPodcastPlaybackControl
{
    /// <summary>The local file the shared player is actively playing (not paused or stopped), or null.</summary>
    Uri? PodcastPlayingFile { get; }

    /// <summary>Raised on the UI thread whenever the shared player's state changes.</summary>
    event EventHandler? PodcastPlayingChanged;

    /// <summary>The speed used for podcast episodes (1, 1.25, 1.5 or 2). Music, radio and audiobooks always play at 1x.</summary>
    double PodcastPlaybackRate { get; }

    /// <summary>Chooses the podcast speed, applying it at once if a podcast is loaded, and remembers it.</summary>
    Task SetPodcastPlaybackRateAsync(double rate, CancellationToken cancellationToken = default);

    /// <summary>Pauses the shared player only if it is still playing <paramref name="file"/>.</summary>
    Task PausePodcastAsync(Uri file, CancellationToken cancellationToken = default);
}
