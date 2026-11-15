using Aibell.Mpd.Client;

namespace Aibell.Desktop.TestSupport;

/// <summary>An in-memory MPD for view model and window tests: records every command and answers from plain lists.</summary>
internal sealed class FakeMpdClient : IMpdClient
{
    public static MpdSong Song(string uri, string title, int id, string? artist = "Artist", string? album = "Album", int seconds = 100, string? genre = null, string? track = null)
    {
        Dictionary<string, IReadOnlyList<string>> tags = new(StringComparer.OrdinalIgnoreCase) { ["Title"] = [title] };
        if (artist is not null) tags["Artist"] = [artist];
        if (album is not null) tags["Album"] = [album];
        if (genre is not null) tags["Genre"] = [genre];
        if (track is not null) tags["Track"] = [track];
        return new MpdSong(uri, tags, TimeSpan.FromSeconds(seconds), id - 1, id);
    }


    public List<string> Calls { get; } = [];
    public MpdStatus Status { get; set; } = new(50, false, false, MpdSingleMode.Off, false, 1, 2, MpdState.Playing, 0, 1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100), 320, null, null, null, null, false);
    public MpdSong? Current { get; set; } = Song("a.mp3", "Alpha", 1);
    public List<MpdSong> Queue { get; set; } = [Song("a.mp3", "Alpha", 1), Song("b.mp3", "Beta", 2, artist: null, album: null)];
    /// <summary>What the server returns for the whole library.</summary>
    public List<MpdSong> Library { get; set; } =
    [
        Song("a/two.flac", "Two", 0, "Band A", "First", 200, "Rock", "2"),
        Song("a/one.flac", "One", 0, "Band A", "First", 180, "Rock", "1/10"),
        Song("b/blue.flac", "Blue", 0, "Band B", "Second", 240, "Jazz", "1"),
        Song("loose.mp3", "Loose", 0, null, null, 60),
    ];
    public List<MpdStoredPlaylist> Lists { get; set; } = [new("Zed", null), new("alpha", null)];
    public List<MpdOutput> OutputList { get; set; } = [new(0, "Speakers", true, "alsa")];
    public Dictionary<string, IReadOnlyList<MpdEntry>> Folders { get; } = new()
    {
        [""] = [new(MpdEntryKind.Song, "song.mp3", Song("song.mp3", "Song", 0)), new(MpdEntryKind.Directory, "Rock"), new(MpdEntryKind.Playlist, "mix.m3u")],
        ["Rock"] = [new(MpdEntryKind.Song, "Rock/x.flac", Song("Rock/x.flac", "X", 0))],
    };
    public Exception? Fail { get; set; }

    /// <summary>Fails only the commands it names, so a test can break one call and leave the rest working.</summary>
    public Func<string, Exception?>? FailWhen { get; set; }
    public MpdPicture? Picture { get; set; }
    public string ProtocolVersion => "0.23.0";

    private Task<T> Do<T>(string call, T value)
    {
        Calls.Add(call);
        return (Fail ?? FailWhen?.Invoke(call)) is { } error ? Task.FromException<T>(error) : Task.FromResult(value);
    }

    private Task<int> Do(string call) => Do(call, 0);

    public Task<MpdStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Do("status", Status);
    public Task<MpdStats> GetStatsAsync(CancellationToken cancellationToken = default) => Do("stats", new MpdStats(3, 2, 4, TimeSpan.Zero, TimeSpan.Zero, null, TimeSpan.Zero));
    public Task<MpdSong?> GetCurrentSongAsync(CancellationToken cancellationToken = default) => Do("currentsong", Current);
    public Task PingAsync(CancellationToken cancellationToken = default) => Do("ping");
    public Task PlayAsync(int? position = null, CancellationToken cancellationToken = default) => Do($"play {position}");
    public Task PlayIdAsync(int id, CancellationToken cancellationToken = default) => Do($"playid {id}");
    public Task PauseAsync(bool pause, CancellationToken cancellationToken = default) => Do($"pause {pause}");
    public Task StopAsync(CancellationToken cancellationToken = default) => Do("stop");
    public Task NextAsync(CancellationToken cancellationToken = default) => Do("next");
    public Task PreviousAsync(CancellationToken cancellationToken = default) => Do("previous");
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Do($"seek {position.TotalSeconds}");
    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        Status = Status with { Volume = volume };
        return Do($"vol {volume}");
    }
    public Task SetRepeatAsync(bool enabled, CancellationToken cancellationToken = default) => Do($"repeat {enabled}");
    public Task SetRandomAsync(bool enabled, CancellationToken cancellationToken = default) => Do($"random {enabled}");
    public Task SetConsumeAsync(bool enabled, CancellationToken cancellationToken = default) => Do($"consume {enabled}");
    public Task SetSingleAsync(MpdSingleMode mode, CancellationToken cancellationToken = default) => Do($"single {mode}");
    public Task SetCrossfadeAsync(int seconds, CancellationToken cancellationToken = default) => Do($"crossfade {seconds}");
    public Task<IReadOnlyList<MpdSong>> GetQueueAsync(CancellationToken cancellationToken = default) => Do("queue", (IReadOnlyList<MpdSong>)Queue);
    public Task AddAsync(string uri, CancellationToken cancellationToken = default) => Do($"add {uri}");
    public Task<int> AddIdAsync(string uri, int? position = null, CancellationToken cancellationToken = default) => Do($"addid {uri}", 42);
    public Task DeleteAsync(int position, CancellationToken cancellationToken = default) => Do($"delete {position}");
    public Task DeleteIdAsync(int id, CancellationToken cancellationToken = default) => Do($"deleteid {id}");
    public Task ClearAsync(CancellationToken cancellationToken = default) => Do("clear");
    public Task MoveAsync(int from, int destination, CancellationToken cancellationToken = default) => Do($"move {from} {destination}");
    public Task<IReadOnlyList<MpdStoredPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default) => Do("playlists", (IReadOnlyList<MpdStoredPlaylist>)Lists);
    public Task<IReadOnlyList<MpdSong>> GetPlaylistSongsAsync(string name, CancellationToken cancellationToken = default) => Do($"playlistsongs {name}", (IReadOnlyList<MpdSong>)[Song("p.mp3", "In list", 0)]);
    public Task LoadPlaylistAsync(string name, CancellationToken cancellationToken = default) => Do($"load {name}");
    public Task SavePlaylistAsync(string name, CancellationToken cancellationToken = default) => Do($"save {name}");
    public Task DeletePlaylistAsync(string name, CancellationToken cancellationToken = default) { Lists.RemoveAll(item => item.Name == name); return Do($"rm {name}"); }
    public Task RenamePlaylistAsync(string name, string newName, CancellationToken cancellationToken = default)
    {
        Lists = [.. Lists.Where(item => item.Name != name), new MpdStoredPlaylist(newName, null)];
        return Do($"rename {name} {newName}");
    }
    public Task AddToPlaylistAsync(string name, string uri, CancellationToken cancellationToken = default) => Do($"playlistadd {name} {uri}");
    public Task RemoveFromPlaylistAsync(string name, int position, CancellationToken cancellationToken = default) => Do($"playlistdelete {name} {position}");
    public Task<IReadOnlyList<MpdEntry>> ListFolderAsync(string path = "", CancellationToken cancellationToken = default) => Do($"lsinfo {path}", Folders.GetValueOrDefault(path) ?? []);
    public Task<IReadOnlyList<MpdSong>> SearchAsync(string text, string tag = "any", CancellationToken cancellationToken = default) => Do($"search {text}", (IReadOnlyList<MpdSong>)[Song("found.mp3", "Found", 0)]);
    public Task<IReadOnlyList<MpdSong>> ListSongsAsync(string folder = "", CancellationToken cancellationToken = default) =>
        Do($"find {folder}", (IReadOnlyList<MpdSong>)(folder == "" ? Library : folder == "Rock" ? [Song("Rock/x.flac", "X", 0), Song("Rock/y.flac", "Y", 0)] : [Song("song.mp3", "Song", 0)]));
    public Task<IReadOnlyList<string>> ListTagAsync(string tag, CancellationToken cancellationToken = default) => Do($"list {tag}", (IReadOnlyList<string>)[]);
    public Task<int> UpdateDatabaseAsync(string path = "", CancellationToken cancellationToken = default) => Do("update", 1);
    public Task<IReadOnlyList<MpdOutput>> GetOutputsAsync(CancellationToken cancellationToken = default) => Do("outputs", (IReadOnlyList<MpdOutput>)OutputList);
    public Task SetOutputEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default) => Do($"output {id} {enabled}");
    public Task<MpdPicture?> GetPictureAsync(string uri, CancellationToken cancellationToken = default) => Do($"picture {uri}", Picture);
    public ValueTask DisposeAsync() { Calls.Add("dispose"); return ValueTask.CompletedTask; }
}

internal sealed class FakeMpdConnector(FakeMpdClient client) : IMpdConnector
{
    public Exception? ConnectFailure { get; set; }
    public int ConnectCalls { get; private set; }
    public int FailConnectsRemaining { get; set; }
    public Action? OnConnect { get; set; }
    public Func<IReadOnlyList<string>, Task>? OnChange { get; private set; }
    public Exception? WatchFailure { get; set; }
    public MpdEndpoint? LastEndpoint { get; private set; }
    public TaskCompletionSource Watching { get; } = new();

    public Task<IMpdClient> ConnectAsync(MpdEndpoint endpoint, CancellationToken cancellationToken)
    {
        LastEndpoint = endpoint;
        ConnectCalls++;
        if (FailConnectsRemaining > 0) { FailConnectsRemaining--; return Task.FromException<IMpdClient>(new IOException("unreachable")); }
        OnConnect?.Invoke();
        return ConnectFailure is { } error ? Task.FromException<IMpdClient>(error) : Task.FromResult<IMpdClient>(client);
    }

    public IMpdChangeSource CreateChangeSource(MpdEndpoint endpoint) => new Source(this);

    public MpdEndpoint Locate(MpdConnectionSettings settings, string? password) =>
        settings.Automatic ? new MpdEndpoint("auto.local", 6600, password) : new MpdEndpoint(settings.Host, settings.Port, password);

    private sealed class Source(FakeMpdConnector owner) : IMpdChangeSource
    {
        public async Task WatchAsync(Func<IReadOnlyList<string>, Task> onChanged, CancellationToken cancellationToken)
        {
            owner.OnChange = onChanged;
            owner.Watching.TrySetResult();
            if (owner.WatchFailure is { } failure) throw failure;
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
