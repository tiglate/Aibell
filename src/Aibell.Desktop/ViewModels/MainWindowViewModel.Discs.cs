using Avalonia.Threading;
using Aibell.Application.Discs;
using Aibell.Application.Plugins;
using Aibell.Domain.Media;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : IDiscPlaybackHandoff
{
    private bool onlineLookup;
    private IPluginAdministration? pluginAdministration;

    public bool OnlineLookupAllowed => onlineLookup;

    /// <summary>Turns online lookups on or off and saves the choice, exactly as the Preferences checkbox does.</summary>
    public Task SetOnlineLookupAsync(bool allowed) => SavePreferencesAsync(GetPreferences() with { OnlineLookup = allowed });

    /// <summary>The Audio CD window needs playback to be available; it is the same player the rest of the application uses.</summary>
    public bool CanUseDisc => !playbackShutdown;

    /// <summary>Syncing copies files and never touches playback, so it is available whenever the library is.</summary>
    public bool CanUseDevices => !playbackShutdown;

    /// <summary>Every track in the library, for the device sync window's "entire library" choice.</summary>
    public Task<IReadOnlyList<Track>> ListAllTracksAsync(CancellationToken cancellationToken = default) => repository.ListAsync(cancellationToken);

    /// <summary>The plugin manager behind the Preferences > Plugins tab; null when plugins are not available (tests, embedded hosts).</summary>
    public IPluginAdministration? PluginAdministration => pluginAdministration;

    public void ConfigurePlugins(IPluginAdministration administration) => pluginAdministration = administration ?? throw new ArgumentNullException(nameof(administration));

    public async Task PlayDiscAsync(IReadOnlyList<Track> tracks, int startIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if ((uint)startIndex >= (uint)tracks.Count) throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (tracks.Any(track => !DiscLocation.TryParse(track.Location, out _, out _)))
            throw new ArgumentException("Only audio CD tracks can be played from the disc window.", nameof(tracks));
        await Dispatcher.UIThread.InvokeAsync(() => RunPlaybackOperation(async () =>
        {
            long stopGeneration = playbackStopGeneration;
            ThrowIfPlaybackAbandoned(cancellationToken, stopGeneration);
            if (changingTrack) throw new InvalidOperationException("Wait for the current playback transition to finish.");
            Track[] queued = [.. tracks.Skip(startIndex)];
            QueueTracks(queued);
            await PlayQueueEntryCoreAsync(playQueue.Items[^queued.Length].Id, cancellationToken, propagateFailure: true);
        }));
    }
}
