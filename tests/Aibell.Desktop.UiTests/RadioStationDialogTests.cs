using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Domain.Radio;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class RadioStationDialogTests
{
    private Window owner = null!;
    private RadioStationDialog dialog = null!;
    [TearDown] public void TearDown() { dialog?.Close(); owner?.Close(); }
    private static RadioStation Station() => new(Guid.NewGuid(), "Synthetic Radio", new Uri("https://radio.example.invalid/live"));

    [AvaloniaTest]
    public async Task Dialog_InvalidAddressesAndBothThemes_KeepSaveDisabledAndFieldsCentered()
    {
        _ = Open(null, _ => Task.CompletedTask);
        Input("RadioStationName").Text = "Station";
        foreach (string invalid in new[] { "file:///tmp/song.mp3", "https://user:secret@example.invalid/live", "https://example.invalid/list.m3u", "not an address" })
        {
            Input("RadioStationUrl").Text = invalid; Dispatcher.UIThread.RunJobs();
            Assert.That(Accept().IsEnabled, Is.False);
        }
        Input("RadioStationUrl").Text = "https://example.invalid/live"; Dispatcher.UIThread.RunJobs();
        dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Avalonia.Application.Current!.RequestedThemeVariant = theme; Dispatcher.UIThread.RunJobs();
            Assert.That(Accept().IsEnabled, Is.True);
            Assert.That(dialog.GetVisualDescendants().OfType<TextBox>().All(input => input.VerticalContentAlignment == VerticalAlignment.Center), Is.True);
            Assert.That(dialog.GetVisualDescendants().OfType<ComboBox>().Single().VerticalContentAlignment, Is.EqualTo(VerticalAlignment.Center));
            Assert.That(Accept().TranslatePoint(default, dialog)!.Value.Y + Accept().Bounds.Height, Is.LessThanOrEqualTo(dialog.Bounds.Height));
        }
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); await Task.CompletedTask;
    }

    [AvaloniaTest]
    public async Task Dialog_EditingACatalogStation_KeepsItsCountryLogoAndCatalogIdentity()
    {
        RadioStation original = Station() with { Country = "Germany", CountryCode = "DE", LogoUri = new Uri("https://radio.example.invalid/logo.png"), CatalogId = "de-synthetic" };
        RadioStation? saved = null;
        Task<bool> result = Open(original, station => { saved = station; return Task.CompletedTask; });
        Input("RadioStationName").Text = "Renamed";
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(saved, Is.EqualTo(original with { Name = "Renamed" }));
    }

    [AvaloniaTest]
    public async Task Dialog_SaveFailure_RetainsEditsForRetryAndPreservesIdentity()
    {
        RadioStation original = Station(); RadioStation? saved = null; int attempts = 0;
        Task<bool> result = Open(original, station =>
        {
            if (++attempts == 1) throw new IOException("Duplicate stream URL");
            saved = station; return Task.CompletedTask;
        });
        Input("RadioStationName").Text = "  Edited  ";
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
        Assert.That(dialog.IsVisible, Is.True); Assert.That(Input("RadioStationName").Text, Is.EqualTo("  Edited  "));
        Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RadioStationStatus").Text, Does.Contain("Duplicate"));
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(saved!.Id, Is.EqualTo(original.Id)); Assert.That(saved.Name, Is.EqualTo("Edited"));
    }

    [AvaloniaTest]
    public async Task Dialog_CloseDuringSave_WaitsForTheStoreAndSavesOnlyOnce()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously); int saves = 0;
        Task<bool> result = Open(Station(), async _ => { saves++; await release.Task; });
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); dialog.Close();
        try { Assert.That(dialog.IsVisible, Is.True); Assert.That(saves, Is.EqualTo(1)); }
        finally { release.TrySetResult(); }
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
    }

    private Task<bool> Open(RadioStation? station, Func<RadioStation, Task> save)
    { owner = new(); owner.Show(); dialog = new(station, save); return dialog.ShowDialog<bool>(owner); }
    private TextBox Input(string name) => dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == name);
    private Button Accept() => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveRadioStation");
}
