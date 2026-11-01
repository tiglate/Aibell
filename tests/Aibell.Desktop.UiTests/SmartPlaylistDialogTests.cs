using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Domain.Playlists;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class SmartPlaylistDialogTests
{
    private Window owner = null!;
    private SmartPlaylistDialog dialog = null!;

    [TearDown]
    public void TearDown()
    {
        dialog?.Close();
        owner?.Close();
    }

    [AvaloniaTest]
    public async Task CreateDialog_KeyboardEntryAndEnter_ReturnsTrimmedNameAndRules()
    {
        Task<SmartPlaylistEdit?> result = Open();
        TextBox name = Named<TextBox>("SmartPlaylistName");
        Assert.That(name.IsFocused, Is.True);
        dialog.KeyTextInput("  Favorites  ");
        Pump();
        Assert.That(Named<Button>("SaveSmartPlaylist").IsEnabled, Is.True);

        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        SmartPlaylistEdit? edit = await result.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(edit, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(edit!.Name, Is.EqualTo("Favorites"));
            Assert.That(edit.Definition.Match, Is.EqualTo(SmartPlaylistMatch.All));
            Assert.That(edit.Definition.Rules, Is.EqualTo(new[]
            {
                new SmartPlaylistRule(SmartPlaylistField.Rating, SmartPlaylistOperator.GreaterOrEqual, "4")
            }));
        });
    }

    [AvaloniaTest]
    public async Task CreateDialog_InvalidRating_BlocksEnterUntilCorrected()
    {
        Task<SmartPlaylistEdit?> result = Open("Favorites");
        TextBox value = Rows().Single().Children.OfType<TextBox>().Single();
        value.Text = "6";
        Pump();
        Assert.That(Named<Button>("SaveSmartPlaylist").IsEnabled, Is.False);
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.That(result.IsCompleted, Is.False);

        value.Text = "5";
        Pump();
        Click(Named<Button>("SaveSmartPlaylist"));

        Assert.That((await result.WaitAsync(TimeSpan.FromSeconds(5)))!.Definition.Rules.Single().Value, Is.EqualTo("5"));
    }

    [AvaloniaTest]
    public async Task EditDialog_CancelAndEscape_ReturnNoChanges()
    {
        SmartPlaylistDefinition definition = new(SmartPlaylistMatch.Any,
            [new(SmartPlaylistField.Genre, SmartPlaylistOperator.Equals, "Rock")]);
        Task<SmartPlaylistEdit?> result = Open("Rock", definition);
        Assert.That(Named<TextBox>("SmartPlaylistName").IsReadOnly, Is.True);
        Rows().Single().Children.OfType<TextBox>().Single().Text = "Jazz";
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.Null);
        Assert.That(definition.Rules.Single().Value, Is.EqualTo("Rock"));

        result = Open("Rock", definition);
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel")));
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.Null);

        result = Open("Rock", definition);
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(button =>
            AutomationProperties.GetName(button) == "Close smart playlist editor"));
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.Null);
    }

    [AvaloniaTest]
    public async Task RuleField_SwitchFromNumericToText_ReplacesComparisonsAndAllowsBlankEquals()
    {
        Task<SmartPlaylistEdit?> result = Open("Missing genre");
        Grid row = Rows().Single();
        ComboBox field = row.Children.OfType<ComboBox>().First();
        ComboBox comparison = row.Children.OfType<ComboBox>().Last();
        field.SelectedIndex = (int)SmartPlaylistField.Genre;
        row.Children.OfType<TextBox>().Single().Text = "";
        Pump();
        Assert.That(comparison.ItemCount, Is.EqualTo(3));
        Assert.That(Named<Button>("SaveSmartPlaylist").IsEnabled, Is.False);
        comparison.SelectedIndex = 1;
        Named<ComboBox>("SmartPlaylistMatch").SelectedIndex = 1;
        Pump();
        Click(Named<Button>("SaveSmartPlaylist"));

        SmartPlaylistEdit? edit = await result.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(edit!.Definition.Match, Is.EqualTo(SmartPlaylistMatch.Any));
            Assert.That(edit.Definition.Rules.Single(), Is.EqualTo(
                new SmartPlaylistRule(SmartPlaylistField.Genre, SmartPlaylistOperator.Equals, "")));
        });
    }

    [AvaloniaTest]
    public void RuleButtons_InsertAfterClickedRow_EnforceLimitsAndRetainLastRow()
    {
        Open("Favorites", new(SmartPlaylistMatch.All,
            [new(SmartPlaylistField.Rating, SmartPlaylistOperator.GreaterOrEqual, "4"),
             new(SmartPlaylistField.Genre, SmartPlaylistOperator.Equals, "Rock")]));
        Click(RuleButton(Rows()[0], "Add rule after this condition"));
        Assert.That(Rows().Select(row => row.Children.OfType<ComboBox>().First().SelectedIndex),
            Is.EqualTo(new[] { (int)SmartPlaylistField.Rating, (int)SmartPlaylistField.Title, (int)SmartPlaylistField.Genre }));

        for (int count = 3; count < 20; count++)
            ActivateByKeyboard(RuleButton(Rows()[0], "Add rule after this condition"));
        Assert.That(Rows(), Has.Length.EqualTo(20));
        Assert.That(Rows().Select(row => RuleButton(row, "Add rule after this condition").IsEnabled), Is.All.False);

        while (Rows().Length > 1) ActivateByKeyboard(RuleButton(Rows()[0], "Remove rule"));
        Assert.That(RuleButton(Rows()[0], "Remove rule").IsEnabled, Is.False);
        Assert.That(RuleButton(Rows()[0], "Add rule after this condition").IsEnabled, Is.True);
    }

    [AvaloniaTest]
    public void Dialog_BothThemes_CentersInputsAndIconsAtMinimumSize()
    {
        Open("Favorites");
        dialog.Width = dialog.MinWidth;
        dialog.Height = dialog.MinHeight;
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Avalonia.Application.Current!.RequestedThemeVariant = theme;
            Pump();
            foreach (TextBox input in dialog.GetVisualDescendants().OfType<TextBox>())
                Assert.That(input.VerticalContentAlignment, Is.EqualTo(VerticalAlignment.Center));
            foreach (ComboBox input in dialog.GetVisualDescendants().OfType<ComboBox>())
                Assert.That(input.VerticalContentAlignment, Is.EqualTo(VerticalAlignment.Center));
            foreach (Button button in dialog.GetVisualDescendants().OfType<Button>().Where(button => button.Content is PathIcon))
            {
                PathIcon icon = (PathIcon)button.Content!;
                Point origin = icon.TranslatePoint(default, button)!.Value;
                Assert.Multiple(() =>
                {
                    Assert.That(origin.X + icon.Bounds.Width / 2, Is.EqualTo(button.Bounds.Width / 2).Within(0.5));
                    Assert.That(origin.Y + icon.Bounds.Height / 2, Is.EqualTo(button.Bounds.Height / 2).Within(0.5));
                });
            }
        }
    }

    private Task<SmartPlaylistEdit?> Open(string name = "", SmartPlaylistDefinition? definition = null)
    {
        owner?.Close();
        owner = new Window();
        owner.Show();
        dialog = new(name, definition);
        Task<SmartPlaylistEdit?> result = dialog.ShowDialog<SmartPlaylistEdit?>(owner);
        Pump();
        return result;
    }

    private Grid[] Rows() => dialog.GetVisualDescendants().OfType<Grid>()
        .Where(grid => grid.Children.OfType<ComboBox>().Count() == 2).ToArray();

    private static Button RuleButton(Grid row, string name) => row.Children.OfType<Button>()
        .Single(button => AutomationProperties.GetName(button) == name);

    private T Named<T>(string name) where T : Control => dialog.GetVisualDescendants()
        .OfType<T>().Single(control => control.Name == name);

    private void Click(Button button)
    {
        button.BringIntoView();
        Pump();
        Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseDown(center, MouseButton.Left);
        dialog.MouseUp(center, MouseButton.Left);
        Pump();
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private void ActivateByKeyboard(Button button)
    {
        button.Focus();
        dialog.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        dialog.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Pump();
    }
}
