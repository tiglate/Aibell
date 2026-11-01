using System.Globalization;
using Aibell.Application.Discs;
using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using LibVLCSharp.Shared;
using LibVlcInstance = LibVLCSharp.Shared.LibVLC;

namespace Aibell.Playback.LibVLC;

public sealed class LibVlcPlaybackEngine : IPlaybackEngine, IPlaybackRateControl, IEqualizerControl, IDisposable
{
    private readonly object stateLock = new();
    private readonly object operationLock = new();
    private CancellationTokenSource? startupCancellation;
    private Task? startupTask;
    private long inputGeneration;
    private bool pauseAfterStartup;
    private TimeSpan startupPosition;
    private LibVlcInstance? libVlc;
    private MediaPlayer? player;
    private Media? media;
    private TimeSpan pendingStartPosition;
    private PlaybackSnapshot snapshot = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8, false);
    private bool disposed;
    private double currentRate = 1.0;
    private EqualizerSettings equalizer = EqualizerSettings.Flat;
    private readonly string[] additionalOptions;

    public LibVlcPlaybackEngine() : this([])
    {
    }

    // Native integration tests configure headless output and repeating test input.
    internal LibVlcPlaybackEngine(string[] additionalOptions)
    {
        this.additionalOptions = additionalOptions.ToArray();
    }

    public PlaybackSnapshot Snapshot
    {
        get
        {
            lock (stateLock)
            {
                return snapshot;
            }
        }
    }

    public event EventHandler<PlaybackSnapshot>? StateChanged;

    public Task LoadAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            lock (operationLock)
            {
                InvalidateStartup();
                MediaPlayer activePlayer = EnsurePlayer();
                pendingStartPosition = TimeSpan.Zero;
                ReplaceMedia(activePlayer, track, TimeSpan.Zero);
                Publish(current => current with
                {
                    Status = PlaybackStatus.Stopped,
                    Track = track,
                    Position = TimeSpan.Zero,
                    Error = null
                });
            }
        }
        catch (Exception exception) when (exception is VLCException or DllNotFoundException or BadImageFormatException)
        {
            PublishFault("LibVLC is unavailable. Install a matching native VLC runtime; on Windows, AIBELL_LIBVLC_PATH can select its folder.");
            throw new InvalidOperationException(Snapshot.Error, exception);
        }

        return Task.CompletedTask;
    }

    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (operationLock)
        {
            MediaPlayer activePlayer = EnsurePlayer();
            if (startupTask is { IsCompleted: false })
            {
                pauseAfterStartup = false;
                return startupTask.WaitAsync(cancellationToken);
            }
            Track track = Snapshot.Track ?? throw new InvalidOperationException("Load a track before starting playback.");
            TimeSpan requestedStart = TimeSpan.Zero;
            if (!HasActiveInput(activePlayer))
            {
                requestedStart = pendingStartPosition;
                ReplaceMedia(activePlayer, track, requestedStart);
                pendingStartPosition = TimeSpan.Zero;
            }

            CancellationTokenSource? startup = null;
            if (requestedStart > TimeSpan.Zero)
            {
                startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(TimeSpan.FromSeconds(10));
                Volatile.Write(ref startupCancellation, startup);
                pauseAfterStartup = false;
                startupPosition = requestedStart;
            }
            if (!activePlayer.Play())
            {
                Volatile.Write(ref startupCancellation, null);
                startup?.Dispose();
                PublishFault("LibVLC could not start playback.");
                throw new InvalidOperationException(Snapshot.Error);
            }
            if (startup is null) return Task.CompletedTask;
            startupTask = FinishResumeStartupAsync(activePlayer, inputGeneration, startup, cancellationToken);
            return startupTask;
        }
    }

    private async Task FinishResumeStartupAsync(MediaPlayer activePlayer, long generation,
        CancellationTokenSource startup, CancellationToken callerToken)
    {
        try
        {
            while (true)
            {
                // Never block a LibVLC callback or retain the operation lock across an await.
                await Task.Delay(10, startup.Token).ConfigureAwait(false);
                lock (operationLock)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    if (generation != inputGeneration || disposed)
                        throw new OperationCanceledException(startup.Token);
                    if (activePlayer.State == VLCState.Error)
                        throw new InvalidOperationException("LibVLC could not prepare the saved playback position.");
                    if (activePlayer.State == VLCState.Ended)
                    {
                        if (startupPosition < Snapshot.Track!.Duration)
                            throw new InvalidOperationException("LibVLC ended before preparing the saved playback position.");
                        return;
                    }
                    if (activePlayer.State != VLCState.Paused) continue;
                    // start-paused holds output while the input buffers. LibVLC queues these
                    // controls in order: seek flushes the opening buffers before unpausing.
                    activePlayer.Time = (long)startupPosition.TotalMilliseconds;
                    Volatile.Write(ref startupCancellation, null);
                    if (!pauseAfterStartup) activePlayer.SetPause(false);
                    else PublishStatus(PlaybackStatus.Paused);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            lock (operationLock)
            {
                if (generation == inputGeneration && !disposed)
                {
                    Volatile.Write(ref startupCancellation, null);
                    activePlayer.Stop();
                    if (exception is OperationCanceledException && callerToken.IsCancellationRequested)
                        Publish(current => current with { Status = PlaybackStatus.Stopped, Position = TimeSpan.Zero, Error = null });
                    else
                        PublishFault(exception is OperationCanceledException
                            ? "LibVLC could not prepare the saved playback position within ten seconds."
                            : exception.Message);
                }
            }
            if (exception is OperationCanceledException && generation == inputGeneration && !callerToken.IsCancellationRequested)
                throw new InvalidOperationException("LibVLC timed out while preparing the saved playback position.", exception);
            throw;
        }
        finally
        {
            lock (operationLock)
            {
                if (ReferenceEquals(startupCancellation, startup)) Volatile.Write(ref startupCancellation, null);
                startup.Dispose();
            }
        }
    }

    private void InvalidateStartup()
    {
        inputGeneration++;
        startupCancellation?.Cancel();
        Volatile.Write(ref startupCancellation, null);
        startupTask = null;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (operationLock)
        {
            if (startupCancellation is not null) pauseAfterStartup = true;
            else EnsurePlayer().SetPause(true);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? interrupted;
        lock (operationLock)
        {
            interrupted = startupTask is { IsCompleted: false } ? startupTask : null;
            InvalidateStartup();
            EnsurePlayer().Stop();
            pendingStartPosition = TimeSpan.Zero;
            Publish(current => current with
            {
                Status = PlaybackStatus.Stopped,
                Position = TimeSpan.Zero,
                Error = null
            });
        }
        return DrainInterruptedStartupAsync(interrupted);
    }

    private static async Task DrainInterruptedStartupAsync(Task? interrupted)
    {
        if (interrupted is null) return;
        try { await interrupted.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (operationLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Track? track = Snapshot.Track;
            if (track is null) return Task.CompletedTask;
            MediaPlayer activePlayer = EnsurePlayer();
            TimeSpan clamped = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : position > track.Duration ? track.Duration : position;
            if (startupCancellation is not null) startupPosition = clamped;
            else if (HasActiveInput(activePlayer)) activePlayer.Time = (long)clamped.TotalMilliseconds;
            else pendingStartPosition = clamped;
            Publish(current => current with { Position = clamped });
        }
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(volume))
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be finite.");
        }
        double clamped = Math.Clamp(volume, 0, 1);
        lock (operationLock)
        {
            EnsurePlayer().Volume = (int)Math.Round(clamped * 100, MidpointRounding.AwayFromZero);
            Publish(current => current with { Volume = clamped });
        }
        return Task.CompletedTask;
    }

    public double Rate
    {
        get { lock (operationLock) return currentRate; }
    }

    public Task SetRateAsync(double rate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(rate) || rate is < 0.5 or > 4)
            throw new ArgumentOutOfRangeException(nameof(rate), "Playback speed must be between 0.5x and 4x.");
        lock (operationLock)
        {
            currentRate = rate;
            // LibVLC may reset the speed for a new input, so it is applied again when playback starts.
            ApplyRate(EnsurePlayer());
        }
        return Task.CompletedTask;
    }

    // The speed the native player is actually using, for integration tests.
    internal float NativeRate { get { lock (operationLock) return player?.Rate ?? 1f; } }

    private void ReapplyRate()
    {
        lock (operationLock)
        {
            if (!disposed && player is { } active) ApplyRate(active);
        }
    }

    private void ApplyRate(MediaPlayer activePlayer)
    {
        if (Math.Abs(activePlayer.Rate - currentRate) > 0.001f) activePlayer.SetRate((float)currentRate);
    }

    public EqualizerSettings Equalizer
    {
        get { lock (operationLock) return equalizer; }
    }

    public Task SetEqualizerAsync(EqualizerSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        lock (operationLock)
        {
            equalizer = settings.Normalized();
            // Without a player yet the values wait here; EnsurePlayer applies them, so startup never loads LibVLC for this.
            if (player is { } active) ApplyEqualizer(active);
        }
        return Task.CompletedTask;
    }

    // LibVLC's own "Flat" preset has a preamp of 12, and a preamp of 0 plays 12 dB quieter (measured), so 0 dB here is 12 there.
    internal static float NativePreamp(EqualizerSettings settings) => (float)Math.Clamp(settings.Preamp + 12, 0, 20);

    // LibVLC copies the values when they are set, so every change needs a fresh native equalizer.
    private void ApplyEqualizer(MediaPlayer activePlayer)
    {
        if (!equalizer.IsEnabled)
        {
            activePlayer.UnsetEqualizer();
            return;
        }

        using Equalizer native = new();
        native.SetPreamp(NativePreamp(equalizer));
        for (uint band = 0; band < EqualizerSettings.BandCount; band++) native.SetAmp((float)equalizer.Bands[(int)band], band);
        activePlayer.SetEqualizer(native);
    }

    public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (operationLock)
        {
            EnsurePlayer().Mute = isMuted;
            Publish(current => current with { IsMuted = isMuted });
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (operationLock)
        {
            if (disposed) return;
            InvalidateStartup();
            disposed = true;
            media?.Dispose();
            player?.Dispose();
            libVlc?.Dispose();
        }
    }

    private static bool HasActiveInput(MediaPlayer activePlayer) => activePlayer.State is
        VLCState.Opening or VLCState.Buffering or VLCState.Playing or VLCState.Paused;

    private Media CreateMedia(Uri location)
    {
        // Audio CD tracks are a device plus a track number: cdda:///dev/sr0 with the cdda-track input option.
        if (!DiscLocation.TryParse(location, out string drive, out int trackNumber)) return new Media(libVlc!, location);
        Media disc = new(libVlc!, "cdda:///" + (OperatingSystem.IsWindows() ? drive + "/" : drive.TrimStart('/')), FromType.FromLocation);
        disc.AddOption(":cdda-track=" + trackNumber.ToString(CultureInfo.InvariantCulture));
        return disc;
    }

    private void ReplaceMedia(MediaPlayer activePlayer, Track track, TimeSpan startPosition)
    {
        Media next = CreateMedia(track.Location);
        if (startPosition > TimeSpan.Zero)
        {
            next.AddOption(":start-paused");
            next.AddOption(":start-time=" + startPosition.TotalSeconds.ToString("R", CultureInfo.InvariantCulture));
        }
        Media? previous = media;
        media = next;
        activePlayer.Media = next;
        previous?.Dispose();
    }

    private MediaPlayer EnsurePlayer()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (player is not null)
        {
            return player;
        }

        Core.Initialize(LibVlcRuntime.ResolveDirectory(OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("AIBELL_LIBVLC_PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        string[] options = ["--no-video", "--quiet", .. additionalOptions];
        libVlc = new LibVlcInstance(options);
        player = new MediaPlayer(libVlc)
        {
            Volume = (int)Math.Round(snapshot.Volume * 100, MidpointRounding.AwayFromZero)
        };
        ApplyEqualizer(player);
        player.Playing += (_, _) =>
        {
            // Never take the operation lock or call back into LibVLC on its event thread: Stop holds that lock while it
            // waits for this thread. Re-apply the chosen speed from the pool instead.
            _ = Task.Run(ReapplyRate);
            PublishStatus(PlaybackStatus.Playing);
        };
        player.Paused += (_, _) => PublishStatus(PlaybackStatus.Paused);
        player.Stopped += (_, _) => Publish(PlaybackSnapshotRules.AfterNativeStop);
        player.EndReached += (_, _) => Publish(current => current with
        {
            Status = PlaybackStatus.Ended,
            Position = current.Track?.Duration ?? TimeSpan.Zero
        });
        player.EncounteredError += (_, _) => PublishFault("LibVLC encountered a playback error.");
        player.TimeChanged += (_, eventArgs) => Publish(current => PlaybackSnapshotRules.WithEnginePosition(
            current, TimeSpan.FromMilliseconds(Math.Max(0, eventArgs.Time))));
        return player;
    }

    private void PublishStatus(PlaybackStatus status)
    {
        if (Volatile.Read(ref startupCancellation) is null)
            Publish(current => current with { Status = status, Error = null });
    }

    private void PublishFault(string message)
    {
        Publish(current => current with { Status = PlaybackStatus.Faulted, Error = message });
    }

    private void Publish(Func<PlaybackSnapshot, PlaybackSnapshot> update)
    {
        PlaybackSnapshot next;
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }
            next = update(snapshot);
            if (next == snapshot)
            {
                return;
            }
            snapshot = next;
        }

        StateChanged?.Invoke(this, next);
    }
}
