using Aibell.Application.Playback;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool visualizerEnabled = true;

    /// <summary>Whether the visualizer is switched on in Preferences (it is on by default).</summary>
    public bool VisualizerEnabled => visualizerEnabled;

    /// <summary>The engine's spectrum feed, or null when the engine cannot provide one (the VLC player).</summary>
    public ISpectrumSource? SpectrumSource => playbackEngine as ISpectrumSource;

    /// <summary>Whether the progress bar's background should be drawing the visualizer.</summary>
    public bool ShowVisualizer => visualizerEnabled && SpectrumSource is not null;

    private void ApplyVisualizerEnabled(bool enabled)
    {
        visualizerEnabled = enabled;
        OnPropertyChanged(nameof(VisualizerEnabled));
        OnPropertyChanged(nameof(ShowVisualizer));
    }
}
