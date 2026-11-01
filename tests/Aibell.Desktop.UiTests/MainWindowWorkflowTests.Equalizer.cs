using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using Aibell.Domain.Playback;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    [AvaloniaTest]
    public async Task Equalizer_ButtonOpensOneWindowAndSlidersReachTheEngineAndPreferences()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-eq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Open();
        await UntilAsync(() => !viewModel.IsBusy);
        Button open = window.FindControl<Button>("OpenEqualizerButton")!;
        Assert.That(open.IsEnabled, Is.True);

        Activate(open);
        Activate(open);
        EqualizerWindow eq = window.OwnedWindows.OfType<EqualizerWindow>().Single();
        eq.FindControl<ToggleButton>("EqualizerEnabled")!.IsChecked = true;
        Assert.That(open.Classes.Contains("active"), Is.True, "the EQ button lights up while the equalizer is on");
        Slider preamp = eq.FindControl<ContentControl>("PreampColumn")!.GetVisualDescendants().OfType<Slider>().Single();
        preamp.Value = -4;
        Slider bass = eq.FindControl<ItemsControl>("BandSliders")!.GetVisualDescendants().OfType<Slider>().First();
        bass.Value = 6;

        Assert.That(engine.Equalizer.IsEnabled, Is.True);
        Assert.That(engine.Equalizer.Preamp, Is.EqualTo(-4));
        Assert.That(engine.Equalizer.Bands[0], Is.EqualTo(6));
        Assert.That(eq.FindControl<ComboBox>("EqualizerPreset")!.SelectedItem?.ToString(), Is.EqualTo("Custom"));

        eq.Close();
        await UntilAsync(() => !eq.IsVisible);
        Assert.That((await settings.LoadAsync()).Equalizer!.Bands[0], Is.EqualTo(6), "closing the window saves the curve");
        await viewModel.SavePreferencesAsync(viewModel.GetPreferences() with { Volume = 0.3 });
        EqualizerSettings kept = (await settings.LoadAsync()).Equalizer!;
        Assert.That(kept.IsEnabled && kept.Preamp == -4 && kept.Bands[0] == 6, Is.True, "saving other preferences keeps the curve");
    }

    [AvaloniaTest]
    public async Task Equalizer_PresetAndReset_ApplyTheCurve()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-eq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Open();
        await UntilAsync(() => !viewModel.IsBusy);
        Activate(window.FindControl<Button>("OpenEqualizerButton")!);
        EqualizerWindow eq = window.OwnedWindows.OfType<EqualizerWindow>().Single();

        ComboBox presets = eq.FindControl<ComboBox>("EqualizerPreset")!;
        presets.SelectedItem = presets.Items.Cast<object>().Single(item => item.ToString() == "Bass boost");
        Assert.That(engine.Equalizer.Bands[0], Is.EqualTo(6));
        Assert.That(engine.Equalizer.Preamp, Is.EqualTo(-3));

        Activate(eq.FindControl<Button>("EqualizerReset")!);
        Assert.That(engine.Equalizer.Bands, Is.All.Zero);
        Assert.That(engine.Equalizer.Preamp, Is.Zero);
        Assert.That(presets.SelectedItem?.ToString(), Is.EqualTo("Flat"));
    }

    [AvaloniaTest]
    public async Task Equalizer_SavedCurveIsAppliedToTheEngineAtStartup()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-eq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await new Aibell.Infrastructure.Settings.JsonApplicationSettingsStore(Path.Combine(directory, "settings.json")).SaveAsync(
            Aibell.Application.Settings.ApplicationPreferences.Default with
            {
                Equalizer = new EqualizerSettings { IsEnabled = true, Preamp = -2, Bands = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] }
            });

        Open();
        await UntilAsync(() => !viewModel.IsBusy);

        Assert.That(engine.Equalizer.IsEnabled, Is.True);
        Assert.That(engine.Equalizer.Bands[9], Is.EqualTo(10));
    }

    [AvaloniaTest]
    public async Task Transport_VolumeSliderKeepsUsableWidthBesideTheToolbarButtons()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-eq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Open();
        await UntilAsync(() => !viewModel.IsBusy);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.That(window.FindControl<Control>("VolumeSurface")!.Bounds.Width, Is.GreaterThanOrEqualTo(90), "the volume slider keeps its room beside the transport buttons");
    }
}
