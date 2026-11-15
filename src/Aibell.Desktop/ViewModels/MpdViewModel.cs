using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Sockets;
using Aibell.Desktop.Localization;
using Aibell.Desktop.Services;
using Aibell.Mpd.Client;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public sealed record MpdPlaylistRow(MpdStoredPlaylist Playlist)
{
    public string Name => Playlist.Name;
}

public sealed partial class MpdOutputRow(MpdOutput output) : ObservableObject
{
    public MpdOutput Output { get; } = output;

    [ObservableProperty]
    private bool isEnabled = output.Enabled;

    public string Name => Output.Name;

    public string Plugin => Output.Plugin ?? "";
}

/// <summary>
/// The MPD connection: the server's stored playlists, outputs and statistics, and the data behind the Music view while MPD is selected.
/// Songs play through Aibell's own transport bar and play queue (see <see cref="MpdPlaybackEngine"/>); nothing here plays on its own, and every
/// action is a user click. It owns one command connection and one change watcher. The server drops a client that is silent for a minute,
/// so it pings now and then, and when a connection breaks it reconnects a few times before giving up.
/// </summary>
public sealed partial class MpdViewModel : ObservableObject, IMpdTransport, IMpdLibrarySource, IDisposable
{
    /// <summary>How long to wait before each reconnection attempt; the number of entries is the number of attempts.</summary>
    public static readonly TimeSpan[] DefaultRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6)];

    /// <summary>MPD closes a connection that sends nothing for <c>connection_timeout</c> (60 seconds by default); pinging well inside that keeps it open.</summary>
    public static readonly TimeSpan DefaultKeepAlive = TimeSpan.FromSeconds(20);

    private readonly IMpdConnector connector;
    private readonly Action<MpdConnectionSettings>? persist;
    private readonly Func<Func<Task>, Task> onUi;
    private readonly TimeSpan[] retryDelays;
    private readonly TimeSpan keepAlive;
    private readonly TimeSpan clockInterval;
    private readonly Action<Exception, string, string>? reportError;
    private string? lastReported;
    private Task? clock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private IMpdClient? client;
    private MpdEndpoint? endpoint;
    private CancellationTokenSource? session;
    private Task? watcher;
    private Task? pinger;
    private int generation;
    private bool oneShotByUs;
    private bool disposed;
    private MpdConnectionSettings settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect), nameof(CanDisconnect), nameof(IsDisconnected), nameof(CanControl), nameof(CanChangeConnection))]
    private bool isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect), nameof(CanChangeConnection), nameof(CanControl))]
    private bool isConnecting;

    [ObservableProperty]
    private bool isReconnecting;

    [ObservableProperty]
    private string statusMessage = Loc.Get("Mpd.NotConnected");

    [ObservableProperty]
    private string endpointText = "";

    [ObservableProperty]
    private MpdState state = MpdState.Stopped;

    [ObservableProperty]
    private TimeSpan elapsed;

    [ObservableProperty]
    private TimeSpan duration;

    [ObservableProperty]
    private int? serverVolume;

    [ObservableProperty]
    private int? currentSongId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUsePlaylist))]
    private MpdPlaylistRow? selectedPlaylist;

    [ObservableProperty]
    private bool isUpdatingDatabase;

    [ObservableProperty]
    private string statsText = "";

    /// <summary>Raised when the server's music library may have changed (connected, reconnected, database updated, disconnected).</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>Raised when the list of stored playlists changed.</summary>
    public event EventHandler? PlaylistsChanged;

    public MpdViewModel(IMpdConnector connector, MpdConnectionSettings? settings = null, Action<MpdConnectionSettings>? persist = null,
        Func<Func<Task>, Task>? onUi = null, TimeSpan[]? retryDelays = null, TimeSpan? keepAlive = null,
        TimeSpan? clockInterval = null, Action<Exception, string, string>? reportError = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        this.connector = connector;
        this.settings = settings ?? MpdConnectionSettings.Default;
        this.persist = persist;
        this.onUi = onUi ?? (action => action());
        this.retryDelays = retryDelays ?? DefaultRetryDelays;
        this.keepAlive = keepAlive ?? DefaultKeepAlive;
        this.clockInterval = clockInterval ?? TimeSpan.FromSeconds(1);
        this.reportError = reportError;
        Playlists.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPlaylists));
        Outputs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOutputs));
        EndpointText = connector.Locate(this.settings, null).ToString();
    }

    public ObservableCollection<MpdPlaylistRow> Playlists { get; } = [];

    public ObservableCollection<MpdOutputRow> Outputs { get; } = [];

    /// <summary>The password typed for this session. It is never written to disk.</summary>
    public string Password { get; set; } = "";

    public MpdConnectionSettings Settings => settings;

    public bool HasPlaylists => Playlists.Count > 0;

    public bool HasOutputs => Outputs.Count > 0;

    public bool IsDisconnected => !IsConnected;

    public bool CanConnect => !IsConnected && !IsConnecting && !disposed;

    public bool CanDisconnect => IsConnected;

    public bool CanChangeConnection => !IsConnected && !IsConnecting;

    public bool CanControl => IsConnected && !IsConnecting;

    public bool CanUsePlaylist => IsConnected && SelectedPlaylist is not null;

    /// <summary>Changes how to reach the server. Takes effect on the next connection; the host and port are saved, the password is not.</summary>
    public void ApplyConnection(MpdConnectionSettings next, string password)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (IsConnected || IsConnecting) return;
        settings = next;
        Password = password ?? "";
        persist?.Invoke(next);
        EndpointText = connector.Locate(settings, null).ToString();
        OnPropertyChanged(nameof(Settings));
    }

    // Connection

    public async Task ConnectAsync()
    {
        if (!CanConnect) return;
        IsConnecting = true;
        MpdEndpoint target = connector.Locate(settings, Password);
        EndpointText = target.ToString();
        StatusMessage = Loc.Format("Mpd.Connecting", target.ToString());
        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        attempt.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            client = await connector.ConnectAsync(target, attempt.Token);
            endpoint = target;
            generation++;
            IsConnected = true;
            StatusMessage = Loc.Format("Mpd.Connected", target.ToString(), client.ProtocolVersion);
            StartSession();
            await RefreshAsync();
            lastReported = null;
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            await DropConnectionAsync();
            StatusMessage = Loc.Format("Mpd.ConnectFailed", target.ToString(), Describe(exception));
            if (exception is not OperationCanceledException || !lifetime.IsCancellationRequested)
                Report(exception, Loc.Format("Mpd.Error.Connect", target.ToString()));
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public async Task DisconnectAsync()
    {
        await DropConnectionAsync();
        StatusMessage = Loc.Get("Mpd.NotConnected");
    }

    private void StartSession()
    {
        session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        int seen = generation;
        watcher = WatchAsync(connector.CreateChangeSource(endpoint!), seen, session.Token);
        pinger = KeepAliveAsync(seen, session.Token);
        clock = clockInterval > TimeSpan.Zero ? ClockAsync(session.Token) : null;
    }

    /// <summary>Stops the change watcher and the pinger and waits briefly for them to finish.</summary>
    private async Task StopSessionAsync()
    {
        CancellationTokenSource? ending = session;
        Task?[] running = [watcher, pinger, clock];
        session = null;
        watcher = pinger = clock = null;
        if (ending is not null) await ending.CancelAsync();
        foreach (Task? task in running.Where(task => task is not null))
        {
            try { await task!.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or SocketException or EndOfStreamException or MpdProtocolException or ObjectDisposedException) { }
        }
        ending?.Dispose();
    }

    private async Task DropConnectionAsync()
    {
        await StopSessionAsync();
        IMpdClient? closing = client;
        client = null;
        generation++;
        if (closing is not null)
        {
            try { await closing.DisposeAsync(); }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { }
        }
        IsConnected = false;
        IsReconnecting = false;
        State = MpdState.Stopped;
        Elapsed = Duration = TimeSpan.Zero;
        CurrentSongId = null;
        ServerVolume = null;
        Playlists.Clear();
        Outputs.Clear();
        SelectedPlaylist = null;
        IsUpdatingDatabase = false;
        StatsText = "";
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task WatchAsync(IMpdChangeSource source, int seen, CancellationToken token)
    {
        try
        {
            await source.WatchAsync(changed => onUi(() => ApplyChangesAsync(changed)), token);
        }
        catch (Exception exception) when (IsConnectionFailure(exception) && !token.IsCancellationRequested)
        {
            // Reconnecting stops this very task, so it runs on its own.
            _ = ReconnectInBackgroundAsync(seen, exception);
        }
    }

    /// <summary>Moves the shown position along while a song plays, so the transport bar's seek bar runs smoothly between server reports.</summary>
    private async Task ClockAsync(CancellationToken token)
    {
        using PeriodicTimer timer = new(clockInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token)) await onUi(() => { AdvanceClock(clockInterval); return Task.CompletedTask; });
        }
        catch (OperationCanceledException)
        {
            // Disconnecting or reconnecting ends the clock.
        }
    }

    private async Task KeepAliveAsync(int seen, CancellationToken token)
    {
        using PeriodicTimer timer = new(keepAlive);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                IMpdClient? active = client;
                if (active is null) return;
                await commandGate.WaitAsync(token);
                try { await active.PingAsync(token); }
                finally { commandGate.Release(); }
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnecting or reconnecting ends the loop.
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            _ = ReconnectInBackgroundAsync(seen, exception);
        }
    }

    private async Task ReconnectInBackgroundAsync(int seen, Exception cause)
    {
        try
        {
            await onUi(async () => await ReconnectAsync(seen, cause));
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // The window is closing.
        }
    }

    /// <summary>
    /// Replaces a broken connection: a few attempts with growing pauses, then gives up and shows why. Returns true when connected again.
    /// <paramref name="seen"/> is the connection the caller found broken; if another caller already replaced it, there is nothing to do.
    /// </summary>
    private async Task<bool> ReconnectAsync(int seen, Exception cause)
    {
        if (disposed || endpoint is null) return false;
        await commandGate.WaitAsync(lifetime.Token);
        try
        {
            if (generation != seen) return client is not null;
            IsReconnecting = true;
            await StopSessionAsync();
            IMpdClient? broken = client;
            client = null;
            if (broken is not null)
            {
                try { await broken.DisposeAsync(); }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { }
            }
            Exception last = cause;
            for (int attempt = 1; attempt <= retryDelays.Length; attempt++)
            {
                StatusMessage = Loc.Format("Mpd.Reconnecting", attempt, retryDelays.Length);
                await Task.Delay(retryDelays[attempt - 1], lifetime.Token);
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    client = await connector.ConnectAsync(endpoint, timeout.Token);
                }
                catch (Exception exception) when (IsConnectionFailure(exception) && !lifetime.IsCancellationRequested)
                {
                    last = exception;
                    continue;
                }
                generation++;
                StartSession();
                try
                {
                    await RefreshCoreAsync(session!.Token);
                }
                catch (Exception exception) when (IsConnectionFailure(exception) && !lifetime.IsCancellationRequested)
                {
                    last = exception;
                    await StopSessionAsync();
                    IMpdClient? failed = client;
                    client = null;
                    if (failed is not null) await failed.DisposeAsync();
                    continue;
                }
                StatusMessage = Loc.Get("Mpd.Reconnected");
                return true;
            }
            await DropConnectionAsync();
            StatusMessage = Loc.Format("Mpd.Lost", Describe(last));
            Report(last, Loc.Get("Mpd.Error.Lost"));
            return false;
        }
        finally
        {
            IsReconnecting = false;
            commandGate.Release();
        }
    }

    private async Task ApplyChangesAsync(IReadOnlyList<string> changed)
    {
        if (client is null) return;
        await RunAsync(async token =>
        {
            if (changed.Any(item => item is "player" or "mixer" or "options" or "playlist" or "update" or "partition")) await RefreshStatusAsync(token);
            if (changed.Contains("stored_playlist")) await RefreshPlaylistsAsync(token);
            if (changed.Contains("output")) await RefreshOutputsAsync(token);
            if (changed.Any(item => item is "database" or "update"))
            {
                await RefreshStatsAsync(token);
                LibraryChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    // Refreshing

    public Task RefreshAsync() => RunAsync(RefreshCoreAsync);

    private async Task RefreshCoreAsync(CancellationToken token)
    {
        await RefreshStatusAsync(token);
        await RefreshPlaylistsAsync(token);
        await RefreshOutputsAsync(token);
        await RefreshStatsAsync(token);
    }

    private async Task RefreshStatsAsync(CancellationToken token)
    {
        IMpdClient active = client ?? throw new InvalidOperationException();
        MpdStats stats = await active.GetStatsAsync(token);
        StatsText = Loc.Format("Mpd.Stats", stats.Songs, stats.Albums, stats.Artists);
    }

    private async Task RefreshStatusAsync(CancellationToken token)
    {
        IMpdClient active = client ?? throw new InvalidOperationException();
        MpdStatus status = await active.GetStatusAsync(token);
        if (oneShotByUs && status.State == MpdState.Stopped)
        {
            // The song we started has finished (or was stopped): give the server's "single" setting back as we found it.
            oneShotByUs = false;
            if (status.SingleMode == MpdSingleMode.OneShot) await active.SetSingleAsync(MpdSingleMode.Off, token);
        }
        State = status.State;
        Elapsed = status.Elapsed ?? TimeSpan.Zero;
        Duration = status.Duration ?? TimeSpan.Zero;
        ServerVolume = status.Volume;
        CurrentSongId = status.State == MpdState.Stopped ? null : status.SongId;
        IsUpdatingDatabase = status.IsUpdatingDatabase;
    }

    private async Task RefreshPlaylistsAsync(CancellationToken token)
    {
        IMpdClient active = client ?? throw new InvalidOperationException();
        IReadOnlyList<MpdStoredPlaylist> lists = await active.GetPlaylistsAsync(token);
        string? selected = SelectedPlaylist?.Name;
        Playlists.Clear();
        foreach (MpdStoredPlaylist list in lists.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)) Playlists.Add(new MpdPlaylistRow(list));
        SelectedPlaylist = Playlists.FirstOrDefault(row => row.Name == selected);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshOutputsAsync(CancellationToken token)
    {
        IMpdClient active = client ?? throw new InvalidOperationException();
        IReadOnlyList<MpdOutput> outputs = await active.GetOutputsAsync(token);
        Outputs.Clear();
        foreach (MpdOutput output in outputs) Outputs.Add(new MpdOutputRow(output));
    }

    /// <summary>Advances the shown position by the time that passed while playing; the next server report corrects any drift.</summary>
    public void AdvanceClock(TimeSpan passed)
    {
        if (State != MpdState.Playing || Duration <= TimeSpan.Zero) return;
        Elapsed = Elapsed + passed > Duration ? Duration : Elapsed + passed;
    }

    // Stored playlists

    /// <summary>Selects a stored playlist by name, as the sidebar's playlist list does.</summary>
    public void SelectPlaylist(string? name) => SelectedPlaylist = name is null ? null : Playlists.FirstOrDefault(row => row.Name == name);

    public Task DeletePlaylistAsync(MpdPlaylistRow? playlist) => RunAsync(async token =>
    {
        if (playlist is null) return;
        await client!.DeletePlaylistAsync(playlist.Name, token);
        await RefreshPlaylistsAsync(token);
    });

    public Task RenamePlaylistAsync(MpdPlaylistRow? playlist, string newName) => RunAsync(async token =>
    {
        if (playlist is null || string.IsNullOrWhiteSpace(newName) || newName.Trim() == playlist.Name) return;
        await client!.RenamePlaylistAsync(playlist.Name, newName.Trim(), token);
        await RefreshPlaylistsAsync(token);
        SelectedPlaylist = Playlists.FirstOrDefault(row => row.Name == newName.Trim());
    });

    /// <summary>Creates a stored playlist (or adds to one that exists) from songs given by their path in the music directory.</summary>
    public Task AddToPlaylistAsync(string name, IReadOnlyList<string> songUris) => RunAsync(async token =>
    {
        if (string.IsNullOrWhiteSpace(name) || songUris.Count == 0) return;
        string target = name.Trim();
        // A playlist never gets the same song twice: what is already in it (or repeated in this request) is left out.
        HashSet<string> present = Playlists.Any(row => row.Name == target)
            ? [.. (await client!.GetPlaylistSongsAsync(target, token)).Select(song => song.Uri)]
            : [];
        int added = 0;
        foreach (string uri in songUris)
        {
            if (!present.Add(uri)) continue;
            await client!.AddToPlaylistAsync(target, uri, token);
            added++;
        }
        await RefreshPlaylistsAsync(token);
        StatusMessage = added == 0 ? Loc.Format("Mpd.PlaylistAlready", target) : Loc.Format("Mpd.PlaylistFilled", target, added);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    });

    public Task UpdateDatabaseAsync() => RunAsync(async token =>
    {
        await client!.UpdateDatabaseAsync("", token);
        IsUpdatingDatabase = true;
        StatusMessage = Loc.Get("Mpd.Updating");
    });

    // Outputs

    public Task SetOutputEnabledAsync(MpdOutputRow? row, bool enabled) => RunAsync(async token =>
    {
        if (row is null) return;
        await client!.SetOutputEnabledAsync(row.Output.Id, enabled, token);
        row.IsEnabled = enabled;
    });

    // The transport the player uses (IMpdTransport). Failures become InvalidOperationException, like a local engine's.

    private async Task<T> Transport<T>(Func<IMpdClient, CancellationToken, Task<T>> action)
    {
        try
        {
            return await ExecuteAsync(token => action(client!, token));
        }
        catch (MpdProtocolException exception)
        {
            // Friendly text for the player's error window; the exception itself (with the MPD error code) stays behind its Details button.
            throw new InvalidOperationException(Loc.Format("Mpd.Error.Command", exception.Message), exception);
        }
    }

    private async Task<int> Transport(Func<IMpdClient, CancellationToken, Task> action) =>
        await Transport(async (active, token) =>
        {
            await action(active, token);
            await RefreshStatusAsync(token);
            return 0;
        });

    public Task<int> AddSongAsync(string songUri, CancellationToken cancellationToken) =>
        Transport((active, token) => active.AddIdAsync(songUri, null, token));

    /// <summary>
    /// Starts the song. The server is first told to stop after it (<c>single oneshot</c>) unless the user already chose a single mode, so
    /// it does not run on into a queue other clients are using; the setting is put back once the server stops.
    /// </summary>
    public Task PlaySongAsync(int songId, CancellationToken cancellationToken) => Transport(async (active, token) =>
    {
        MpdStatus status = await active.GetStatusAsync(token);
        if (status.SingleMode == MpdSingleMode.Off)
        {
            await active.SetSingleAsync(MpdSingleMode.OneShot, token);
            oneShotByUs = true;
        }
        await active.PlayIdAsync(songId, token);
    });

    public Task ResumeAsync(CancellationToken cancellationToken) => Transport((active, token) => active.PauseAsync(false, token));

    public Task PauseAsync(CancellationToken cancellationToken) => Transport((active, token) => active.PauseAsync(true, token));

    public Task StopAsync(CancellationToken cancellationToken) => Transport((active, token) => active.StopAsync(token));

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) => Transport((active, token) => active.SeekAsync(position, token));

    public Task SetServerVolumeAsync(int volume, CancellationToken cancellationToken) => Transport((active, token) => active.SetVolumeAsync(volume, token));

    public Task RemoveSongAsync(int songId, CancellationToken cancellationToken) => Transport(async (active, token) =>
    {
        try { await active.DeleteIdAsync(songId, token); }
        catch (MpdProtocolException exception) when (exception.Code == MpdProtocolException.NoExist) { }
    });

    public Task<byte[]?> GetCoverAsync(string songUri, CancellationToken cancellationToken) =>
        Transport(async (active, token) => (await active.GetPictureAsync(songUri, token))?.Data);

    // The data behind the Music view while MPD is selected (IMpdLibrarySource).

    public Task<IReadOnlyList<MpdSong>> GetAllSongsAsync(CancellationToken cancellationToken) => Transport((active, token) => active.ListSongsAsync("", token));

    public Task<IReadOnlyList<MpdSong>> GetPlaylistSongsAsync(string name, CancellationToken cancellationToken) =>
        Transport((active, token) => active.GetPlaylistSongsAsync(name, token));

    public Task RemoveFromPlaylistAsync(string name, string songUri, CancellationToken cancellationToken) => Transport(async (active, token) =>
    {
        IReadOnlyList<MpdSong> songs = await active.GetPlaylistSongsAsync(name, token);
        for (int position = 0; position < songs.Count; position++)
        {
            if (songs[position].Uri != songUri) continue;
            await active.RemoveFromPlaylistAsync(name, position, token);
            break;
        }
    });

    // Plumbing

    /// <summary>
    /// Offers a problem to the application's error window: the friendly sentence first, and behind its Details button the technical
    /// text (exception, stack trace, server, protocol, error code) that is also written to the log. The same problem is shown once
    /// until something succeeds again, so a dead server does not open one window per attempt. The password is never part of it.
    /// </summary>
    private void Report(Exception exception, string friendly)
    {
        string key = $"{exception.GetType().Name}|{friendly}";
        if (key == lastReported || reportError is null) return;
        lastReported = key;
        System.Text.StringBuilder context = new();
        context.AppendLine(CultureInfo.InvariantCulture, $"MPD server: {endpoint?.ToString() ?? EndpointText}");
        context.AppendLine(CultureInfo.InvariantCulture, $"Protocol version: {client?.ProtocolVersion ?? "not connected"}");
        context.AppendLine(CultureInfo.InvariantCulture, $"Status line: {StatusMessage}");
        if (exception is MpdProtocolException { Command.Length: > 0 } refusal)
            context.AppendLine(CultureInfo.InvariantCulture, $"MPD error {refusal.Code} in command '{refusal.Command}' (position {refusal.ListIndex} of the command list)");
        reportError(exception, friendly, context.ToString());
    }

    private static bool IsConnectionFailure(Exception exception) =>
        exception is IOException or SocketException or EndOfStreamException or ObjectDisposedException or OperationCanceledException or MpdProtocolException;

    private static bool IsBrokenConnection(Exception exception) => exception is IOException or SocketException or EndOfStreamException or ObjectDisposedException;

    private static string Describe(Exception exception) =>
        exception is OperationCanceledException ? Loc.Get("Mpd.TimedOut") : exception.Message;

    /// <summary>
    /// Runs one action against the server, one at a time. A connection that turns out to be broken is replaced (a few attempts) and the
    /// action runs once more; if that fails too the connection is closed. A refusal from MPD is thrown as it is.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        if (disposed || client is null || session is null) throw new InvalidOperationException(Loc.Get("Mpd.ConnectFirst"));
        for (int pass = 0; ; pass++)
        {
            int seen = generation;
            CancellationToken token = session?.Token ?? lifetime.Token;
            try
            {
                await commandGate.WaitAsync(token);
                try
                {
                    return await action(token);
                }
                finally
                {
                    commandGate.Release();
                }
            }
            catch (Exception exception) when (IsBrokenConnection(exception) && !lifetime.IsCancellationRequested)
            {
                if (pass == 0 && await ReconnectAsync(seen, exception)) continue;
                if (client is not null) await DropConnectionAsync();
                StatusMessage = Loc.Format("Mpd.Lost", exception.Message);
                throw new InvalidOperationException(Loc.Get("Mpd.Error.Lost"), exception);
            }
        }
    }

    /// <summary>Runs one server action and shows what went wrong instead of throwing.</summary>
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (disposed || client is null || session is null) return;
        try
        {
            await ExecuteAsync(async token =>
            {
                await action(token);
                return 0;
            });
        }
        catch (OperationCanceledException)
        {
            // Disconnecting or closing the window cancels the work in flight.
        }
        catch (MpdProtocolException exception)
        {
            StatusMessage = Loc.Format("Mpd.CommandFailed", exception.Message);
            Report(exception, Loc.Format("Mpd.Error.Command", exception.Message));
        }
        catch (InvalidOperationException exception) when (exception.InnerException is not null || !IsConnected)
        {
            // The connection could not be kept; the status line already says so.
        }
    }

    /// <summary>
    /// What to do with a song Aibell started on the server when Aibell closes (set by the host): stop it and take it out of the server's
    /// queue, so closing the application also ends the music it was playing.
    /// </summary>
    public Func<CancellationToken, Task>? ReleasePlaybackAsync { get; set; }

    public async Task ShutdownAsync()
    {
        if (disposed) return;
        if (ReleasePlaybackAsync is { } release && IsConnected)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            try { await release(timeout.Token); }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException) { }
        }
        await lifetime.CancelAsync();
        await DropConnectionAsync();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        commandGate.Dispose();
        lifetime.Dispose();
    }
}
