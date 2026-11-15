using Aibell.Desktop.Localization;
using Aibell.Desktop.ViewModels;
using Aibell.Mpd.Client;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Aibell.Desktop;

/// <summary>Chooses how to reach the MPD server: found automatically, or a host (or socket path) and port, with a password for this session.</summary>
public sealed class MpdConnectionDialog : Window
{
    public MpdConnectionDialog(MpdViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Name = "MpdConnectionDialog"; Title = Loc.Get("Mpd.ConnectionTitle"); Width = 500; Height = 420; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        MpdConnectionSettings current = model.Settings;
        CheckBox automatic = new() { Name = "MpdAutomatic", Content = Loc.Get("Mpd.Automatic"), IsChecked = current.Automatic };
        TextBox host = new() { Name = "MpdHost", Text = current.Host, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        TextBox port = new() { Name = "MpdPort", Text = current.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        TextBox password = new() { Name = "MpdPassword", Text = model.Password, PasswordChar = '•', Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(host, Loc.Get("Mpd.Host")); AutomationProperties.SetName(port, Loc.Get("Mpd.Port")); AutomationProperties.SetName(password, Loc.Get("Mpd.Password"));
        TextBlock error = new() { Name = "MpdConnectionError", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#D52268")) };
        void Sync() { host.IsEnabled = port.IsEnabled = automatic.IsChecked != true; }
        automatic.IsCheckedChanged += (_, _) => Sync(); Sync();
        StackPanel form = new() { Spacing = 10, Margin = new Thickness(18) };
        form.Children.Add(automatic);
        form.Children.Add(new TextBlock { Text = Loc.Get("Mpd.AutomaticNote"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        form.Children.Add(Field(Loc.Get("Mpd.Host"), host));
        form.Children.Add(Field(Loc.Get("Mpd.Port"), port));
        form.Children.Add(Field(Loc.Get("Mpd.Password"), password));
        form.Children.Add(new TextBlock { Text = Loc.Get("Mpd.PasswordNote"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        form.Children.Add(error);
        Button save = new() { Name = "SaveMpdConnection", Content = Loc.Get("Dialog.Save"), Width = 92, Height = 30, Classes = { "dialogAccept" } };
        Button cancel = new() { Content = Loc.Get("Dialog.Cancel"), Width = 92, Height = 30 };
        save.Click += (_, _) =>
        {
            bool auto = automatic.IsChecked == true;
            bool validPort = int.TryParse(port.Text, out int number) && number is >= 1 and <= 65535;
            if (!auto && string.IsNullOrWhiteSpace(host.Text)) { error.Text = Loc.Get("Mpd.HostInvalid"); return; }
            if (!auto && !validPort) { error.Text = Loc.Get("Mpd.PortInvalid"); return; }
            model.ApplyConnection(new MpdConnectionSettings(auto, string.IsNullOrWhiteSpace(host.Text) ? current.Host : host.Text.Trim(), validPort ? number : current.Port),
                password.Text ?? "");
            Close(true);
        };
        cancel.Click += (_, _) => Close(false);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        Border footer = new() { Child = buttons, Padding = new Thickness(14, 10), [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush") };
        Grid layout = new() { RowDefinitions = new RowDefinitions("*,51") };
        layout.Children.Add(new ScrollViewer { Content = form }); Grid.SetRow(footer, 1); layout.Children.Add(footer);
        Content = DialogChrome.Wrap(this, layout);
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(false); } };
    }

    private static StackPanel Field(string label, Control content)
    {
        StackPanel field = new() { Spacing = 5 };
        field.Children.Add(new TextBlock { Text = label });
        field.Children.Add(content);
        return field;
    }
}
