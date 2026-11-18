using Aibell.Application.Playback;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool visualizerEnabled = true;

    /// <summary>Whether the visualizer is switched on in Preferences (it is on by default).</summary>
    public bool VisualizerEnabled => visualizerEnabled;

    private Services.SharedSpectrumSource? sharedSpectrum;

    /// <summary>
    /// The engine's spectrum feed, or null when the engine cannot provide one (the VLC player). Every visualizer gets the same
    /// shared feed, so the analysis keeps running while any of them is showing.
    /// </summary>
    public ISpectrumSource? SpectrumSource => playbackEngine is ISpectrumSource source
        ? sharedSpectrum is { } shared && ReferenceEquals(shared.Inner, source) ? shared : sharedSpectrum = new Services.SharedSpectrumSource(source)
        : null;

    /// <summary>Whether the progress bar's background should be drawing the visualizer.</summary>
    public bool ShowVisualizer => visualizerEnabled && SpectrumSource is not null;

    private void ApplyVisualizerEnabled(bool enabled)
    {
        visualizerEnabled = enabled;
        OnPropertyChanged(nameof(VisualizerEnabled));
        OnPropertyChanged(nameof(ShowVisualizer));
    }
}
