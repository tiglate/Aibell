using System.ComponentModel;
using Aibell.Application.Playback;
using Aibell.Desktop.Localization;
using Aibell.Domain.Media;
using Aibell.Mpd.Client;

namespace Aibell.Desktop.Services;

/// <summary>
/// Plays <c>mpd://</c> tracks on an MPD server, so the server is one more output behind Aibell's own transport bar and play queue.
/// Aibell keeps the queue; the server only ever holds the one song being played, added behind whatever is already in its own queue
/// and set to stop after it (<c>single oneshot</c>), so a queue other clients are using is left alone. When the song ends on its
/// own the engine reports <see cref="PlaybackStatus.Ended"/> and Aibell moves on, exactly as with a local file.
/// </summary>
public sealed class MpdPlaybackEngine : IPlaybackEngine, IDisposable
{
    private IMpdTransport? transport;
    private Track? loaded;
    private int? ownedId;
    private bool userStopped = true;
    private bool ended;
    private bool wasPlaying;
    private bool loading;
    private bool muted;
    private int? volumeBeforeMute;
    private TimeSpan? pendingSeek;
    private string? error;
    private PlaybackSnapshot last = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0);
    private int? lastServerVolume;
    private bool lastConnected;

    public event EventHandler<PlaybackSnapshot>? StateChanged;

    /// <summary>Raised when the server's volume or its availability changed, whether or not a song of ours is loaded.</summary>
    public event EventHandler? ServerVolumeChanged;

    /// <summary>True from loading an MPD track until another engine takes over.</summary>
    public bool IsActive => loaded is not null;

    public PlaybackSnapshot Snapshot => last = Build();

    /// <summary>The connection the engine plays through, or <c>null</c> before the MPD view was opened.</summary>
    public IMpdTransport? Transport => transport;

    /// <summary>Connects the engine to the view model that owns the server connection.</summary>
    public void Attach(IMpdTransport next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (ReferenceEquals(transport, next)) return;
        if (transport is not null) transport.PropertyChanged -= OnTransportChanged;
        transport = next;
        transport.PropertyChanged += OnTransportChanged;
    }

    private IMpdTransport Connected()
    {
        if (transport is not { IsConnected: true } active) throw new InvalidOperationException(Loc.Get("Mpd.ConnectFirst"));
        return active;
    }

    public async Task LoadAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!MpdTracks.TryGetSongUri(track.Location, out string songUri)) throw new ArgumentException("Only MPD tracks can be played on the server.", nameof(track));
        IMpdTransport active = Connected();
        userStopped = true;
        await DropOwnedAsync(active, cancellationToken);
        userStopped = false;
        ended = false;
        error = null;
        wasPlaying = false;
        pendingSeek = null;
        loading = true;
        loaded = track;
        try
        {
            ownedId = await active.AddSongAsync(songUri, cancellationToken);
        }
        catch
        {
            loaded = null;
            throw;
        }
        Publish();
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        if (loaded is null || ownedId is not { } id) return;
        IMpdTransport active = Connected();
        userStopped = false;
        ended = false;
        loading = false;
        if (active.CurrentSongId == id && active.State == MpdState.Paused) await active.ResumeAsync(cancellationToken);
        else if (active.CurrentSongId == id && active.State == MpdState.Playing) { }
        else
        {
            await active.PlaySongAsync(id, cancellationToken);
            if (pendingSeek is { } position)
            {
                pendingSeek = null;
                await active.SeekAsync(position, cancellationToken);
            }
        }
        Publish();
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        if (loaded is null || transport is not { IsConnected: true } active) return;
        if (active.State == MpdState.Playing && active.CurrentSongId == ownedId) await active.PauseAsync(cancellationToken);
        Publish();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (loaded is null) return;
        userStopped = true;
        loading = false;
        if (transport is { IsConnected: true } active && active.CurrentSongId == ownedId && active.State != MpdState.Stopped)
            await active.StopAsync(cancellationToken);
        Publish();
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (loaded is null) return;
        if (transport is { IsConnected: true } active && active.CurrentSongId == ownedId && active.State != MpdState.Stopped) await active.SeekAsync(position, cancellationToken);
        else pendingSeek = position;
        Publish();
    }

    /// <summary>
    /// Sets the server's volume. The player re-applies its own volume every time it loads a track, and that must not reach the server:
    /// MPD's volume is a cubic scale on the audio stream (71% is about 9 dB down), stacked on the headset's own volume, which throws away
    /// resolution and changes a level the user set on the server. So volume calls that arrive between loading a track and starting it
    /// are ignored; a call from the volume bar while a song plays (or none is loaded) is applied.
    /// </summary>
    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        if (loading || transport is not { IsConnected: true } active) return;
        int percent = (int)Math.Round(Math.Clamp(volume, 0, 1) * 100, MidpointRounding.AwayFromZero);
        if (muted) { volumeBeforeMute = percent; return; }
        await active.SetServerVolumeAsync(percent, cancellationToken);
    }

    /// <summary>MPD has no mute, so muting sets the volume to 0 and unmuting puts it back.</summary>
    public async Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default)
    {
        if (loading || transport is not { IsConnected: true } active || isMuted == muted) return;
        if (isMuted)
        {
            volumeBeforeMute = active.ServerVolume;
            muted = true;
            await active.SetServerVolumeAsync(0, cancellationToken);
        }
        else
        {
            muted = false;
            if (volumeBeforeMute is { } previous) await active.SetServerVolumeAsync(previous, cancellationToken);
        }
        Publish();
    }

    /// <summary>Gives the server back: stops what this engine started and removes its queue entry. Used when another engine takes over.</summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (loaded is null) return;
        userStopped = true;
        loading = false;
        if (transport is { IsConnected: true } active)
        {
            try
            {
                await DropOwnedAsync(active, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // The server went away while we were letting go of it; there is nothing left to release.
            }
        }
        loaded = null;
        ownedId = null;
        muted = false;
        Publish();
    }

    /// <summary>Stops the song this engine added if it is still playing, then takes it out of the server queue.</summary>
    private async Task DropOwnedAsync(IMpdTransport active, CancellationToken cancellationToken)
    {
        if (ownedId is not { } id) return;
        if (active.CurrentSongId == id && active.State != MpdState.Stopped) await active.StopAsync(cancellationToken);
        ownedId = null;
        await active.RemoveSongAsync(id, cancellationToken);
    }

    private void OnTransportChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (transport is { } active && (active.ServerVolume != lastServerVolume || active.IsConnected != lastConnected))
        {
            lastServerVolume = active.ServerVolume;
            lastConnected = active.IsConnected;
            ServerVolumeChanged?.Invoke(this, EventArgs.Empty);
        }
        if (loaded is null) return;
        Publish();
    }

    private void Publish()
    {
        PlaybackSnapshot before = last;
        PlaybackSnapshot now = Snapshot;
        if (now.Status != before.Status || now.Position != before.Position || now.Track != before.Track || now.Volume != before.Volume || now.Error != before.Error)
            StateChanged?.Invoke(this, now);
    }

    private PlaybackSnapshot Build()
    {
        double volume = transport?.ServerVolume is { } percent ? percent / 100.0 : 0;
        if (loaded is null) return new PlaybackSnapshot(PlaybackStatus.Stopped, null, TimeSpan.Zero, volume, muted);
        if (transport is not { IsConnected: true } active)
        {
            error = Loc.Get("Mpd.ConnectionLost");
            return new PlaybackSnapshot(PlaybackStatus.Faulted, loaded, TimeSpan.Zero, volume, muted, error, error);
        }
        bool ours = ownedId is not null && active.CurrentSongId == ownedId;
        TimeSpan position = ours && active.State != MpdState.Stopped ? active.Elapsed : TimeSpan.Zero;
        PlaybackStatus status;
        if (ours && active.State == MpdState.Playing) { status = PlaybackStatus.Playing; wasPlaying = true; }
        else if (ours && active.State == MpdState.Paused) status = PlaybackStatus.Paused;
        else if (wasPlaying && !userStopped && (ours || active.State == MpdState.Stopped))
        {
            // It was playing and nobody asked it to stop, and now the server is idle: the song finished.
            ended = true;
            wasPlaying = false;
            status = PlaybackStatus.Ended;
        }
        else if (ended) status = PlaybackStatus.Ended;
        else status = PlaybackStatus.Stopped;
        if (status == PlaybackStatus.Ended) position = loaded.Duration;
        return new PlaybackSnapshot(status, loaded, position, volume, muted);
    }

    public void Dispose()
    {
        if (transport is not null) transport.PropertyChanged -= OnTransportChanged;
        transport = null;
    }
}
