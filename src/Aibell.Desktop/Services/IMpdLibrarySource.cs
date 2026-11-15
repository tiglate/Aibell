using Aibell.Mpd.Client;

namespace Aibell.Desktop.Services;

/// <summary>Where the Music view gets its songs while MPD is selected: the server's whole library and its stored playlists.</summary>
public interface IMpdLibrarySource
{
    bool IsConnected { get; }

    /// <summary>Every song in the server's music library, with its tags.</summary>
    Task<IReadOnlyList<MpdSong>> GetAllSongsAsync(CancellationToken cancellationToken);

    /// <summary>The songs of a stored playlist, in order.</summary>
    Task<IReadOnlyList<MpdSong>> GetPlaylistSongsAsync(string name, CancellationToken cancellationToken);

    /// <summary>Removes the first entry of a stored playlist that is the given song. A song that is not in it is not an error.</summary>
    Task RemoveFromPlaylistAsync(string name, string songUri, CancellationToken cancellationToken);
}
