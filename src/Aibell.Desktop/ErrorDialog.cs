using Aibell.Desktop.Localization;
using Aibell.Desktop.Services;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace Aibell.Desktop;

/// <summary>
/// Tells the user that something went wrong in plain words and keeps the technical text one click away behind <b>Details</b>.
/// While the window is open, further problems are appended to the details instead of opening more windows.
/// </summary>
public sealed class ErrorDialog : Window
{
    private const double CollapsedHeight = 230, ExpandedHeight = 520;
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox details = new()
    {
        Name = "ErrorDetailsText",
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"),
        FontSize = 12,
        IsVisible = false
    };
    private readonly Button toggle = new() { Name = "ErrorDetailsButton", Height = 30, MinWidth = 92, Padding = new Thickness(14, 0) };
    private readonly Button copy = new() { Name = "ErrorCopyButton", Height = 30, MinWidth = 92, Padding = new Thickness(14, 0), IsVisible = false };
    private int more;
    private string detailText;

    public ErrorDialog(ErrorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Title = report.Title;
        Width = 640;
        Height = CollapsedHeight;
        MinWidth = 420;
        MinHeight = CollapsedHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("DialogBrush");
        message.Text = report.Message;
        detailText = report.Details;
        details.Text = detailText;
        AutomationProperties.SetName(details, Loc.Get("ErrorDialog.DetailsAutomation"));
        toggle.Content = Loc.Get("ErrorDialog.Details");
        copy.Content = Loc.Get("ErrorDialog.Copy");
        Button close = new() { Name = "ErrorCloseButton", Content = Loc.Get("ErrorDialog.Close"), Height = 30, MinWidth = 92, Padding = new Thickness(14, 0), Classes = { "dialogAccept" } };
        toggle.Click += (_, _) => SetDetailsVisible(!details.IsVisible);
        copy.Click += async (_, _) =>
        {
            if (GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(detailText);
                copy.Content = Loc.Get("ErrorDialog.Copied");
            }
        };
        close.Click += (_, _) => Close();

        StackPanel left = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(toggle); left.Children.Add(copy);
        Grid buttons = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttons.Children.Add(left);
        Grid.SetColumn(close, 1); buttons.Children.Add(close);
        Grid body = new() { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        body.Children.Add(message);
        Grid.SetRow(details, 1); body.Children.Add(details);
        Grid.SetRow(buttons, 2); body.Children.Add(buttons);
        Content = DialogChrome.Wrap(this, body);
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Opened += (_, _) => close.Focus();
    }

    public bool DetailsVisible => details.IsVisible;

    /// <summary>The technical text shown (and copied) behind the Details button.</summary>
    public string DetailText => detailText;

    /// <summary>Adds another problem to this window instead of opening a second one.</summary>
    public void Append(ErrorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        more++;
        message.Text = report.Message + Environment.NewLine + Loc.Format("ErrorDialog.More", more);
        detailText = report.Details + Environment.NewLine + new string('-', 40) + Environment.NewLine + detailText;
        details.Text = detailText;
    }

    private void SetDetailsVisible(bool visible)
    {
        details.IsVisible = visible;
        copy.IsVisible = visible;
        toggle.Content = visible ? Loc.Get("ErrorDialog.HideDetails") : Loc.Get("ErrorDialog.Details");
        Height = visible ? Math.Max(Height, ExpandedHeight) : CollapsedHeight;
        if (visible) details.Focus();
    }
}
