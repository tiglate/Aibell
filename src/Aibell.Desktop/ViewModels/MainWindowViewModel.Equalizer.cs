using Aibell.Application.Playback;
using Aibell.Domain.Playback;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : IEqualizerHost
{
    private EqualizerSettings equalizer = EqualizerSettings.Flat;

    public bool CanUseEqualizer => playbackEngine is IEqualizerControl;

    public bool IsEqualizerOn => equalizer.IsEnabled;

    EqualizerSettings IEqualizerHost.Current => equalizer;

    async Task IEqualizerHost.ApplyAsync(EqualizerSettings settings, bool persist, CancellationToken cancellationToken)
    {
        EqualizerSettings normalized = settings.Normalized();
        equalizer = normalized;
        OnPropertyChanged(nameof(IsEqualizerOn));
        if (playbackEngine is IEqualizerControl control) await control.SetEqualizerAsync(normalized, cancellationToken);
        if (persist) await PersistPreferencesAsync(cancellationToken);
    }

    // Startup restore: the engine keeps the values until its player exists, so this never loads LibVLC by itself.
    private async Task RestoreEqualizerAsync(EqualizerSettings? saved, CancellationToken cancellationToken)
    {
        equalizer = saved?.Normalized() ?? EqualizerSettings.Flat;
        OnPropertyChanged(nameof(IsEqualizerOn));
        if (playbackEngine is IEqualizerControl control && equalizer.IsEnabled)
            await control.SetEqualizerAsync(equalizer, cancellationToken);
    }
}
