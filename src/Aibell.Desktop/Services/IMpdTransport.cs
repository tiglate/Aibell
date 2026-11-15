using System.ComponentModel;
using Aibell.Mpd.Client;

namespace Aibell.Desktop.Services;

/// <summary>
/// What the player needs from the MPD view model to play on the server: its state, and a few commands that report failure by throwing
/// <see cref="InvalidOperationException"/> (as the local engines do) instead of showing a message.
/// </summary>
public interface IMpdTransport : INotifyPropertyChanged
{
    bool IsConnected { get; }

    MpdState State { get; }

    TimeSpan Elapsed { get; }

    TimeSpan Duration { get; }

    /// <summary>The server's volume as 0 to 100, or <c>null</c> when it has no mixer.</summary>
    int? ServerVolume { get; }

    /// <summary>The queue id of the song the server is playing or paused on, or <c>null</c>.</summary>
    int? CurrentSongId { get; }

    /// <summary>Adds a song to the end of the server queue without playing it, and returns its queue id.</summary>
    Task<int> AddSongAsync(string songUri, CancellationToken cancellationToken);

    /// <summary>Plays a song added by <see cref="AddSongAsync"/> and arranges for the server to stop when it ends.</summary>
    Task PlaySongAsync(int songId, CancellationToken cancellationToken);

    Task ResumeAsync(CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);

    Task SetServerVolumeAsync(int volume, CancellationToken cancellationToken);

    /// <summary>Removes a song this application added; a song that is already gone is not an error.</summary>
    Task RemoveSongAsync(int songId, CancellationToken cancellationToken);

    /// <summary>The cover of a song, or <c>null</c>.</summary>
    Task<byte[]?> GetCoverAsync(string songUri, CancellationToken cancellationToken);
}
