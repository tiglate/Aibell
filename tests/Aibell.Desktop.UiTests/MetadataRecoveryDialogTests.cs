using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Library;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class MetadataRecoveryDialogTests
{
    private Window owner = null!;
    private MetadataRecoveryDialog dialog = null!;

    [TearDown]
    public void TearDown() { dialog?.Close(); owner?.Close(); }

    [AvaloniaTest]
    public async Task Dialog_ChosenFolder_InspectsAndRequiresConfirmationBeforeDiscard()
    {
        string root = Path.Combine(Path.GetTempPath(), "recovery-preview");
        PendingMetadataEdit edit = Preview(root);
        int scans = 0, discards = 0;
        owner = new(); owner.Show();
        dialog = new([], (folders, _) =>
        {
            scans++; Assert.That(folders, Is.EqualTo(new[] { root }));
            return Task.FromResult(new MetadataEditRecoveryScan(discards == 0 ? [edit] : [], []));
        }, (_, _) => { discards++; return Task.CompletedTask; }, chooseFolder: _ => Task.FromResult<string?>(root));
        Task<bool> result = dialog.ShowDialog<bool>(owner); Pump();
        Assert.That(scans, Is.Zero);
        Click("ChooseMetadataRecoveryFolder");
        await UntilAsync(() => List().ItemCount == 1);
        List().SelectedIndex = 0; Click("DiscardMetadataPreview");
        await UntilAsync(() => dialog.OwnedWindows.Any());
        dialog.OwnedWindows.Single().Close(false);
        Pump(); Assert.That(discards, Is.Zero);
        Click("DiscardMetadataPreview");
        await UntilAsync(() => dialog.OwnedWindows.Any());
        dialog.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmDiscardMetadataPreview")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => List().ItemCount == 0);
        Assert.That(discards, Is.EqualTo(1));
        Assert.That(scans, Is.EqualTo(2));
        Click("CloseMetadataRecovery"); Assert.That(await result, Is.False);
    }

    [AvaloniaTest]
    public async Task Dialog_CloseDuringScan_CancelsAndDrainsBeforeClosing()
    {
        bool cancelled = false;
        owner = new(); owner.Show();
        dialog = new([Path.GetTempPath()], async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return new([], []);
        }, (_, _) => Task.CompletedTask);
        Task<bool> result = dialog.ShowDialog<bool>(owner); Pump();
        Assert.That(FindButton("ScanMetadataRecovery").IsEnabled, Is.False);
        Click("CloseMetadataRecovery");
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        Assert.That(cancelled, Is.True);
        Assert.That(owner.IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task Dialog_ConflictingDiscard_ShowsErrorAndKeepsInspectedItem()
    {
        string root = Path.GetTempPath(); PendingMetadataEdit edit = Preview(root);
        owner = new(); owner.Show();
        dialog = new([root], (_, _) => Task.FromResult(new MetadataEditRecoveryScan([edit],
            [new(new Uri(Path.Combine(root, "unknown.json")), "Unrecognized record retained.")])),
            (_, _) => Task.FromException(new IOException("The preview changed; scan again.")));
        _ = dialog.ShowDialog<bool>(owner); Pump();
        await UntilAsync(() => List().ItemCount == 1);
        dialog.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true; Pump();
        Assert.That(dialog.GetVisualDescendants().OfType<SelectableTextBlock>().Single(text => text.Name == "MetadataRecoveryIssues").Text,
            Does.Contain("Unrecognized record retained"));
        List().SelectedIndex = 0; Click("DiscardMetadataPreview");
        await UntilAsync(() => dialog.OwnedWindows.Any());
        dialog.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmDiscardMetadataPreview")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "MetadataRecoveryStatus").Text?.Contains("preview changed") == true);
        Assert.That(List().ItemCount, Is.EqualTo(1));
        Assert.That(FindButton("DiscardMetadataPreview").IsEnabled, Is.True);
    }

    private static PendingMetadataEdit Preview(string root) => new(new Uri(Path.Combine(root, "track.flac")),
        new Uri(Path.Combine(root, "preview.stage")), new Uri(Path.Combine(root, "preview.json")),
        new string('A', 64), new string('B', 64), new string('C', 64), PendingMetadataEditState.Validated);
    private ListBox List() => dialog.GetVisualDescendants().OfType<ListBox>().Single();
    private Button FindButton(string name) => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
    private void Click(string name) => FindButton(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition()) { Pump(); await Task.Delay(10, timeout.Token); }
        Pump();
    }
}
