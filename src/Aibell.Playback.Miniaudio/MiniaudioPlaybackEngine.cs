using Aibell.Application.Discs;
using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Playback.Core;

namespace Aibell.Playback.Miniaudio;

/// <summary>
/// The built-in player: miniaudio for output and the common formats (MP3, FLAC, Ogg Vorbis, WAV), Media Foundation for AAC and
/// friends on Windows. It needs no other player installed. The equalizer and the pitch-preserving playback speed are done here, on the decoded audio.
/// </summary>
public sealed class MiniaudioPlaybackEngine : IPlaybackEngine, IPlaybackRateControl, IEqualizerControl, IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private readonly object gate = new();
    private readonly object stateLock = new();
    private readonly AudioSourceFactory sources;
    private readonly bool useNullBackend;
    private readonly Timer positionTimer;
    private PlaybackSnapshot snapshot = new(PlaybackStatus.Stopped, null, TimeSpan.Zero, 0.8, false);
    private double playbackRate = 1.0;
    private EqualizerSettings equalizer = EqualizerSettings.Flat;
    private volatile EqualizerProcessor? equalizerProcessor;
    private AudioOutput? output;
    private DecodePipeline? pipeline;
    private volatile DecodePipeline? rendering;
    private TimeSpan pendingStart;
    private long generation;
    private int endHandled;
    private float targetGain = 0.8f;
    private float currentGain = 0.8f;
    private bool disposed;

    /// <param name="http">Client for network streams.</param>
    /// <param name="managedCodecs">Use the managed (C#) decoders where they exist, falling back to miniaudio's for the rest.</param>
    /// <param name="soundFontPath">A SoundFont 2 file the user chose for MIDI playback, or null for the built-in instruments.</param>
    /// <param name="drives">Where audio CD tables of contents come from; without it the built-in player does not play discs.</param>
    public MiniaudioPlaybackEngine(HttpClient? http = null, bool managedCodecs = false, string? soundFontPath = null, IOpticalDriveService? drives = null) : this(http, Environment.GetEnvironmentVariable("AIBELL_AUDIO_NULL") == "1", managedCodecs, soundFontPath, drives)
    {
    }

    // Tests run on machines without sound hardware, so they use miniaudio's silent backend.
    internal MiniaudioPlaybackEngine(HttpClient? http, bool useNullBackend, bool managedCodecs = false, string? soundFontPath = null, IOpticalDriveService? drives = null)
    {
        // The managed decoders live in Aibell.Playback.Core; miniaudio's own decoders are the last resort and stay here, behind the native library.
        sources = new AudioSourceFactory(http, managedCodecs: managedCodecs, soundFontPath: soundFontPath, drives: drives,
            nativeDecoderFallback: (stream, sampleRate) => NativeDecoderSource.TryOpen(stream, sampleRate, out _));
        this.useNullBackend = useNullBackend;
        positionTimer = new Timer(_ => Guarded(PublishPosition, "updating the playback position"), null, Timeout.Infinite, Timeout.Infinite);
    }

    public PlaybackSnapshot Snapshot
    {
        get
        {
            lock (stateLock) return snapshot;
        }
    }

    public event EventHandler<PlaybackSnapshot>? StateChanged;

    public Task LoadAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ReleasePipeline();
            pendingStart = TimeSpan.Zero;
            Publish(current => current with { Status = PlaybackStatus.Stopped, Track = track, Position = TimeSpan.Zero, Error = null, ErrorDetails = null });
        }
        return Task.CompletedTask;
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Track track;
        TimeSpan start;
        long mine;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            track = Snapshot.Track ?? throw new InvalidOperationException("Load a track before starting playback.");
            if (pipeline is not null)
            {
                EnsureOutput().Start();
                positionTimer.Change(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
                Publish(current => current with { Status = PlaybackStatus.Playing, Error = null, ErrorDetails = null });
                return;
            }
            mine = ++generation;
            start = pendingStart;
            pendingStart = TimeSpan.Zero;
        }

        DecodePipeline? opened = null;
        try
        {
            uint deviceRate;
            double speed;
            lock (gate)
            {
                deviceRate = EnsureOutput().SampleRate;
                speed = playbackRate;
            }
            IPcmSource source = await sources.OpenAsync(track.Location, deviceRate, cancellationToken).ConfigureAwait(false);
            opened = new DecodePipeline(source, deviceRate, (long)(start.TotalSeconds * deviceRate), speed);
            await Task.Run(() => opened.WaitUntilReady(StartTimeout, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            opened?.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            opened?.Dispose();
            string message = exception is NotSupportedException or InvalidOperationException ? exception.Message
                : $"The built-in player could not play this track: {exception.Message}";
            lock (gate)
            {
                if (mine == generation) Publish(current => current with { Status = PlaybackStatus.Faulted, Error = message, ErrorDetails = exception.ToString() });
            }
            throw new InvalidOperationException(message, exception);
        }

        lock (gate)
        {
            if (disposed || mine != generation)
            {
                opened.Dispose();
                return;
            }
            pipeline = opened;
            endHandled = 0;
            rendering = opened;
            EnsureOutput().Start();
            Publish(current => current with { Status = PlaybackStatus.Playing, Position = start, Error = null, ErrorDetails = null });
            positionTimer.Change(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (pipeline is null || Snapshot.Status != PlaybackStatus.Playing) return Task.CompletedTask;
            output?.Stop();
            positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
            TimeSpan position = pipeline.Position;
            Publish(current => PlaybackSnapshotRules.WithEnginePosition(current with { Status = PlaybackStatus.Paused }, position));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ReleasePipeline();
            pendingStart = TimeSpan.Zero;
            Publish(current => current with { Status = PlaybackStatus.Stopped, Position = TimeSpan.Zero, Error = null, ErrorDetails = null });
        }
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Snapshot.Track is not { } track) return Task.CompletedTask;
            TimeSpan clamped = position < TimeSpan.Zero ? TimeSpan.Zero
                : track.Duration > TimeSpan.Zero && position > track.Duration ? track.Duration : position;
            if (pipeline is { } active)
            {
                endHandled = 0;
                active.Seek(clamped);
            }
            else
            {
                pendingStart = clamped;
            }
            Publish(current => current with { Position = clamped });
        }
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be finite.");
        double clamped = Math.Clamp(volume, 0, 1);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Publish(current => current with { Volume = clamped });
            targetGain = Snapshot.IsMuted ? 0f : (float)clamped;
        }
        return Task.CompletedTask;
    }

    public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Publish(current => current with { IsMuted = isMuted });
            targetGain = isMuted ? 0f : (float)Snapshot.Volume;
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            positionTimer.Dispose();
            ReleasePipeline();
            output?.Dispose();
            output = null;
        }
    }

    public double Rate
    {
        get
        {
            lock (gate) return playbackRate;
        }
    }

    public Task SetRateAsync(double rate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(rate) || rate is < 0.5 or > 4)
            throw new ArgumentOutOfRangeException(nameof(rate), "Playback speed must be between 0.5x and 4x.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (playbackRate == rate) return Task.CompletedTask;
            playbackRate = rate;
            pipeline?.SetRate(rate);
        }
        return Task.CompletedTask;
    }

    public EqualizerSettings Equalizer
    {
        get
        {
            lock (gate) return equalizer;
        }
    }

    public Task SetEqualizerAsync(EqualizerSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            equalizer = settings.Normalized();
            // Before the first play there is no device, so the values wait here and are applied when it opens.
            equalizerProcessor?.Set(equalizer);
        }
        return Task.CompletedTask;
    }

    private AudioOutput EnsureOutput()
    {
        if (output is not null) return output;
        AudioOutput opened = new(Render, useNullBackend);
        EqualizerProcessor processor = new(opened.SampleRate);
        processor.Set(equalizer);
        equalizerProcessor = processor;
        return output = opened;
    }

    private void ReleasePipeline()
    {
        generation++;
        positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
        output?.Stop();
        rendering = null;
        pipeline?.Dispose();
        pipeline = null;
    }

    // Runs on the audio thread: copy decoded frames, apply the volume with a short ramp (no clicks), and report the end.
    private void Render(Span<float> buffer)
    {
        DecodePipeline? active = rendering;
        if (active is null)
        {
            buffer.Clear();
            return;
        }
        active.Render(buffer);
        equalizerProcessor?.Process(buffer);
        float from = currentGain, to = targetGain;
        if (from == 1f && to == 1f)
        {
        }
        else if (from == to)
        {
            for (int i = 0; i < buffer.Length; i++) buffer[i] *= to;
        }
        else
        {
            int frames = buffer.Length / NativeAudio.Channels;
            for (int frame = 0; frame < frames; frame++)
            {
                float gain = from + (to - from) * (frame + 1) / frames;
                buffer[frame * 2] *= gain;
                buffer[(frame * 2) + 1] *= gain;
            }
            currentGain = to;
        }
        if (active.Finished && Interlocked.Exchange(ref endHandled, 1) == 0) _ = Task.Run(() => Guarded(() => HandleEnd(active), "finishing the track"));
    }

    // Background work must never let an exception escape: on a thread-pool or timer thread that would end the process, with nothing
    // shown to the user. The failure is published as a Faulted snapshot instead, carrying the full exception for the log.
    private void Guarded(Action work, string what)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            try
            {
                lock (gate) { if (!disposed) ReleasePipeline(); }
                Publish(current => current with { Status = PlaybackStatus.Faulted, Error = $"The built-in player failed while {what}: {exception.Message}", ErrorDetails = exception.ToString() });
            }
            catch (Exception)
            {
                // Nothing more can be done from here; the original failure was the one that mattered.
            }
        }
    }

    private void HandleEnd(DecodePipeline finished)
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(pipeline, finished)) return;
            Exception? failure = finished.Failure;
            ReleasePipeline();
            if (failure is not null)
            {
                string message = $"The built-in player could not continue this track: {failure.Message}";
                Publish(current => current with { Status = PlaybackStatus.Faulted, Error = message, ErrorDetails = failure.ToString() });
            }
            else
            {
                Publish(current => current with { Status = PlaybackStatus.Ended, Position = current.Track?.Duration ?? TimeSpan.Zero });
            }
        }
    }

    private void PublishPosition()
    {
        TimeSpan position;
        lock (gate)
        {
            if (pipeline is not { } active || Snapshot.Status != PlaybackStatus.Playing) return;
            position = active.Position;
        }
        Publish(current => PlaybackSnapshotRules.WithEnginePosition(current, position));
    }

    private void Publish(Func<PlaybackSnapshot, PlaybackSnapshot> update)
    {
        PlaybackSnapshot next;
        lock (stateLock)
        {
            PlaybackSnapshot updated = update(snapshot);
            if (updated == snapshot) return;
            snapshot = next = updated;
        }
        StateChanged?.Invoke(this, next);
    }
}
