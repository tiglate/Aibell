namespace Aibell.Mpd.Client;

/// <summary>The MPD commands the application uses, so view models can be tested without a server.</summary>
public interface IMpdClient : IAsyncDisposable
{
    string ProtocolVersion { get; }

    Task<MpdStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<MpdStats> GetStatsAsync(CancellationToken cancellationToken = default);

    Task<MpdSong?> GetCurrentSongAsync(CancellationToken cancellationToken = default);

    Task PingAsync(CancellationToken cancellationToken = default);

    Task PlayAsync(int? position = null, CancellationToken cancellationToken = default);

    Task PlayIdAsync(int id, CancellationToken cancellationToken = default);

    Task PauseAsync(bool pause, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task NextAsync(CancellationToken cancellationToken = default);

    Task PreviousAsync(CancellationToken cancellationToken = default);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default);

    Task SetRepeatAsync(bool enabled, CancellationToken cancellationToken = default);

    Task SetRandomAsync(bool enabled, CancellationToken cancellationToken = default);

    Task SetConsumeAsync(bool enabled, CancellationToken cancellationToken = default);

    Task SetSingleAsync(MpdSingleMode mode, CancellationToken cancellationToken = default);

    Task SetCrossfadeAsync(int seconds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MpdSong>> GetQueueAsync(CancellationToken cancellationToken = default);

    Task AddAsync(string uri, CancellationToken cancellationToken = default);

    Task<int> AddIdAsync(string uri, int? position = null, CancellationToken cancellationToken = default);

    Task DeleteAsync(int position, CancellationToken cancellationToken = default);

    Task DeleteIdAsync(int id, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);

    Task MoveAsync(int from, int destination, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MpdStoredPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MpdSong>> GetPlaylistSongsAsync(string name, CancellationToken cancellationToken = default);

    Task LoadPlaylistAsync(string name, CancellationToken cancellationToken = default);

    Task SavePlaylistAsync(string name, CancellationToken cancellationToken = default);

    Task DeletePlaylistAsync(string name, CancellationToken cancellationToken = default);

    Task RenamePlaylistAsync(string name, string newName, CancellationToken cancellationToken = default);

    Task AddToPlaylistAsync(string name, string uri, CancellationToken cancellationToken = default);

    Task RemoveFromPlaylistAsync(string name, int position, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MpdEntry>> ListFolderAsync(string path = "", CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MpdSong>> SearchAsync(string text, string tag = "any", CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MpdSong>> ListSongsAsync(string folder = "", CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListTagAsync(string tag, CancellationToken cancellationToken = default);

    Task<int> UpdateDatabaseAsync(string path = "", CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MpdOutput>> GetOutputsAsync(CancellationToken cancellationToken = default);

    Task SetOutputEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default);

    Task<MpdPicture?> GetPictureAsync(string uri, CancellationToken cancellationToken = default);
}
