using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Radio;
using Aibell.Domain.Radio;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class RadioPlaylistImportDialogTests
{
    private Window owner = null!;
    private RadioPlaylistImportDialog dialog = null!;
    private static RadioPlaylistImportPreview Preview() => new(new Uri("file:///playlists/stations.m3u8"),
        [new RadioStation(Guid.NewGuid(), "Synthetic Radio", new Uri("https://radio.example.invalid/live"))],
        [new RadioPlaylistIssue(2, "Use a direct stream URL.")], 1);

    [TearDown]
    public void TearDown() { dialog?.Close(); owner?.Close(); }

    [AvaloniaTest]
    public async Task Dialog_Review_ImportsOnlyAfterExplicitConfirmation()
    {
        RadioPlaylistImportPreview? saved = null;
        Task<bool> result = Open(_ => Task.FromResult(Preview()), (preview, _) => { saved = preview; return Task.CompletedTask; });
        await UntilAsync(() => Accept().IsEnabled);
        Assert.That(saved, Is.Null);
        Assert.That(Status("RadioImportSummary"), Does.Contain("1 station ready; 1 skipped; 1 duplicate"));
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(saved!.Stations.Single().Name, Is.EqualTo("Synthetic Radio"));
    }

    [AvaloniaTest]
    public async Task Dialog_CancelDuringPreparation_CancelsReaderAndNeverImports()
    {
        int imports = 0;
        bool cancelled = false;
        Task<bool> result = Open(async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return Preview();
        }, (_, _) => { imports++; return Task.CompletedTask; });
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        Assert.Multiple(() => { Assert.That(cancelled, Is.True); Assert.That(imports, Is.Zero); });
    }

    [AvaloniaTest]
    public async Task Dialog_FailedImport_PreservesReviewAndAllowsRetry()
    {
        int attempts = 0;
        Task<bool> result = Open(_ => Task.FromResult(Preview()), (_, _) =>
            ++attempts == 1 ? Task.FromException(new IOException("Storage unavailable")) : Task.CompletedTask);
        await UntilAsync(() => Accept().IsEnabled);
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(Accept().IsEnabled, Is.True);
            Assert.That(Status("RadioImportStatus"), Does.Contain("Storage unavailable"));
        });
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(attempts, Is.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task Dialog_CloseDuringImport_DrainsOneSaveBeforeClosing()
    {
        int imports = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> result = Open(_ => Task.FromResult(Preview()), async (_, _) => { imports++; await release.Task; });
        await UntilAsync(() => Accept().IsEnabled);
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        dialog.Close();
        try { Assert.That(dialog.IsVisible, Is.True); Assert.That(imports, Is.EqualTo(1)); }
        finally { release.TrySetResult(); }
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
    }

    [AvaloniaTest]
    public async Task Dialog_EmptyPreviewAndBothThemes_KeepImportDisabledAtMinimumSize()
    {
        _ = Open(_ => Task.FromResult(Preview() with { Stations = [] }), (_, _) => Task.CompletedTask);
        await UntilAsync(() => Status("RadioImportStatus").Contains("No new", StringComparison.Ordinal));
        dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Avalonia.Application.Current!.RequestedThemeVariant = theme;
            Dispatcher.UIThread.RunJobs();
            Assert.That(Accept().IsEnabled, Is.False);
            Point position = Accept().TranslatePoint(default, dialog)!.Value;
            Assert.That(position.Y + Accept().Bounds.Height, Is.LessThanOrEqualTo(dialog.Bounds.Height));
            Assert.That(position.X + Accept().Bounds.Width, Is.LessThanOrEqualTo(dialog.Bounds.Width));
        }
    }

    [AvaloniaTest]
    public async Task Dialog_ReaderFailure_ReportsErrorAndKeepsImportDisabled()
    {
        _ = Open(_ => Task.FromException<RadioPlaylistImportPreview>(new InvalidDataException("Invalid station playlist")), (_, _) => Task.CompletedTask);
        await UntilAsync(() => Status("RadioImportStatus").Contains("Invalid station playlist", StringComparison.Ordinal));
        Assert.That(Accept().IsEnabled, Is.False);
    }

    private Task<bool> Open(Func<CancellationToken, Task<RadioPlaylistImportPreview>> prepare,
        Func<RadioPlaylistImportPreview, CancellationToken, Task> import)
    {
        owner = new(); owner.Show();
        dialog = new(Preview().Source, prepare, import);
        return dialog.ShowDialog<bool>(owner);
    }
    private Button Accept() => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmRadioImport");
    private string Status(string name) => dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == name).Text ?? "";
    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, deadline.Token); }
        Dispatcher.UIThread.RunJobs();
    }
}
