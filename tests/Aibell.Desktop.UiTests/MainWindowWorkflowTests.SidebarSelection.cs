using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    [AvaloniaTest]
    public async Task Sidebar_HighlightsTheCurrentView()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-sidebar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Open(radio: true);
        await UntilAsync(() => !viewModel.IsBusy);
        Button music = window.FindControl<Button>("SelectMusicButton")!, radio = window.FindControl<Button>("SelectRadioButton")!;
        Assert.That(music.Classes.Contains("selected"), Is.True);
        Assert.That(radio.Classes.Contains("selected"), Is.False);

        Activate(radio);
        Assert.That(radio.Classes.Contains("selected"), Is.True);
        Assert.That(music.Classes.Contains("selected"), Is.False);
        Assert.That(radio.Tag, Is.Not.Null, "each entry carries its vector icon");
    }
}
