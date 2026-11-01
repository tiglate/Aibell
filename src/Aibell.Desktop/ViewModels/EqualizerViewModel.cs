using System.Collections.ObjectModel;
using System.Globalization;
using Aibell.Desktop.Localization;
using Aibell.Domain.Playback;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public interface IEqualizerHost
{
    EqualizerSettings Current { get; }

    /// <summary>Applies the settings to the engine; <paramref name="persist"/> also saves them with the preferences.</summary>
    Task ApplyAsync(EqualizerSettings settings, bool persist, CancellationToken cancellationToken = default);
}

public sealed partial class EqualizerBandViewModel : ObservableObject
{
    public EqualizerBandViewModel(string label, double gain, double maximum = EqualizerSettings.MaximumGain) { Label = label; this.gain = gain; Maximum = maximum; }

    public double Maximum { get; }

    public string Label { get; }

    [ObservableProperty]
    private double gain;
}

public sealed record EqualizerPresetChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Drives the equalizer window. Slider moves reach the engine at once; the preferences file is only written after the
/// sliders have been still for a moment, and once more when the window closes.
/// </summary>
public sealed partial class EqualizerViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PersistDelay = TimeSpan.FromMilliseconds(400);
    private readonly IEqualizerHost host;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? pendingPersist;
    private bool updating;
    private bool disposed;

    public EqualizerViewModel(IEqualizerHost host)
    {
        this.host = host;
        Preamp = new EqualizerBandViewModel(Loc.Get("Equalizer.Preamp"), 0, EqualizerSettings.MaximumPreamp);
        Bands = new ObservableCollection<EqualizerBandViewModel>(EqualizerSettings.BandFrequencies.Select(frequency =>
            new EqualizerBandViewModel(FrequencyLabel(frequency), 0)));
        Presets = new ObservableCollection<EqualizerPresetChoice>(
            [new EqualizerPresetChoice(null, Loc.Get("Equalizer.Preset.Custom")),
             .. EqualizerPresets.All.Select(preset => new EqualizerPresetChoice(preset.Id, PresetName(preset.Id)))]);
        Preamp.PropertyChanged += (_, _) => Changed();
        foreach (EqualizerBandViewModel band in Bands) band.PropertyChanged += (_, _) => Changed();
        Load(host.Current);
    }

    // 31.25 and 62.5 Hz read as the usual 31 and 63.
    private static string FrequencyLabel(double frequency) => frequency >= 1000
        ? string.Create(CultureInfo.InvariantCulture, $"{frequency / 1000:0.#}K")
        : Math.Round(frequency, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    // Literal keys, so the resource checks can see that each one is used.
    private static string PresetName(string id) => id switch
    {
        "Flat" => Loc.Get("Equalizer.Preset.Flat"),
        "BassBoost" => Loc.Get("Equalizer.Preset.BassBoost"),
        "TrebleBoost" => Loc.Get("Equalizer.Preset.TrebleBoost"),
        "Rock" => Loc.Get("Equalizer.Preset.Rock"),
        "Pop" => Loc.Get("Equalizer.Preset.Pop"),
        "Jazz" => Loc.Get("Equalizer.Preset.Jazz"),
        "Classical" => Loc.Get("Equalizer.Preset.Classical"),
        "Vocal" => Loc.Get("Equalizer.Preset.Vocal"),
        _ => id
    };

    public EqualizerBandViewModel Preamp { get; }

    public ObservableCollection<EqualizerBandViewModel> Bands { get; }

    public ObservableCollection<EqualizerPresetChoice> Presets { get; }

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private EqualizerPresetChoice? selectedPreset;

    partial void OnIsEnabledChanged(bool value) => Changed();

    partial void OnSelectedPresetChanged(EqualizerPresetChoice? value)
    {
        if (updating || value?.Id is not { } id) return;
        (string _, double preamp, double[] bands) = EqualizerPresets.All.First(preset => preset.Id == id);
        Load(host.Current with { Preamp = preamp, Bands = bands }, apply: true, keepPreset: true);
    }

    public EqualizerSettings Current => new()
    {
        IsEnabled = IsEnabled,
        Preamp = Preamp.Gain,
        Bands = Bands.Select(band => band.Gain).ToArray()
    };

    public void Reset() => Load(host.Current with { Preamp = 0, Bands = new double[EqualizerSettings.BandCount] }, apply: true);

    private void Load(EqualizerSettings settings, bool apply = false, bool keepPreset = false)
    {
        EqualizerSettings normalized = settings.Normalized();
        updating = true;
        try
        {
            IsEnabled = normalized.IsEnabled;
            Preamp.Gain = normalized.Preamp;
            for (int index = 0; index < Bands.Count; index++) Bands[index].Gain = normalized.Bands[index];
            if (!keepPreset) SelectedPreset = Presets.First(choice => choice.Id == EqualizerPresets.Match(normalized));
        }
        finally { updating = false; }
        if (apply) Changed();
    }

    private void Changed()
    {
        if (updating || disposed) return;
        EqualizerSettings settings = Current;
        string? matched = EqualizerPresets.Match(settings);
        updating = true;
        try { SelectedPreset = Presets.First(choice => choice.Id == matched); }
        finally { updating = false; }
        _ = ApplyAsync(settings);
    }

    private async Task ApplyAsync(EqualizerSettings settings)
    {
        pendingPersist?.Cancel();
        CancellationTokenSource persist = pendingPersist = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            await host.ApplyAsync(settings, persist: false, lifetime.Token);
            await Task.Delay(PersistDelay, persist.Token);
            await host.ApplyAsync(settings, persist: true, lifetime.Token);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Writes the current curve to the preferences now, e.g. when the window closes.</summary>
    public Task FlushAsync()
    {
        pendingPersist?.Cancel();
        return host.ApplyAsync(Current, persist: true);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
