using Aibell.Desktop.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Aibell.Desktop;

/// <summary>A shortcut as printed on the keys: each alternative is a combination (<c>Ctrl</c>, <c>Shift</c>, <c>N</c>), shown joined by "+".</summary>
public sealed record ShortcutEntry(IReadOnlyList<IReadOnlyList<string>> Alternatives, string Description);

public sealed record ShortcutSection(string Title, IReadOnlyList<ShortcutEntry> Entries);

/// <summary>Every keyboard shortcut the window handles, grouped as the Help &gt; Shortcuts window shows them.</summary>
public static class ShortcutCatalog
{
    private static ShortcutEntry Entry(string description, params string[][] alternatives) => new(alternatives, description);

    public static IReadOnlyList<ShortcutSection> Create() =>
    [
        new ShortcutSection(Loc.Get("Shortcuts.Section.General"),
        [
            Entry(Loc.Get("Shortcuts.Search"), ["Ctrl", "F"]),
            Entry(Loc.Get("Shortcuts.ClearSearch"), ["Esc"]),
            Entry(Loc.Get("Main.Preferences"), ["Ctrl", ","]),
            Entry(Loc.Get("Menu.Quit"), ["Ctrl", "Q"])
        ]),
        new ShortcutSection(Loc.Get("Shortcuts.Section.Library"),
        [
            Entry(Loc.Get("Main.OpenFiles"), ["Ctrl", "Shift", "O"]),
            Entry(Loc.Get("Main.AddFolder"), ["Ctrl", "O"]),
            Entry(Loc.Get("Main.CreatePlaylist"), ["Ctrl", "N"]),
            Entry(Loc.Get("Main.CreateSmartPlaylist"), ["Ctrl", "Shift", "N"]),
            Entry(Loc.Get("Main.ViewMetadata"), ["Alt", "Enter"])
        ]),
        new ShortcutSection(Loc.Get("Shortcuts.Section.Playback"),
        [
            Entry(Loc.Get("Shortcuts.PlayPause"), ["Space"]),
            Entry(Loc.Get("Shortcuts.PlaySelected"), ["Enter"]),
            Entry(Loc.Get("Main.Previous"), ["Alt", "←"]),
            Entry(Loc.Get("Main.Next"), ["Alt", "→"])
        ]),
        new ShortcutSection(Loc.Get("Shortcuts.Section.Seek"),
        [
            Entry(Loc.Get("Shortcuts.Seek5"), ["←"], ["→"]),
            Entry(Loc.Get("Shortcuts.Seek15"), ["Page Down"], ["Page Up"]),
            Entry(Loc.Get("Shortcuts.SeekEnds"), ["Home"], ["End"])
        ]),
        new ShortcutSection(Loc.Get("Shortcuts.Section.Volume"),
        [
            Entry(Loc.Get("Shortcuts.Volume5"), ["←"], ["→"]),
            Entry(Loc.Get("Shortcuts.Volume10"), ["Page Down"], ["Page Up"]),
            Entry(Loc.Get("Shortcuts.VolumeEnds"), ["Home"], ["End"])
        ]),
        new ShortcutSection(Loc.Get("Shortcuts.Section.Podcasts"),
        [
            Entry(Loc.Get("Pod.Subscribe"), ["Enter"]),
            Entry(Loc.Get("Shortcuts.Rate"), ["0"], ["1"], ["2"], ["3"], ["4"], ["5"]),
            Entry(Loc.Get("Shortcuts.DeleteDownload"), ["Delete"]),
            Entry(Loc.Get("Pod.ClearSearch"), ["Esc"])
        ])
    ];
}

/// <summary>The Help &gt; Shortcuts window: a scrolling list of key caps and what they do.</summary>
public sealed class ShortcutsWindow : Window
{
    public ShortcutsWindow(IReadOnlyList<ShortcutSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        Title = Loc.Get("Shortcuts.Title");
        Width = 540;
        Height = 620;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");

        Grid.SetIsSharedSizeScope(this, true);
        StackPanel list = new() { Name = "ShortcutList", Margin = new Thickness(4, 4, 20, 4), Spacing = 16 };
        foreach (ShortcutSection section in sections)
        {
            Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 6, ColumnSpacing = 16 };
            for (int row = 0; row < section.Entries.Count; row++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Control keys = Keys(section.Entries[row]);
                TextBlock description = new() { Text = section.Entries[row].Description, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(keys, row); grid.Children.Add(keys);
                Grid.SetRow(description, row); Grid.SetColumn(description, 1); grid.Children.Add(description);
            }
            // The key caps column is as wide as the widest entry in every section so the descriptions line up.
            grid.ColumnDefinitions[0].SharedSizeGroup = "Keys";
            list.Children.Add(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = section.Title, FontSize = 13, FontWeight = FontWeight.SemiBold },
                    grid
                }
            });
        }
        Button close = new() { Name = "CloseShortcuts", Content = Loc.Get("Common.Close"), Width = 92, Height = 30, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close(false);
        Grid layout = new() { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 12 };
        layout.Children.Add(new ScrollViewer
        {
            Name = "ShortcutScroll",
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        Grid.SetRow(close, 1); layout.Children.Add(close);
        Content = DialogChrome.Wrap(this, layout);
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(false); } };
    }

    // Key names are the words printed on keyboards, so they are not translated and the row never mirrors in right-to-left languages.
    private static StackPanel Keys(ShortcutEntry entry)
    {
        StackPanel panel = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        for (int alternative = 0; alternative < entry.Alternatives.Count; alternative++)
        {
            if (alternative > 0) panel.Children.Add(Separator("/"));
            IReadOnlyList<string> combination = entry.Alternatives[alternative];
            for (int key = 0; key < combination.Count; key++)
            {
                if (key > 0) panel.Children.Add(Separator("+"));
                panel.Children.Add(Cap(combination[key]));
            }
        }
        return panel;
    }

    private static TextBlock Separator(string text)
    {
        TextBlock block = new() { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        block.Classes.Add("secondary");
        return block;
    }

    private static Border Cap(string text)
    {
        Border cap = new()
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2),
            MinWidth = 24,
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }
        };
        cap[!Border.BackgroundProperty] = new DynamicResourceExtension("PillBrush");
        cap[!Border.BorderBrushProperty] = new DynamicResourceExtension("PanelBorderBrush");
        return cap;
    }
}
