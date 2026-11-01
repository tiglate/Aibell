using Aibell.Domain.Playback;

namespace Aibell.Application.Playback;

/// <summary>Optional engine capability: a ten-band equalizer applied to the current and following inputs.</summary>
public interface IEqualizerControl
{
    EqualizerSettings Equalizer { get; }

    Task SetEqualizerAsync(EqualizerSettings settings, CancellationToken cancellationToken = default);
}
