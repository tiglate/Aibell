using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Aibell.Application.Plugins;
using Aibell.Desktop.Localization;

namespace Aibell.Desktop;

/// <summary>
/// The plugin manager shown in Preferences: every plugin with its description, version and state, a switch to enable it, and
/// Install / Uninstall for plugins the user added. Plugins are never loaded or unloaded while the application runs, so each
/// change states plainly that it applies after a restart. Changes are applied immediately and do not wait for Save.
/// </summary>
public sealed class PluginsPanel : UserControl
{
    private readonly IPluginAdministration? administration;
    private readonly Func<Task<string?>>? pickFolder;
    private readonly Func<Task<string?>>? pickArchive;
    private readonly StackPanel rows = new() { Spacing = 6 };
    private readonly TextBlock message = new() { Name = "PluginsMessage", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button installFolder = new() { Name = "InstallPluginFolder", Content = Loc.Get("Plugins.InstallFolder"), Height = 30 };
    private readonly Button installArchive = new() { Name = "InstallPluginZip", Content = Loc.Get("Plugins.InstallZip"), Height = 30 };
    private bool busy;

    public PluginsPanel(IPluginAdministration? administration, Func<Task<string?>>? pickFolder = null, Func<Task<string?>>? pickArchive = null)
    {
        this.administration = administration;
        this.pickFolder = pickFolder;
        this.pickArchive = pickArchive;
        Name = "PluginsPanel";
        Grid layout = new() { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8 };
        layout.Children.Add(new TextBlock
        {
            Text = Loc.Get("Plugins.Intro"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        ScrollViewer scroll = new() { Content = rows, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        Grid.SetRow(message, 2); layout.Children.Add(message);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        installFolder.IsEnabled = installArchive.IsEnabled = administration is not null && pickFolder is not null && pickArchive is not null;
        installFolder.Click += async (_, _) => await InstallAsync(pickFolder);
        installArchive.Click += async (_, _) => await InstallAsync(pickArchive);
        buttons.Children.Add(installFolder); buttons.Children.Add(installArchive);
        Grid.SetRow(buttons, 3); layout.Children.Add(buttons);
        Content = layout;
        Refresh();
    }

    public void Refresh()
    {
        rows.Children.Clear();
        IReadOnlyList<PluginEntry> entries = administration?.List() ?? [];
        if (entries.Count == 0)
        {
            rows.Children.Add(new TextBlock { Name = "NoPlugins", Text = Loc.Get("Plugins.None"), Margin = new Thickness(0, 6) });
            return;
        }
        foreach (PluginEntry entry in entries) rows.Children.Add(Row(entry));
    }

    public static string StatusOf(PluginEntry entry) =>
        entry.Failure is { } failure ? Loc.Format("Plugins.Status.Failed", failure)
        : entry.RemovalPending ? Loc.Get("Plugins.Status.Removal")
        : entry.IsRunning && entry.EnabledNext ? Loc.Get("Plugins.Status.Active")
        : entry.IsRunning ? Loc.Get("Plugins.Status.WillStop")
        : entry.EnabledNext ? Loc.Get("Plugins.Status.WillStart")
        : Loc.Get("Plugins.Status.Off");

    private Border Row(PluginEntry entry)
    {
        StackPanel text = new() { Spacing = 2 };
        string title = entry.Version.Length > 0 ? $"{entry.Name}  {entry.Version}" : entry.Name;
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (entry.Description.Length > 0) text.Children.Add(new TextBlock { Text = entry.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Name = "PluginStatus_" + entry.Folder,
            Text = (entry.IsUserInstalled ? Loc.Get("Plugins.OriginUser") : Loc.Get("Plugins.OriginBundled")) + StatusOf(entry),
            FontSize = 11,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap
        });
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        CheckBox enabled = new()
        {
            Name = "PluginEnabled_" + entry.Folder,
            Content = Loc.Get("Plugins.Enabled"),
            IsChecked = entry.EnabledNext,
            IsEnabled = entry.Failure is null && !entry.RemovalPending && !busy
        };
        AutomationProperties.SetName(enabled, Loc.Format("Plugins.EnableAutomation", entry.Name));
        enabled.IsCheckedChanged += (_, _) => Apply(() => administration!.SetEnabled(entry.Folder, enabled.IsChecked == true));
        actions.Children.Add(enabled);
        if (entry.IsUserInstalled && !entry.RemovalPending)
        {
            Button uninstall = new() { Name = "PluginUninstall_" + entry.Folder, Content = Loc.Get("Plugins.Uninstall"), Height = 28, IsEnabled = !busy };
            AutomationProperties.SetName(uninstall, Loc.Format("Plugins.UninstallAutomation", entry.Name));
            uninstall.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is not Window owner ||
                    !await DialogChrome.ConfirmAsync(owner, Loc.Get("Plugins.UninstallTitle"), Loc.Format("Plugins.UninstallPrompt", entry.Name),
                        Loc.Get("Plugins.UninstallAccept"), "ConfirmUninstallPlugin")) return;
                Apply(() => administration!.Uninstall(entry.Folder), Loc.Format("Plugins.UninstallScheduled", entry.Name));
            };
            actions.Children.Add(uninstall);
        }
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(text); Grid.SetColumn(actions, 1); row.Children.Add(actions);
        return new Border
        {
            Name = "PluginRow_" + entry.Folder,
            Padding = new Thickness(10, 8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            [!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("PanelBorderBrush"),
            Child = row
        };
    }

    private void Apply(Action change, string? done = null)
    {
        try
        {
            change();
            message.Text = done ?? Loc.Get("Plugins.Saved");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            message.Text = exception.Message;
        }
        Refresh();
    }

    private async Task InstallAsync(Func<Task<string?>>? pick)
    {
        if (busy || administration is null || pick is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        string? source = await pick();
        if (string.IsNullOrWhiteSpace(source)) return;
        if (!await DialogChrome.ConfirmAsync(owner, Loc.Get("Plugins.InstallTitle"), Loc.Format("Plugins.InstallWarning", source),
                Loc.Get("Plugins.InstallAccept"), "ConfirmInstallPlugin")) return;
        busy = true; installFolder.IsEnabled = installArchive.IsEnabled = false; Refresh();
        try
        {
            string name = await administration.InstallAsync(source);
            message.Text = Loc.Format("Plugins.Installed", name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            message.Text = Loc.Format("Plugins.InstallFailed", exception.Message);
        }
        finally
        {
            busy = false; installFolder.IsEnabled = installArchive.IsEnabled = true; Refresh();
        }
    }
}
