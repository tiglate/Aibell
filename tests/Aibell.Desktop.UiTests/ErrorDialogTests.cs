using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using Aibell.Desktop.Services;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class ErrorDialogTests
{
    private static ErrorReport Report(string message, string details) => new("Something went wrong", message, details, "playback");

    [AvaloniaTest]
    public void Details_AreHiddenUntilTheButtonIsPressedAndCanBeCopiedThenHidden()
    {
        ErrorDialog dialog = new(Report("The track could not be played.", "Track: /a.ogg\nstack trace"));
        dialog.Show();
        Button details = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ErrorDetailsButton");
        Button copy = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ErrorCopyButton");

        Assert.That(dialog.DetailsVisible, Is.False);
        Assert.That(copy.IsVisible, Is.False);
        Assert.That(details.Content, Is.EqualTo("Details"));

        details.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.That(dialog.DetailsVisible, Is.True);
        Assert.That(copy.IsVisible, Is.True);
        Assert.That(details.Content, Is.EqualTo("Hide details"));
        Assert.That(dialog.GetVisualDescendants().OfType<TextBox>().Single().Text, Is.EqualTo("Track: /a.ogg\nstack trace"));
        Assert.That(dialog.Height, Is.GreaterThan(400), "the window grows to show the text");
        dialog.Close();
    }

    [AvaloniaTest]
    public void Append_AddsToTheSameWindowWithAGrowingCount()
    {
        ErrorDialog dialog = new(Report("First failure.", "details one"));
        dialog.Show();

        dialog.Append(Report("Second failure.", "details two"));
        dialog.Append(Report("Third failure.", "details three"));

        TextBlock message = dialog.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text?.Contains("failure", StringComparison.Ordinal) == true);
        Assert.That(message.Text, Does.Contain("Third failure.").And.Contain("2 more problems"));
        Assert.That(dialog.DetailText, Does.Contain("details one").And.Contain("details two").And.Contain("details three"));
        dialog.Close();
    }

    [AvaloniaTest]
    public void Escape_ClosesTheWindow()
    {
        ErrorDialog dialog = new(Report("Message", "details"));
        dialog.Show();

        dialog.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);

        Assert.That(dialog.IsVisible, Is.False);
    }
}
