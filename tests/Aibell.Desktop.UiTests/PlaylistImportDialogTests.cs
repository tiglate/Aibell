using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Playlists;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class PlaylistImportDialogTests
{
    private Window owner = null!;
    private PlaylistImportDialog dialog = null!;
    private static PlaylistImportPreview Preview()
    {
        Uri source = new("file:///playlists/review.m3u8");
        Track track = new(Guid.NewGuid(), new Uri("file:///music/tone.flac"), "Tone", "Artist", "Album", TimeSpan.FromSeconds(1));
        return new(source, "Review", [track], [new(2, "missing.flac", "The media file does not exist.")], 1);
    }
    [TearDown] public void TearDown() { dialog?.Close(); owner?.Close(); }

    [AvaloniaTest]
    public async Task Dialog_PreparedReview_TrimsNameAndImportsOnlyAfterConfirmation()
    {
        string? saved = null;
        Task<bool> result = Open(_ => Task.FromResult(Preview()), (name, _, _) => { saved = name; return Task.CompletedTask; });
        await UntilAsync(() => Accept().IsEnabled);
        Assert.That(saved, Is.Null);
        Name().Text = "   "; Dispatcher.UIThread.RunJobs(); Assert.That(Accept().IsEnabled, Is.False);
        Name().Text = "  Edited  "; Dispatcher.UIThread.RunJobs(); Assert.That(Accept().IsEnabled, Is.True);
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(saved, Is.EqualTo("Edited"));
    }

    [AvaloniaTest]
    public async Task Dialog_CancelDuringPreparation_CancelsAndNeverImports()
    {
        bool cancelled = false; int imports = 0;
        Task<bool> result = Open(async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return Preview();
        }, (_, _, _) => { imports++; return Task.CompletedTask; });
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        Assert.That(cancelled, Is.True); Assert.That(imports, Is.Zero);
    }

    [AvaloniaTest]
    public async Task Dialog_SaveFailure_RetainsReviewAndInputForRetry()
    {
        _ = Open(_ => Task.FromResult(Preview()), (_, _, _) => Task.FromException(new IOException("Duplicate playlist name")));
        await UntilAsync(() => Accept().IsEnabled);
        Name().Text = "Keep edited name";
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
        Assert.That(dialog.IsVisible, Is.True); Assert.That(Name().Text, Is.EqualTo("Keep edited name"));
        Assert.That(Accept().IsEnabled, Is.True);
        Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PlaylistImportStatus").Text, Does.Contain("Duplicate"));
    }

    [AvaloniaTest]
    public async Task Dialog_EmptyReviewAndBothThemes_PreservesMinimumLayoutAndDisablesImport()
    {
        PlaylistImportPreview empty = Preview() with { Tracks = [] };
        _ = Open(_ => Task.FromResult(empty), (_, _, _) => Task.CompletedTask);
        await UntilAsync(() => Name().IsEnabled);
        dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Avalonia.Application.Current!.RequestedThemeVariant = theme; Dispatcher.UIThread.RunJobs();
            Assert.That(Name().VerticalContentAlignment, Is.EqualTo(VerticalAlignment.Center));
            Assert.That(Accept().IsEnabled, Is.False);
            Assert.That(Accept().TranslatePoint(default, dialog)!.Value.Y + Accept().Bounds.Height, Is.LessThanOrEqualTo(dialog.Bounds.Height));
        }
    }

    private Task<bool> Open(Func<CancellationToken, Task<PlaylistImportPreview>> prepare, Func<string, PlaylistImportPreview, CancellationToken, Task> import)
    {
        owner = new(); owner.Show(); dialog = new(Preview().Source, prepare, import);
        return dialog.ShowDialog<bool>(owner);
    }
    private TextBox Name() => dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "ImportedPlaylistName");
    private Button Accept() => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmPlaylistImport");
    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, deadline.Token); }
        Dispatcher.UIThread.RunJobs();
    }
}
