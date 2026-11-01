using Aibell.Desktop.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Aibell.Application.Radio;
using Aibell.Domain.Radio;

namespace Aibell.Desktop;

public sealed class RadioPlaylistImportDialog : Window
{
    public RadioPlaylistImportDialog(Uri source, Func<CancellationToken, Task<RadioPlaylistImportPreview>> prepare,
        Func<RadioPlaylistImportPreview, CancellationToken, Task> import, CancellationToken cancellationToken = default)
    {
        Name = "RadioPlaylistImportDialog"; Title = Loc.Get("Picker.ImportStations");
        Width = 720; Height = 520; MinWidth = 540; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RadioPlaylistImportPreview? preview = null;
        bool reading = false, saving = false, closeRequested = false;
        TextBlock summary = new() { Name = "RadioImportSummary", TextWrapping = TextWrapping.Wrap };
        TextBlock status = new() { Name = "RadioImportStatus", TextWrapping = TextWrapping.Wrap };
        ListBox stations = new()
        {
            Name = "RadioImportStations",
            ItemTemplate = new FuncDataTemplate<RadioStation>((station, _) => new StackPanel
            {
                Margin = new Thickness(7, 4),
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = station?.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new SelectableTextBlock { Text = station?.Stream.AbsoluteUri, TextTrimming = TextTrimming.CharacterEllipsis,
                        [ToolTip.TipProperty] = station?.Stream.AbsoluteUri }
                }
            })
        };
        ListBox issues = new()
        {
            Name = "RadioImportIssues",
            ItemTemplate = new FuncDataTemplate<RadioPlaylistIssue>((issue, _) => new TextBlock
            {
                Text = issue is null ? "" : Loc.Format("Import.Entry", issue.Entry, issue.Message),
                Margin = new Thickness(7, 4),
                TextWrapping = TextWrapping.Wrap
            })
        };
        AutomationProperties.SetName(stations, Loc.Get("Import.StationsReady"));
        AutomationProperties.SetName(issues, Loc.Get("Import.SkippedStations"));
        TabItem skipped = new() { Header = Loc.Get("Import.SkippedTab"), Content = issues };
        TabControl tabs = new() { Items = { new TabItem { Header = Loc.Get("Import.StationsTab"), Content = stations }, skipped } };
        Button cancel = new() { Name = "CancelRadioImport", Content = Loc.Get("Dialog.Cancel"), Width = 92, Height = 30 },
            accept = new() { Name = "ConfirmRadioImport", Content = "Import", Width = 92, Height = 30, Classes = { "dialogAccept" }, IsEnabled = false };
        void Validate() => accept.IsEnabled = !reading && !saving && !lifetime.IsCancellationRequested && preview?.Stations.Count > 0;
        bool CanClose()
        {
            if (saving) return false;
            if (!reading) return true;
            closeRequested = true; lifetime.Cancel(); status.Text = Loc.Get("Common.Cancelling"); return false;
        }
        async Task PrepareAsync()
        {
            reading = true; summary.Text = Loc.Get("Import.ReadingStations"); Validate();
            try
            {
                preview = await prepare(lifetime.Token); lifetime.Token.ThrowIfCancellationRequested();
                stations.ItemsSource = preview.Stations; issues.ItemsSource = preview.Issues;
                skipped.Header = Loc.Format("Import.SkippedCount", preview.Issues.Count);
                summary.Text = Loc.Format("Import.PlaylistSummary", Loc.Plural("Import.StationCount", preview.Stations.Count), preview.Issues.Count, Loc.Plural("Import.DuplicateCount", preview.Duplicates));
                status.Text = preview.Stations.Count == 0 ? Loc.Get("Import.NoStations") : Loc.Get("Import.ReviewStations");
            }
            catch (OperationCanceledException) { status.Text = Loc.Get("Import.StationsCancelled"); }
            catch (Exception exception) { summary.Text = Loc.Get("Import.StationsFailed"); status.Text = exception.Message; }
            finally { reading = false; Validate(); if (closeRequested) Close(false); }
        }
        async Task ImportAsync()
        {
            Validate();
            if (!accept.IsEnabled || preview is null) return;
            saving = true; cancel.IsEnabled = tabs.IsEnabled = false; Validate();
            try { await import(preview, lifetime.Token); saving = false; Close(true); }
            catch (Exception exception)
            {
                status.Text = exception.Message; saving = false;
                cancel.IsEnabled = tabs.IsEnabled = true; Validate();
            }
        }
        cancel.Click += (_, _) => { if (CanClose()) Close(false); };
        accept.Click += async (_, _) => await ImportAsync();
        StackPanel heading = new()
        {
            Spacing = 8,
            Children =
            {
                new SelectableTextBlock { Text = source.LocalPath, TextTrimming = TextTrimming.CharacterEllipsis,
                    [ToolTip.TipProperty] = source.LocalPath }, summary
            }
        };
        Grid body = new() { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        body.Children.Add(heading); Grid.SetRow(tabs, 1); body.Children.Add(tabs);
        ScrollViewer messages = new() { Content = status, MaxHeight = 75 }; Grid.SetRow(messages, 2); body.Children.Add(messages);
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
        Opened += async (_, _) => { await PrepareAsync(); if (IsVisible) (accept.IsEnabled ? accept : cancel).Focus(); };
        Closing += (_, args) => { if (!CanClose()) args.Cancel = true; };
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
        KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; if (CanClose()) Close(false); }
            else if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control)) { args.Handled = true; await ImportAsync(); }
        };
    }
}
