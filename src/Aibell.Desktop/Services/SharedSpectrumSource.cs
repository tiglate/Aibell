using Aibell.Application.Playback;

namespace Aibell.Desktop.Services;

/// <summary>
/// Lets several visualizers use one engine's spectrum: the analysis runs while at least one of them has switched it on, so one
/// visualizer switching off (a hidden window) never starves another.
/// </summary>
public sealed class SharedSpectrumSource(ISpectrumSource inner) : ISpectrumSource
{
    private int users;

    public ISpectrumSource Inner => inner;

    public bool IsSpectrumAvailable => inner.IsSpectrumAvailable;

    public void SetSpectrumEnabled(bool enabled)
    {
        int before = users;
        users = Math.Max(0, users + (enabled ? 1 : -1));
        if ((before == 0) != (users == 0)) inner.SetSpectrumEnabled(users > 0);
    }

    public bool TryReadSpectrum(Span<float> bands) => inner.TryReadSpectrum(bands);
}
