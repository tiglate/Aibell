using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using Aibell.Application.Settings;
using Aibell.Desktop.Services;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    private async Task<(ApplicationErrorReporter Reporter, string[] Files)> OpenWithErrorReportingAsync(params string[] fixtures)
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-errors-{Guid.NewGuid():N}");
        string media = Path.Combine(directory, "music");
        Directory.CreateDirectory(media);
        foreach (string name in fixtures) File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", name), Path.Combine(media, name));
        Open();
        ApplicationErrorReporter reporter = new(Path.Combine(directory, "logs"));
        viewModel.ConfigureErrorReporting(reporter, new SessionMonitor(Path.Combine(directory, "logs")));
        reporter.Reported += viewModel.ShowUnexpectedError;
        await UntilAsync(() => !viewModel.IsBusy);
        await viewModel.ImportAsync(media);
        await UntilAsync(() => viewModel.Tracks.Count == fixtures.Length);
        return (reporter, [.. fixtures.Select(name => Path.Combine(media, name))]);
    }

    [AvaloniaTest]
    public async Task MainWindow_PlaybackFailure_OpensTheErrorWindowAndDetailsShowTheTechnicalText()
    {
        (ApplicationErrorReporter reporter, string[] files) = await OpenWithErrorReportingAsync("fixture.mp3");
        viewModel.ConfigurePlaybackEngine(PlaybackEngineKind.PipeWire);
        window.FindControl<ListBox>("TrackList")!.SelectedIndex = 0;
        engine.FailAt = "Play";

        await viewModel.TogglePlaybackAsync();
        await UntilAsync(() => window.OwnedWindows.OfType<ErrorDialog>().Any());

        ErrorDialog dialog = window.OwnedWindows.OfType<ErrorDialog>().Single();
        Assert.That(dialog.DetailsVisible, Is.False, "the technical text starts hidden");
        Button details = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ErrorDetailsButton");
        Activate(details, dialog);
        Assert.That(dialog.DetailsVisible, Is.True);
        string shown = dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ErrorDetailsText").Text!;
        Assert.That(shown, Does.Contain("Play failed").And.Contain(files[0]).And.Contain("Player: PipeWire (Managed)").And.Contain("Format: mp3").And.Contain(reporter.LogPath));
        Assert.That(File.ReadAllText(reporter.LogPath), Does.Contain("[playback]").And.Contain(files[0]).And.Contain("Play failed"));
        Assert.That(viewModel.StatusMessage, Does.Contain("Play failed"));
        Activate(details, dialog);
        Assert.That(dialog.DetailsVisible, Is.False, "Details toggles");
    }

    [AvaloniaTest]
    public async Task MainWindow_SeveralFailuresInARow_ShareOneErrorWindow()
    {
        (_, string[] files) = await OpenWithErrorReportingAsync("fixture.mp3", "fixture.wav");
        ListBox list = window.FindControl<ListBox>("TrackList")!;
        engine.FailAt = "Play";

        list.SelectedIndex = 0;
        await viewModel.TogglePlaybackAsync();
        list.SelectedIndex = 1;
        await viewModel.TogglePlaybackAsync();
        await UntilAsync(() => window.OwnedWindows.OfType<ErrorDialog>().Any());
        await UntilAsync(() => window.OwnedWindows.OfType<ErrorDialog>().Single().DetailText.Contains(files[1], StringComparison.Ordinal));

        ErrorDialog dialog = window.OwnedWindows.OfType<ErrorDialog>().Single();
        Assert.That(dialog.DetailText, Does.Contain(files[0]).And.Contain(files[1]));
    }

    [AvaloniaTest]
    public async Task MainWindow_PreviousSessionCrash_IsReportedWithWhatWasPlaying()
    {
        (ApplicationErrorReporter reporter, _) = await OpenWithErrorReportingAsync("fixture.mp3");

        viewModel.ReportPreviousSession(new PreviousSessionCrash(DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow.AddMinutes(-2),
            new SessionActivity("playing", "/music/Ghost - Jigolo Har Megiddo.ogg", "Miniaudio")));
        await UntilAsync(() => window.OwnedWindows.OfType<ErrorDialog>().Any());

        ErrorDialog dialog = window.OwnedWindows.OfType<ErrorDialog>().Single();
        Assert.That(dialog.Title, Is.EqualTo("Aibell closed unexpectedly"));
        Assert.That(dialog.DetailText, Does.Contain("/music/Ghost - Jigolo Har Megiddo.ogg").And.Contain("Miniaudio").And.Contain("native code"));
        Assert.That(File.ReadAllText(reporter.LogPath), Does.Contain("[previous-session]"));
    }

    private static void Activate(Button button, Window owner)
    {
        button.Focus();
        owner.KeyPressQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        owner.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
