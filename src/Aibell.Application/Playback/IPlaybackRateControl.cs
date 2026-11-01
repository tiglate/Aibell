namespace Aibell.Application.Playback;

/// <summary>Optional engine capability: playback speed of the current and following inputs (1.0 is normal speed).</summary>
public interface IPlaybackRateControl
{
    double Rate { get; }

    Task SetRateAsync(double rate, CancellationToken cancellationToken = default);
}
