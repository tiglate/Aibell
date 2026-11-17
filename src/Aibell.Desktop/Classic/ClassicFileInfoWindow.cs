using System.Globalization;
using Aibell.Desktop.Localization;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Aibell.Desktop.Classic;

/// <summary>The classic player's file information: what is known about one playlist entry.</summary>
internal sealed class ClassicFileInfoWindow : Window
{
    public ClassicFileInfoWindow(Track track)
    {
        Name = "ClassicFileInfo";
        Title = Loc.Get("Classic.FileInfo");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        (string Label, string Value)[] rows =
        [
            (Loc.Get("Column.Title"), track.Title),
            (Loc.Get("Column.Artist"), track.Artist),
            (Loc.Get("Column.Album"), track.Album),
            (Loc.Get("Column.Time"), ClassicPlayerViewModel.FormatDuration(track.Duration)),
            (Loc.Get("Classic.BitRate"), track.BitRate > 0 ? string.Create(CultureInfo.CurrentCulture, $"{track.BitRate} kbps") : "—"),
            (Loc.Get("Classic.SampleRate"), track.SampleRate > 0 ? string.Create(CultureInfo.CurrentCulture, $"{track.SampleRate} Hz") : "—"),
            (Loc.Get("Classic.Location"), track.Location.IsFile ? track.Location.LocalPath : track.Location.ToString()),
        ];
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14, RowSpacing = 6, Margin = new Thickness(18) };
        for (int row = 0; row < rows.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBlock label = new() { Text = rows[row].Label, Opacity = 0.75 };
            SelectableTextBlock value = new() { Text = rows[row].Value, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        Button close = new() { Content = Loc.Get("Classic.Close"), Width = 92, Height = 30, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 0, 18, 18), Classes = { "dialogAccept" } };
        close.Click += (_, _) => Close();
        KeyDown += (_, args) =>
        {
            if (args.Key is not (Key.Escape or Key.Enter)) return;
            args.Handled = true;
            Close();
        };
        Content = DialogChrome.Wrap(this, new StackPanel { Children = { grid, close } });
    }
}
