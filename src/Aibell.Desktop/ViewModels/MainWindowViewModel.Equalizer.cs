using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : IEqualizerHost
{
    private EqualizerSettings equalizer = EqualizerSettings.Flat;
    private double balance;
    private bool equalizerAuto;
    // The curve in place when the current track started (after AUTO had its say); a curve only belongs to a track once it differs.
    private EqualizerSettings autoBaseline = EqualizerSettings.Flat;
    private Dictionary<string, EqualizerSettings> trackCurves = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public event EventHandler? EqualizerChanged;

    public bool CanUseEqualizer => playbackEngine is IEqualizerControl;

    public bool IsEqualizerOn => equalizer.IsEnabled;

    EqualizerSettings IEqualizerHost.Current => equalizer;

    /// <summary>The left/right balance, -1 to 1; the built-in player applies it, VLC and MPD play centred.</summary>
    public double Balance => balance;

    public bool CanUseBalance => playbackEngine is IBalanceControl;

    /// <summary>AUTO: each track gets the curve last set while it played, or else the preset suiting its genre.</summary>
    public bool IsEqualizerAuto => equalizerAuto;

    async Task IEqualizerHost.ApplyAsync(EqualizerSettings settings, bool persist, CancellationToken cancellationToken)
    {
        EqualizerSettings normalized = settings.Normalized();
        equalizer = normalized;
        OnPropertyChanged(nameof(IsEqualizerOn));
        if (playbackEngine is IEqualizerControl control) await control.SetEqualizerAsync(normalized, cancellationToken);
        // A curve the user settles on while AUTO is on belongs to the track playing.
        // Saving the curve as it stands (the window closing, the on/off switch) must not pin it to the track.
        if (persist && equalizerAuto && !SameCurve(normalized, autoBaseline) && AutoKey(playbackEngine.Snapshot.Track) is { } key)
        {
            Remember(key, normalized);
            autoBaseline = normalized;
        }
        if (persist) await PersistPreferencesAsync(cancellationToken);
    }

    public async Task SetBalanceAsync(double value, bool persist, CancellationToken cancellationToken = default)
    {
        value = double.IsFinite(value) ? Math.Round(Math.Clamp(value, -1, 1), 2) : 0;
        // The middle is easy to hit, as on the classic players.
        if (Math.Abs(value) < 0.05) value = 0;
        balance = value;
        OnPropertyChanged(nameof(Balance));
        if (playbackEngine is IBalanceControl control) await control.SetBalanceAsync(value, cancellationToken);
        if (persist) await PersistPreferencesAsync(cancellationToken);
    }

    public async Task SetEqualizerAutoAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        equalizerAuto = enabled;
        autoBaseline = equalizer;
        OnPropertyChanged(nameof(IsEqualizerAuto));
        EqualizerChanged?.Invoke(this, EventArgs.Empty);
        if (enabled) await ApplyAutoEqualizerAsync(playbackEngine.Snapshot.Track, cancellationToken);
        await PersistPreferencesAsync(cancellationToken);
    }

    /// <summary>The curve AUTO picks for <paramref name="track"/>: its own, its genre's preset, or null to keep the current one.</summary>
    internal EqualizerSettings? AutoCurve(Track? track)
    {
        if (AutoKey(track) is not { } key) return null;
        if (trackCurves.TryGetValue(key, out EqualizerSettings? remembered)) return remembered with { IsEnabled = equalizer.IsEnabled };
        if (EqualizerPresets.ForGenre(track!.Genre) is not { } id) return null;
        (string _, double preamp, double[] bands) = EqualizerPresets.All.First(preset => preset.Id == id);
        return equalizer with { Preamp = preamp, Bands = bands };
    }

    // Runs as a track starts: with AUTO on, its curve replaces the current one (the on/off switch stays as the user left it).
    private async Task ApplyAutoEqualizerAsync(Track? track, CancellationToken cancellationToken)
    {
        if (!equalizerAuto) return;
        if (AutoCurve(track) is not { } curve || SameCurve(curve, equalizer))
        {
            autoBaseline = equalizer;
            return;
        }
        equalizer = curve.Normalized();
        autoBaseline = equalizer;
        OnPropertyChanged(nameof(IsEqualizerOn));
        if (playbackEngine is IEqualizerControl control) await control.SetEqualizerAsync(equalizer, cancellationToken);
        EqualizerChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameCurve(EqualizerSettings left, EqualizerSettings right) =>
        left.Preamp == right.Preamp && left.Bands.SequenceEqual(right.Bands);

    // Local files only: radio streams and MPD songs have no lasting identity worth a curve.
    private static string? AutoKey(Track? track) => track?.Location is { IsAbsoluteUri: true, IsFile: true } location ? location.AbsoluteUri : null;

    private void Remember(string key, EqualizerSettings curve)
    {
        trackCurves.Remove(key);
        trackCurves[key] = curve;
        while (trackCurves.Count > Aibell.Application.Settings.ApplicationPreferences.MaximumEqualizerTrackCurves) trackCurves.Remove(trackCurves.Keys.First());
    }

    // Startup restore: the engine keeps the values until its player exists, so this never loads LibVLC by itself.
    private async Task RestoreEqualizerAsync(Aibell.Application.Settings.ApplicationPreferences preferences, CancellationToken cancellationToken)
    {
        equalizer = preferences.Equalizer?.Normalized() ?? EqualizerSettings.Flat;
        equalizerAuto = preferences.EqualizerAuto;
        trackCurves = new Dictionary<string, EqualizerSettings>(preferences.EqualizerTrackCurves ?? new Dictionary<string, EqualizerSettings>(), StringComparer.Ordinal);
        balance = double.IsFinite(preferences.Balance) ? Math.Clamp(preferences.Balance, -1, 1) : 0;
        OnPropertyChanged(nameof(IsEqualizerOn));
        OnPropertyChanged(nameof(Balance));
        OnPropertyChanged(nameof(IsEqualizerAuto));
        if (playbackEngine is IEqualizerControl control && equalizer.IsEnabled)
            await control.SetEqualizerAsync(equalizer, cancellationToken);
        if (playbackEngine is IBalanceControl balanceControl && balance != 0)
            await balanceControl.SetBalanceAsync(balance, cancellationToken);
    }
}
