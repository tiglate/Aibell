using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class AboutWindowTests
{
    private static readonly AboutInfo Info = new("Aibell", "9.8.7", "Some Author", "Copyright (c) 2026 Some Author.", "MIT",
    [
        new AboutSection("Playback",
        [
            new AboutRow("Player in use", "Built-in player", AboutState.Available),
            new AboutRow("VLC (LibVLC)", "Not found", AboutState.Unavailable)
        ])
    ]);

    // Dialogs here are built in code, so their names are not in a name scope; find them in the visual tree like the other dialog tests.
    internal static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void ShowTab(AboutWindow window, int index)
    {
        Named<TabControl>(window, "AboutTabs").SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    private static AboutWindow Open()
    {
        AboutWindow window = new(Info);
        window.Show();
        return window;
    }

    [AvaloniaTest]
    public void Open_ShowsTheNameVersionAuthorAndLicense()
    {
        AboutWindow window = Open();

        Assert.That(Named<TextBlock>(window, "AboutName").Text, Is.EqualTo("Aibell"));
        Assert.That(Named<TextBlock>(window, "AboutVersion").Text, Is.EqualTo("Version 9.8.7"));
        Assert.That(Named<TextBlock>(window, "AboutAuthor").Text, Is.EqualTo("Some Author"));
        Assert.That(Named<TextBlock>(window, "AboutLicense").Text, Is.EqualTo("MIT"));
        Assert.That(Named<TextBlock>(window, "AboutCopyright").Text, Is.EqualTo("Copyright (c) 2026 Some Author."));
        Assert.That(Named<Image>(window, "AboutLogo").Source, Is.Not.Null, "the logo ships as an asset");
        window.Close();
    }

    [AvaloniaTest]
    public void Open_ListsTheFeaturesItWasGiven()
    {
        AboutWindow window = Open();

        ShowTab(window, 1);
        string text = string.Join('\n', Named<ScrollViewer>(window, "AboutFeatures").GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));

        Assert.That(text, Does.Contain("Player in use").And.Contain("Built-in player").And.Contain("Not found"));
        window.Close();
    }

    [AvaloniaTest]
    public void Open_AcknowledgesSongbirdAndBanshee()
    {
        AboutWindow window = Open();
        ShowTab(window, 2);

        Assert.That(Named<TextBlock>(window, "CreditSongbird").Text, Does.Contain("Songbird").And.Contain("iTunes"));
        Assert.That(Named<TextBlock>(window, "CreditBanshee").Text, Does.Contain("Banshee").And.Contain("no Banshee source code"));
        window.Close();
    }

    [AvaloniaTest]
    public void Escape_ClosesTheWindow()
    {
        AboutWindow window = Open();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.That(window.IsVisible, Is.False);
    }
}

[TestFixture]
public sealed class ShortcutsWindowTests
{
    private static readonly string[] Expected = ["Ctrl", "O", "N", "Shift", "F", "Q", ",", "Alt", "Enter", "Space", "Esc", "Delete", "Page Up", "Home"];

    [AvaloniaTest]
    public void Catalog_ListsEveryShortcutTheApplicationHandles()
    {
        string[] keys = [.. ShortcutCatalog.Create().SelectMany(section => section.Entries).SelectMany(entry => entry.Alternatives).SelectMany(keys => keys)];

        Assert.That(keys, Is.SupersetOf(Expected));
        Assert.That(ShortcutCatalog.Create().All(section => section.Entries.Count > 0 && section.Entries.All(entry => entry.Description.Length > 0 && !entry.Description.StartsWith('!'))), Is.True);
    }

    [AvaloniaTest]
    public void Open_ShowsEachSectionWithItsKeyCaps()
    {
        ShortcutsWindow window = new(ShortcutCatalog.Create());
        window.Show();

        string[] text = [.. AboutWindowTests.Named<StackPanel>(window, "ShortcutList").GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "")];

        Assert.That(text, Does.Contain("Ctrl").And.Contain("Quit").And.Contain("Seek bar (when focused)"));
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.That(window.IsVisible, Is.False);
    }
}
