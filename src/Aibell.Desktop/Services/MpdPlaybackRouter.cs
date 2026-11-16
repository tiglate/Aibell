using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;

namespace Aibell.Desktop.Services;

/// <summary>
/// The player the rest of the application talks to. Tracks whose location is <c>mpd://</c> play on the MPD server and everything else on
/// the local engine; loading one kind first stops the other, so the two never play together. The equalizer and speed controls belong to
/// the local engine.
/// <para>
/// The volume bar belongs to whichever output is in use: while an MPD song is loaded, or while MPD is selected in the Library list and
/// connected (<see cref="ServerSelected"/>), it shows and sets the server's volume, and the local engine's volume is left as it was.
/// The player re-applies its volume and mute state every time it loads a track; that happens between loading and starting a track and
/// is never passed to the server, nor, right after MPD, to the local engine (which keeps the level it had).
/// </para>
/// </summary>
public sealed class MpdPlaybackRouter : IPlaybackEngine, IEqualizerControl, IPlaybackRateControl, ISpectrumSource, IAsyncDisposable
{
    private readonly IPlaybackEngine local;
    private readonly MpdPlaybackEngine mpd;
    private bool mpdActive;
    private bool serverSelected;
    private bool loadSequence;
    private bool ignoreVolumeOnce;
    private bool ignoreMuteOnce;

    public MpdPlaybackRouter(IPlaybackEngine local, MpdPlaybackEngine mpd)
    {
        this.local = local ?? throw new ArgumentNullException(nameof(local));
        this.mpd = mpd ?? throw new ArgumentNullException(nameof(mpd));
        local.StateChanged += (_, snapshot) => { if (!mpdActive) StateChanged?.Invoke(this, Compose(snapshot)); };
        mpd.StateChanged += (_, snapshot) => { if (mpdActive) StateChanged?.Invoke(this, Compose(snapshot)); };
        mpd.ServerVolumeChanged += (_, _) => StateChanged?.Invoke(this, Snapshot);
    }

    public MpdPlaybackEngine Mpd => mpd;

    public event EventHandler<PlaybackSnapshot>? StateChanged;

    private IPlaybackEngine Active => mpdActive ? mpd : local;

    /// <summary>Set while MPD is the selected library source: the volume bar then controls the server's volume (once it is connected).</summary>
    public bool ServerSelected
    {
        get => serverSelected;
        set
        {
            if (serverSelected == value) return;
            serverSelected = value;
            StateChanged?.Invoke(this, Snapshot);
        }
    }

    /// <summary>True when the volume bar and the mute button act on the MPD server.</summary>
    public bool ControlsServerVolume => (mpdActive || serverSelected) && mpd.Transport is { IsConnected: true };

    public PlaybackSnapshot Snapshot => Compose(mpdActive ? mpd.Snapshot : local.Snapshot);

    private PlaybackSnapshot Compose(PlaybackSnapshot snapshot)
    {
        if (!ControlsServerVolume) return snapshot;
        PlaybackSnapshot server = mpd.Snapshot;
        return snapshot with { Volume = server.Volume, IsMuted = server.IsMuted };
    }

    public async Task LoadAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ignoreVolumeOnce = ignoreMuteOnce = false;
        bool toMpd = MpdTracks.TryGetSongUri(track.Location, out _);
        if (toMpd)
        {
            await local.StopAsync(cancellationToken);
            mpdActive = true;
            try
            {
                await mpd.LoadAsync(track, cancellationToken);
            }
            catch
            {
                mpdActive = false;
                throw;
            }
        }
        else
        {
            if (mpdActive || mpd.IsActive)
            {
                await mpd.ReleaseAsync(cancellationToken);
                mpdActive = false;
                // The player is about to hand its own volume and mute state to "the" engine; they are still the server's, so the local
                // engine keeps what it had.
                if (!ControlsServerVolume) ignoreVolumeOnce = ignoreMuteOnce = true;
            }
            await local.LoadAsync(track, cancellationToken);
        }
        loadSequence = true;
        StateChanged?.Invoke(this, Snapshot);
    }

    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        loadSequence = false;
        return Active.PlayAsync(cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default) => Active.PauseAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        loadSequence = false;
        return Active.StopAsync(cancellationToken);
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Active.SeekAsync(position, cancellationToken);

    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        if (ControlsServerVolume)
        {
            if (!loadSequence) await mpd.SetVolumeAsync(volume, cancellationToken);
            return;
        }
        if (ignoreVolumeOnce)
        {
            ignoreVolumeOnce = false;
            return;
        }
        await local.SetVolumeAsync(volume, cancellationToken);
    }

    public async Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default)
    {
        if (ControlsServerVolume)
        {
            if (!loadSequence) await mpd.SetMutedAsync(isMuted, cancellationToken);
            return;
        }
        if (ignoreMuteOnce)
        {
            ignoreMuteOnce = false;
            return;
        }
        await local.SetMutedAsync(isMuted, cancellationToken);
    }

    public EqualizerSettings Equalizer => local is IEqualizerControl control ? control.Equalizer : EqualizerSettings.Flat;

    public Task SetEqualizerAsync(EqualizerSettings settings, CancellationToken cancellationToken = default) =>
        local is IEqualizerControl control ? control.SetEqualizerAsync(settings, cancellationToken) : Task.CompletedTask;

    // The sound of an MPD server never passes through this program, so there is nothing to analyse while it plays.
    public bool IsSpectrumAvailable => !mpdActive && local is ISpectrumSource { IsSpectrumAvailable: true };

    public void SetSpectrumEnabled(bool enabled)
    {
        if (local is ISpectrumSource source) source.SetSpectrumEnabled(enabled);
    }

    public bool TryReadSpectrum(Span<float> bands)
    {
        if (!mpdActive && local is ISpectrumSource source) return source.TryReadSpectrum(bands);
        bands.Clear();
        return false;
    }

    public double Rate => local is IPlaybackRateControl control ? control.Rate : 1.0;

    public Task SetRateAsync(double rate, CancellationToken cancellationToken = default) =>
        local is IPlaybackRateControl control ? control.SetRateAsync(rate, cancellationToken) : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            await mpd.ReleaseAsync(timeout.Token);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
            // Closing must not wait on a server that has gone away.
        }
        mpd.Dispose();
        if (local is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else if (local is IDisposable disposable) disposable.Dispose();
    }
}
