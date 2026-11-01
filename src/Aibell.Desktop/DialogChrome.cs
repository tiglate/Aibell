using Aibell.Desktop.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Aibell.Desktop;

internal static class DialogChrome
{
    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string acceptText, string acceptName)
    {
        Window dialog = new()
        {
            Title = title,
            Width = 470,
            Height = 255,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        dialog[!Window.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("DialogBrush");
        Grid prompt = new() { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 14 };
        prompt.Children.Add(new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } });
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancel = new() { Content = Loc.Get("Dialog.Cancel"), Width = 92, Height = 30 },
            accept = new() { Name = acceptName, Content = acceptText, Width = 92, Height = 30, Classes = { "dialogAccept" } };
        cancel.Click += (_, _) => dialog.Close(false); accept.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(accept); Grid.SetRow(buttons, 1); prompt.Children.Add(buttons);
        dialog.Content = Wrap(dialog, prompt);
        dialog.KeyDown += (_, args) => { if (args.Key == Avalonia.Input.Key.Escape) { args.Handled = true; dialog.Close(false); } };
        return dialog.ShowDialog<bool>(owner);
    }

    public static Control Wrap(Window window, Control content, Func<bool>? canClose = null)
    {
        window.WindowDecorations = WindowDecorations.None;
        Button close = new()
        {
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Classes = { "toolbar" },
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new PathIcon
            {
                Width = 14,
                Height = 14,
                Foreground = Brushes.White,
                Data = Geometry.Parse("M5,3 L12,10 L19,3 L21,5 L14,12 L21,19 L19,21 L12,14 L5,21 L3,19 L10,12 L3,5 Z")
            }
        };
        AutomationProperties.SetName(close, Loc.Format("Dialog.CloseAutomation", window.Title));
        close.Click += (_, _) => { if (canClose?.Invoke() != false) window.Close(false); };
        Grid caption = new() { ColumnDefinitions = new ColumnDefinitions("*,40") };
        caption.Children.Add(new TextBlock
        {
            Text = window.Title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(16, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(close, 1); caption.Children.Add(close);
        Border title = new()
        {
            Child = caption,
            Background = new SolidColorBrush(Color.Parse("#303438")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D52268")),
            BorderThickness = new Thickness(0, 0, 0, 3)
        };
        title.PointerPressed += (_, args) => { if (args.GetCurrentPoint(title).Properties.IsLeftButtonPressed) window.BeginMoveDrag(args); };
        Grid layout = new() { RowDefinitions = new RowDefinitions("44,*") };
        layout.Children.Add(title); Grid.SetRow(content, 1); layout.Children.Add(content);
        return new Border { Child = layout, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#17191B")) };
    }
}
