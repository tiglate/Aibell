namespace Aibell.Application.Playback;

/// <summary>Optional engine capability: a live frequency spectrum of the sound being played, for the visualizer.</summary>
public interface ISpectrumSource
{
    /// <summary>Whether this engine can report a spectrum right now (false for engines that never see the samples, or while a remote server plays).</summary>
    bool IsSpectrumAvailable { get; }

    /// <summary>Turns the analysis on or off. While off the audio thread does no extra work at all.</summary>
    void SetSpectrumEnabled(bool enabled);

    /// <summary>
    /// Fills <paramref name="bands"/> with the level of each log-spaced band, 0 (silence) to 1 (full scale), lowest frequency first.
    /// Returns false, leaving the bands zero, when nothing is playing or the analysis is off. Call from one thread at a time.
    /// </summary>
    bool TryReadSpectrum(Span<float> bands);
}
