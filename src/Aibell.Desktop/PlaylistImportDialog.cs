using Aibell.Desktop.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Aibell.Application.Playlists;
using Aibell.Domain.Media;

namespace Aibell.Desktop;

public sealed class PlaylistImportDialog : Window
{
    public PlaylistImportDialog(Uri source, Func<CancellationToken, Task<PlaylistImportPreview>> prepare,
        Func<string, PlaylistImportPreview, CancellationToken, Task> import, CancellationToken cancellationToken = default)
    {
        Name = "PlaylistImportDialog"; Title = Loc.Get("Picker.ImportPlaylist");
        Width = 720; Height = 560; MinWidth = 570; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        PlaylistImportPreview? preview = null;
        bool reading = false, saving = false, closeRequested = false;
        TextBox name = new()
        {
            Name = "ImportedPlaylistName",
            MaxLength = 200,
            PlaceholderText = Loc.Get("Dialog.PlaylistName"),
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(name, Loc.Get("Dialog.PlaylistName"));
        TextBlock summary = new() { Name = "PlaylistImportSummary", TextWrapping = TextWrapping.Wrap };
        TextBlock status = new() { Name = "PlaylistImportStatus", TextWrapping = TextWrapping.Wrap };
        ListBox tracks = new()
        {
            Name = "PlaylistImportTracks",
            ItemTemplate = new FuncDataTemplate<Track>((track, _) => new TextBlock
            {
                Text = track is null ? "" : track.Title + " — " + track.Artist,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(7, 4),
                [ToolTip.TipProperty] = track?.Location.LocalPath
            })
        };
        ListBox issues = new()
        {
            Name = "PlaylistImportIssues",
            ItemTemplate = new FuncDataTemplate<PlaylistFileIssue>((issue, _) => new StackPanel
            {
                Margin = new Thickness(7, 4),
                Spacing = 3,
                Children =
            {
                new TextBlock { Text = issue is null ? "" : Loc.Format("Import.Entry", issue.Entry, issue.Message), TextWrapping = TextWrapping.Wrap },
                new SelectableTextBlock { Text = issue?.Location, TextTrimming = TextTrimming.CharacterEllipsis, [ToolTip.TipProperty] = issue?.Location }
            }
            })
        };
        AutomationProperties.SetName(tracks, Loc.Get("Import.TracksReady")); AutomationProperties.SetName(issues, Loc.Get("Import.SkippedPlaylist"));
        TabItem skipped = new() { Header = Loc.Get("Import.SkippedTab"), Content = issues };
        TabControl tabs = new() { Items = { new TabItem { Header = Loc.Get("Import.TracksTab"), Content = tracks }, skipped } };
        Button cancel = new() { Name = "CancelPlaylistImport", Content = Loc.Get("Dialog.Cancel"), Width = 92, Height = 30 },
            accept = new() { Name = "ConfirmPlaylistImport", Content = "Import", Width = 92, Height = 30, Classes = { "dialogAccept" }, IsEnabled = false };
        void Validate() => accept.IsEnabled = !reading && !saving && preview?.Tracks.Count > 0 &&
            !string.IsNullOrWhiteSpace(name.Text) && !name.Text.Contains('\0');
        bool CanClose()
        {
            if (saving) return false;
            if (!reading) return true;
            closeRequested = true; lifetime.Cancel(); status.Text = Loc.Get("Common.Cancelling"); return false;
        }
        async Task PrepareAsync()
        {
            reading = true; name.IsEnabled = false; summary.Text = Loc.Get("Import.ReadingFiles"); Validate();
            try
            {
                preview = await prepare(lifetime.Token); lifetime.Token.ThrowIfCancellationRequested();
                name.Text = preview.Name;
                tracks.ItemsSource = preview.Tracks; issues.ItemsSource = preview.Issues;
                skipped.Header = Loc.Format("Import.SkippedCount", preview.Issues.Count);
                summary.Text = Loc.Format("Import.PlaylistSummary", Loc.Plural("Import.TrackCount", preview.Tracks.Count), preview.Issues.Count, Loc.Plural("Import.DuplicateCount", preview.Duplicates));
                status.Text = preview.Tracks.Count == 0 ? Loc.Get("Import.NoTracks") : Loc.Get("Import.ReviewPlaylist");
            }
            catch (OperationCanceledException) { status.Text = Loc.Get("Import.PlaylistCancelled"); }
            catch (Exception exception) { summary.Text = Loc.Get("Import.PlaylistFailed"); status.Text = exception.Message; }
            finally
            {
                reading = false; name.IsEnabled = true; Validate();
                if (closeRequested) Close(false);
            }
        }
        async Task ImportAsync()
        {
            Validate();
            if (!accept.IsEnabled || preview is null) return;
            saving = true; name.IsEnabled = cancel.IsEnabled = tabs.IsEnabled = false; Validate();
            try { await import(name.Text!.Trim(), preview, lifetime.Token); saving = false; Close(true); }
            catch (Exception exception)
            {
                status.Text = exception.Message; saving = false;
                name.IsEnabled = cancel.IsEnabled = tabs.IsEnabled = true; Validate();
            }
        }
        name.TextChanged += (_, _) => Validate();
        cancel.Click += (_, _) => { if (CanClose()) Close(false); };
        accept.Click += async (_, _) => await ImportAsync();
        StackPanel heading = new()
        {
            Spacing = 8,
            Children =
        {
            new SelectableTextBlock { Text = source.LocalPath, TextTrimming = TextTrimming.CharacterEllipsis, [ToolTip.TipProperty] = source.LocalPath },
            new TextBlock { Text = "PLAYLIST NAME", FontSize = 11, [!ForegroundProperty] = new DynamicResourceExtension("SecondaryTextBrush") }, name, summary
        }
        };
        Grid body = new() { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        body.Children.Add(heading); Grid.SetRow(tabs, 1); body.Children.Add(tabs);
        ScrollViewer messages = new() { Content = status, MaxHeight = 70 }; Grid.SetRow(messages, 2); body.Children.Add(messages);
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, accept }
        };
        Border footer = new() { Child = actions, Padding = new Thickness(14, 10), [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush") };
        Grid content = new() { RowDefinitions = new RowDefinitions("*,51") };
        content.Children.Add(body); Grid.SetRow(footer, 1); content.Children.Add(footer);
        Content = DialogChrome.Wrap(this, content, CanClose);
        Opened += async (_, _) => { await PrepareAsync(); if (IsVisible) name.Focus(); };
        Closing += (_, args) => { if (!CanClose()) args.Cancel = true; };
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
        KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; if (CanClose()) Close(false); }
            else if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control)) { args.Handled = true; await ImportAsync(); }
        };
    }
}
