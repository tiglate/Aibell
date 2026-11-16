using Aibell.Application.Lyrics;
using Aibell.Desktop.Localization;
using Aibell.Domain.Lyrics;
using Aibell.Domain.Media;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Aibell.Desktop;

/// <summary>Search the lyrics plugins by title and artist and choose the right lyrics for a track. The choice is remembered for the track.</summary>
public sealed class LyricsSearchDialog : Window
{
    /// <summary>The lyrics the user picked, or null when the dialog was cancelled.</summary>
    public TrackLyrics? Selected { get; private set; }

    public LyricsSearchDialog(LyricsService service, Track track)
    {
        Name = "LyricsSearchDialog"; Title = Loc.Get("Lyrics.SearchTitle"); Width = 640; Height = 560; MinWidth = 520; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        TextBox title = Input("LyricsSearchTitleBox", Loc.Get("Lyrics.SearchTitleField"), track.Title);
        TextBox artist = Input("LyricsSearchArtistBox", Loc.Get("Lyrics.SearchArtistField"), track.Artist);
        Button search = new() { Name = "LyricsSearchRun", Content = Loc.Get("Lyrics.SearchRun"), Height = 32, VerticalAlignment = VerticalAlignment.Bottom };
        ListBox results = new() { Name = "LyricsSearchResults", Height = 190 };
        AutomationProperties.SetName(results, Loc.Get("Lyrics.SearchResults"));
        TextBox preview = new() { Name = "LyricsSearchPreview", IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 150 };
        AutomationProperties.SetName(preview, Loc.Get("Lyrics.Preview"));
        TextBlock status = new() { Name = "LyricsSearchStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Button use = new() { Name = "LyricsSearchUse", Content = Loc.Get("Lyrics.Use"), Width = 140, Height = 30, IsEnabled = false, Classes = { "dialogAccept" } };
        Button cancel = new() { Content = Loc.Get("Dialog.Cancel"), Width = 92, Height = 30 };
        CancellationTokenSource lifetime = new();
        List<LyricsCandidate> found = [];
        bool busy = false;

        Grid inputs = new() { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), ColumnSpacing = 8 };
        inputs.Children.Add(Field(Loc.Get("Lyrics.SearchTitleField"), title));
        Control artistField = Field(Loc.Get("Lyrics.SearchArtistField"), artist); Grid.SetColumn(artistField, 1); inputs.Children.Add(artistField);
        Grid.SetColumn(search, 2); inputs.Children.Add(search);
        StackPanel form = new()
        {
            Spacing = 10,
            Margin = new Thickness(18),
            Children = { new TextBlock { Text = Loc.Get("Lyrics.SearchNote"), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, inputs, status, results, preview }
        };

        async Task RunSearch()
        {
            if (busy) return;
            busy = true; search.IsEnabled = false; use.IsEnabled = false; status.Text = Loc.Get("Lyrics.Searching"); results.ItemsSource = null; preview.Text = "";
            try
            {
                IReadOnlyList<LyricsCandidate> hits = await service.SearchAsync(new LyricsQuery(title.Text?.Trim() ?? "", artist.Text?.Trim() ?? "", track.Album, track.Duration), lifetime.Token);
                found = [.. hits.OrderByDescending(hit => hit.HasSyncedText).ThenBy(hit => Math.Abs((hit.Duration - track.Duration).TotalSeconds))];
                results.ItemsSource = found.Select(Describe).ToList();
                status.Text = found.Count == 0 ? Loc.Get("Lyrics.SearchNone") : Loc.Format("Lyrics.SearchCount", found.Count);
                if (found.Count > 0) results.SelectedIndex = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) when (exception is not OutOfMemoryException) { status.Text = exception.Message; }
            finally { busy = false; search.IsEnabled = true; }
        }

        results.SelectionChanged += (_, _) =>
        {
            LyricsCandidate? pick = results.SelectedIndex >= 0 && results.SelectedIndex < found.Count ? found[results.SelectedIndex] : null;
            use.IsEnabled = pick is not null && !busy;
            preview.Text = pick is null ? "" : pick.IsInstrumental ? Loc.Get("Lyrics.Instrumental")
                : TrackLyrics.FromText(pick.SyncedText, pick.PlainText, false, LyricsOrigin.Provider)?.ToText() ?? "";
        };
        search.Click += async (_, _) => await RunSearch();
        use.Click += async (_, _) =>
        {
            if (results.SelectedIndex < 0 || results.SelectedIndex >= found.Count || busy) return;
            busy = true; use.IsEnabled = search.IsEnabled = false;
            try
            {
                Selected = await service.SelectAsync(track, found[results.SelectedIndex], lifetime.Token);
                if (Selected is null) { status.Text = Loc.Get("Lyrics.NoText"); return; }
                busy = false; Close(true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException) { status.Text = exception.Message; }
            finally { if (!IsVisible) { } else { busy = false; search.IsEnabled = true; use.IsEnabled = results.SelectedIndex >= 0; } }
        };
        cancel.Click += (_, _) => Close(false);
        title.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await RunSearch(); } };
        artist.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await RunSearch(); } };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, use } };
        Border footer = new() { Child = buttons, Padding = new Thickness(14, 10), [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush") };
        Grid content = new() { RowDefinitions = new RowDefinitions("*,51") };
        content.Children.Add(new ScrollViewer { Content = form }); Grid.SetRow(footer, 1); content.Children.Add(footer);
        Content = DialogChrome.Wrap(this, content, () => !busy);
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
        Opened += async (_, _) => { await RunSearch(); };
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(false); } };
    }

    internal static string Describe(LyricsCandidate candidate)
    {
        string album = candidate.Album.Length > 0 ? $" ({candidate.Album})" : "";
        string flags = (candidate.HasSyncedText ? "  ♪ " + Loc.Get("Lyrics.Synced") : "") + (candidate.IsInstrumental ? "  · " + Loc.Get("Lyrics.InstrumentalBadge") : "");
        return $"{candidate.Artist} — {candidate.Title}{album}  {candidate.Duration:m\\:ss}{flags}";
    }

    private static TextBox Input(string name, string label, string value)
    {
        TextBox input = new() { Name = name, Text = value, MaxLength = 200, Height = 32, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0) };
        AutomationProperties.SetName(input, label); return input;
    }

    private static StackPanel Field(string label, Control control) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 11, [!ForegroundProperty] = new DynamicResourceExtension("SecondaryTextBrush") }, control }
    };
}
