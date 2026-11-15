using System.Globalization;

namespace Aibell.Mpd.Client;

/// <summary>Typed commands for an MPD server on top of an <see cref="MpdConnection"/>.</summary>
public sealed class MpdClient : IMpdClient
{
    private const int MaximumPictureBytes = 16 << 20;

    private readonly MpdConnection connection;

    public MpdClient(MpdConnection connection) => this.connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public static async Task<MpdClient> ConnectAsync(MpdEndpoint endpoint, CancellationToken cancellationToken = default) =>
        new(await MpdConnection.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false));

    /// <summary>The protocol version the server announced.</summary>
    public string ProtocolVersion => connection.ProtocolVersion;

    private Task<MpdResponse> Run(string name, CancellationToken cancellationToken, params object?[] arguments) =>
        connection.SendAsync(MpdCommand.Format(name, arguments), cancellationToken);

    public async Task<MpdStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        MpdParser.ParseStatus((await Run("status", cancellationToken).ConfigureAwait(false)).Pairs);

    public async Task<MpdStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        MpdParser.ParseStats((await Run("stats", cancellationToken).ConfigureAwait(false)).Pairs);

    /// <summary>The song being played or paused, or <c>null</c> when there is none.</summary>
    public async Task<MpdSong?> GetCurrentSongAsync(CancellationToken cancellationToken = default) =>
        MpdParser.ParseSongs((await Run("currentsong", cancellationToken).ConfigureAwait(false)).Pairs) is { Count: > 0 } songs ? songs[0] : null;

    public Task PingAsync(CancellationToken cancellationToken = default) => Run("ping", cancellationToken);

    // Playback

    public Task PlayAsync(int? position = null, CancellationToken cancellationToken = default) =>
        position is { } index ? Run("play", cancellationToken, index) : Run("play", cancellationToken);

    public Task PlayIdAsync(int id, CancellationToken cancellationToken = default) => Run("playid", cancellationToken, id);

    public Task PauseAsync(bool pause, CancellationToken cancellationToken = default) => Run("pause", cancellationToken, pause);

    public Task StopAsync(CancellationToken cancellationToken = default) => Run("stop", cancellationToken);

    public Task NextAsync(CancellationToken cancellationToken = default) => Run("next", cancellationToken);

    public Task PreviousAsync(CancellationToken cancellationToken = default) => Run("previous", cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Run("seekcur", cancellationToken, position);

    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default) => Run("setvol", cancellationToken, Math.Clamp(volume, 0, 100));

    public Task SetRepeatAsync(bool enabled, CancellationToken cancellationToken = default) => Run("repeat", cancellationToken, enabled);

    public Task SetRandomAsync(bool enabled, CancellationToken cancellationToken = default) => Run("random", cancellationToken, enabled);

    public Task SetConsumeAsync(bool enabled, CancellationToken cancellationToken = default) => Run("consume", cancellationToken, enabled);

    public Task SetSingleAsync(MpdSingleMode mode, CancellationToken cancellationToken = default) =>
        connection.SendAsync($"single {(mode switch { MpdSingleMode.On => "1", MpdSingleMode.OneShot => "oneshot", _ => "0" })}", cancellationToken);

    public Task SetCrossfadeAsync(int seconds, CancellationToken cancellationToken = default) => Run("crossfade", cancellationToken, Math.Max(0, seconds));

    // Queue

    public async Task<IReadOnlyList<MpdSong>> GetQueueAsync(CancellationToken cancellationToken = default) =>
        MpdParser.ParseSongs((await Run("playlistinfo", cancellationToken).ConfigureAwait(false)).Pairs);

    /// <summary>Adds a file or folder from the database (or a stream URL) to the end of the queue.</summary>
    public Task AddAsync(string uri, CancellationToken cancellationToken = default) => Run("add", cancellationToken, uri);

    /// <summary>Adds one song and returns its queue id.</summary>
    public async Task<int> AddIdAsync(string uri, int? position = null, CancellationToken cancellationToken = default)
    {
        MpdResponse response = position is { } index ? await Run("addid", cancellationToken, uri, index).ConfigureAwait(false) : await Run("addid", cancellationToken, uri).ConfigureAwait(false);
        return int.TryParse(response.First("Id"), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : 0;
    }

    public Task DeleteAsync(int position, CancellationToken cancellationToken = default) => Run("delete", cancellationToken, position);

    public Task DeleteIdAsync(int id, CancellationToken cancellationToken = default) => Run("deleteid", cancellationToken, id);

    public Task ClearAsync(CancellationToken cancellationToken = default) => Run("clear", cancellationToken);

    public Task MoveAsync(int from, int destination, CancellationToken cancellationToken = default) => Run("move", cancellationToken, from, destination);

    // Stored playlists

    public async Task<IReadOnlyList<MpdStoredPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default) =>
        MpdParser.ParsePlaylists((await Run("listplaylists", cancellationToken).ConfigureAwait(false)).Pairs);

    public async Task<IReadOnlyList<MpdSong>> GetPlaylistSongsAsync(string name, CancellationToken cancellationToken = default) =>
        MpdParser.ParseSongs((await Run("listplaylistinfo", cancellationToken, name).ConfigureAwait(false)).Pairs);

    /// <summary>Appends a stored playlist to the queue.</summary>
    public Task LoadPlaylistAsync(string name, CancellationToken cancellationToken = default) => Run("load", cancellationToken, name);

    /// <summary>Saves the queue as a new stored playlist.</summary>
    public Task SavePlaylistAsync(string name, CancellationToken cancellationToken = default) => Run("save", cancellationToken, name);

    public Task DeletePlaylistAsync(string name, CancellationToken cancellationToken = default) => Run("rm", cancellationToken, name);

    public Task RenamePlaylistAsync(string name, string newName, CancellationToken cancellationToken = default) => Run("rename", cancellationToken, name, newName);

    public Task AddToPlaylistAsync(string name, string uri, CancellationToken cancellationToken = default) => Run("playlistadd", cancellationToken, name, uri);

    public Task RemoveFromPlaylistAsync(string name, int position, CancellationToken cancellationToken = default) => Run("playlistdelete", cancellationToken, name, position);

    // Database

    /// <summary>The items of a folder of the music directory; an empty path is the top.</summary>
    public async Task<IReadOnlyList<MpdEntry>> ListFolderAsync(string path = "", CancellationToken cancellationToken = default) =>
        MpdParser.ParseEntries((await (path.Length == 0 ? Run("lsinfo", cancellationToken) : Run("lsinfo", cancellationToken, path)).ConfigureAwait(false)).Pairs);

    /// <summary>
    /// Songs whose <paramref name="tag"/> contains the text, ignoring case. For <c>any</c> the file name is searched too, because MPD's
    /// <c>any</c> covers tags only and has no "or", so untagged files would never be found; the two answers are merged.
    /// </summary>
    public async Task<IReadOnlyList<MpdSong>> SearchAsync(string text, string tag = "any", CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MpdSong> found = MpdParser.ParseSongs((await Run("search", cancellationToken, MpdCommand.ContainsFilter(tag, text)).ConfigureAwait(false)).Pairs);
        if (tag != "any") return found;
        IReadOnlyList<MpdSong> byName = MpdParser.ParseSongs((await Run("search", cancellationToken, MpdCommand.ContainsFilter("file", text)).ConfigureAwait(false)).Pairs);
        HashSet<string> seen = [.. found.Select(song => song.Uri)];
        return [.. found, .. byName.Where(song => seen.Add(song.Uri))];
    }

    /// <summary>Every song under a folder of the music directory, including its sub-folders; an empty path is the whole library.</summary>
    public async Task<IReadOnlyList<MpdSong>> ListSongsAsync(string folder = "", CancellationToken cancellationToken = default) =>
        MpdParser.ParseSongs((await Run("find", cancellationToken, MpdCommand.BaseFilter(folder)).ConfigureAwait(false)).Pairs);

    /// <summary>All values of a tag in the database, for example every artist.</summary>
    public async Task<IReadOnlyList<string>> ListTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        MpdResponse response = await Run("list", cancellationToken, tag).ConfigureAwait(false);
        return [.. response.Pairs.Where(pair => pair.Value.Length > 0).Select(pair => pair.Value)];
    }

    /// <summary>Starts a database update, of one folder or all, and returns the job id.</summary>
    public async Task<int> UpdateDatabaseAsync(string path = "", CancellationToken cancellationToken = default)
    {
        MpdResponse response = await (path.Length == 0 ? Run("update", cancellationToken) : Run("update", cancellationToken, path)).ConfigureAwait(false);
        return int.TryParse(response.First("updating_db"), NumberStyles.None, CultureInfo.InvariantCulture, out int job) ? job : 0;
    }

    // Outputs

    public async Task<IReadOnlyList<MpdOutput>> GetOutputsAsync(CancellationToken cancellationToken = default) =>
        MpdParser.ParseOutputs((await Run("outputs", cancellationToken).ConfigureAwait(false)).Pairs);

    public Task SetOutputEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default) =>
        Run(enabled ? "enableoutput" : "disableoutput", cancellationToken, id);

    // Pictures

    /// <summary>
    /// The cover of a song: a <c>cover.*</c> file next to it, or else a picture embedded in the file. <c>null</c> when there is none or the
    /// server is too old for either command.
    /// </summary>
    public async Task<MpdPicture?> GetPictureAsync(string uri, CancellationToken cancellationToken = default)
    {
        foreach (string command in new[] { "albumart", "readpicture" })
        {
            try
            {
                MpdPicture? picture = await ReadPictureAsync(command, uri, cancellationToken).ConfigureAwait(false);
                if (picture is not null) return picture;
            }
            catch (MpdProtocolException exception) when (exception.Code is MpdProtocolException.NoExist or 5)
            {
                // No such picture, or the command is unknown to an older server: try the next source.
            }
        }
        return null;
    }

    private async Task<MpdPicture?> ReadPictureAsync(string command, string uri, CancellationToken cancellationToken)
    {
        using MemoryStream data = new();
        string? type = null;
        while (true)
        {
            MpdResponse response = await Run(command, cancellationToken, uri, (int)data.Length).ConfigureAwait(false);
            if (response.Binary is not { Length: > 0 } chunk) return data.Length > 0 ? new MpdPicture(data.ToArray(), type) : null;
            type ??= response.First("type");
            data.Write(chunk);
            long size = long.TryParse(response.First("size"), NumberStyles.None, CultureInfo.InvariantCulture, out long total) ? total : data.Length;
            if (size > MaximumPictureBytes) return null;
            if (data.Length >= size) return new MpdPicture(data.ToArray(), type);
        }
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
