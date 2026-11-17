using Aibell.Desktop.Localization;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Aibell.Desktop.Classic;

/// <summary>Finds tracks in Aibell's library by any words of their title, artist or album, to add them to the classic playlist.</summary>
internal sealed class LibraryPickerWindow : Window
{
    private const int MaximumShown = 500;

    private readonly IReadOnlyList<Track> library;
    private readonly ListBox list = new() { Name = "ClassicLibraryResults", SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock count = new() { Name = "ClassicLibraryCount", Opacity = 0.75 };

    public LibraryPickerWindow(IReadOnlyList<Track> library)
    {
        this.library = library;
        Name = "ClassicLibraryPicker";
        Title = Loc.Get("Classic.AddFromLibrary");
        Width = 620;
        Height = 520;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        TextBox search = new() { Name = "ClassicLibrarySearch", PlaceholderText = Loc.Get("Classic.LibrarySearchHint") };
        AutomationProperties.SetName(search, Loc.Get("Classic.LibrarySearchHint"));
        AutomationProperties.SetName(list, Loc.Get("Classic.LibraryResults"));
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Track>((track, _) => new TextBlock
        {
            Text = track is null ? "" : $"{ClassicPlayerViewModel.DescribeTrack(track)}  ·  {track.Album}",
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        });
        Button add = new() { Name = "ClassicLibraryAdd", Content = Loc.Get("Classic.Add"), Width = 110, Height = 30 };
        Button play = new() { Name = "ClassicLibraryPlay", Content = Loc.Get("Classic.AddAndPlay"), Width = 130, Height = 30, Classes = { "dialogAccept" } };
        Button cancel = new() { Content = Loc.Get("Dialog.Cancel"), Width = 92, Height = 30 };
        add.Click += (_, _) => Finish(play: false);
        play.Click += (_, _) => Finish(play: true);
        cancel.Click += (_, _) => Close();
        list.DoubleTapped += (_, _) => Finish(play: true);
        search.TextChanged += (_, _) => Filter(search.Text);
        search.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Down && list.ItemCount > 0)
            {
                list.SelectedIndex = 0;
                list.Focus();
                args.Handled = true;
            }
        };
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) Close();
            else if (args.Key == Key.Enter) Finish(play: true);
            else return;
            args.Handled = true;
        };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, add, play } };
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 10, Margin = new Thickness(18) };
        layout.Children.Add(search);
        Grid.SetRow(list, 1);
        layout.Children.Add(list);
        Grid.SetRow(count, 2);
        layout.Children.Add(count);
        Grid.SetRow(buttons, 3);
        layout.Children.Add(buttons);
        Content = DialogChrome.Wrap(this, layout);
        Opened += (_, _) => search.Focus();
        Filter(null);
    }

    /// <summary>The tracks to add, in the order shown.</summary>
    public IReadOnlyList<Track> Chosen { get; private set; } = [];

    /// <summary>Whether the first chosen track should start playing.</summary>
    public bool PlayChosen { get; private set; }

    /// <summary>The tracks matching every word of <paramref name="text"/> in their title, artist, album or file name.</summary>
    internal static IEnumerable<Track> Matches(IEnumerable<Track> tracks, string? text)
    {
        string[] words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tracks.Where(track => words.All(word =>
            track.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase) || track.Artist.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
            track.Album.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
            Path.GetFileName(track.Location.LocalPath).Contains(word, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(track => track.Artist, StringComparer.CurrentCultureIgnoreCase).ThenBy(track => track.Album, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(track => track.DiscNumber).ThenBy(track => track.TrackNumber);
    }

    private void Filter(string? text)
    {
        List<Track> found = [.. Matches(library, text)];
        list.ItemsSource = found.Take(MaximumShown).ToList();
        count.Text = found.Count > MaximumShown ? Loc.Format("Classic.LibraryTooMany", MaximumShown, found.Count) : Loc.Format("Classic.LibraryCount", found.Count);
    }

    private void Finish(bool play)
    {
        List<Track> shown = list.ItemsSource as List<Track> ?? [];
        Chosen = list.SelectedItems?.Count > 0 ? [.. shown.Where(track => list.SelectedItems.Contains(track))] : [];
        PlayChosen = play;
        if (Chosen.Count > 0) Close();
    }
}
