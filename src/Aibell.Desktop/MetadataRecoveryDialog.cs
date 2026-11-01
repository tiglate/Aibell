using Aibell.Desktop.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Aibell.Application.Library;

namespace Aibell.Desktop;

public sealed class MetadataRecoveryDialog : Window
{
    public MetadataRecoveryDialog(IReadOnlyCollection<string> folders,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<MetadataEditRecoveryScan>> inspect,
        Func<PendingMetadataEdit, CancellationToken, Task> discard,
        Func<Window, Task<string?>>? chooseFolder = null, CancellationToken cancellationToken = default)
    {
        Name = "MetadataRecoveryDialog"; Title = Loc.Get("Recovery.Title");
        Width = 740; Height = 590; MinWidth = 590; MinHeight = 490;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? operation = null;
        bool busy = false, closeRequested = false;
        string[] roots = folders.ToArray();
        SelectableTextBlock scope = new() { Name = "MetadataRecoveryFolders", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        TextBlock status = new() { Name = "MetadataRecoveryStatus", TextWrapping = TextWrapping.Wrap };
        SelectableTextBlock details = new() { Name = "MetadataRecoveryDetails", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        SelectableTextBlock issues = new() { Name = "MetadataRecoveryIssues", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        Expander review = new()
        {
            Header = Loc.Get("Recovery.NeedsReviewTab"),
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { Content = issues, MaxHeight = 110 }
        };
        ListBox list = new()
        {
            Name = "MetadataRecoveryList",
            Height = 180,
            MinHeight = 80,
            ItemTemplate = new FuncDataTemplate<PendingMetadataEdit>((edit, _) => new TextBlock
            {
                Text = edit is null ? "" : Path.GetFileName(edit.Source.LocalPath) + " — " +
                    (edit.State == PendingMetadataEditState.Validated ? Loc.Get("Recovery.Validated") : Loc.Get("Recovery.Incomplete")),
                Margin = new Thickness(5),
                TextWrapping = TextWrapping.Wrap
            })
        };
        AutomationProperties.SetName(list, Loc.Get("Recovery.Title"));
        Button scan = new() { Name = "ScanMetadataRecovery", Content = Loc.Get("Recovery.Scan"), Height = 30 },
            choose = new() { Name = "ChooseMetadataRecoveryFolder", Content = Loc.Get("Recovery.ChooseFolder"), Height = 30 },
            discardButton = new() { Name = "DiscardMetadataPreview", Content = Loc.Get("Recovery.DiscardButton"), Height = 30, IsEnabled = false },
            close = new() { Name = "CloseMetadataRecovery", Content = Loc.Get("Common.Close"), Height = 30, Width = 100 };
        void UpdateScope()
        {
            scope.Text = roots.Length == 0 ? Loc.Get("Recovery.ChooseHint")
                : string.Join("\n", roots) + "\n" + Loc.Get("Recovery.SubfoldersIncluded");
            scan.IsEnabled = !busy && roots.Length > 0;
        }
        void UpdateSelection()
        {
            discardButton.IsEnabled = !busy && list.SelectedItem is PendingMetadataEdit;
            details.Text = list.SelectedItem is PendingMetadataEdit selected
                ? Loc.Format("Recovery.Details", selected.Source.LocalPath, selected.StagedFile.LocalPath) : "";
        }
        void RequestClose()
        {
            if (!busy) { Close(false); return; }
            closeRequested = true; operation?.Cancel(); status.Text = Loc.Get("Common.Cancelling");
        }
        void SetBusy(bool value)
        {
            busy = value; list.IsEnabled = choose.IsEnabled = !value;
            close.Content = value ? Loc.Get("Recovery.CancelAndClose") : Loc.Get("Common.Close");
            close.Width = value ? 135 : 100;
            UpdateScope(); UpdateSelection();
        }
        async Task RefreshAsync(CancellationToken token)
        {
            status.Text = Loc.Get("Recovery.Scanning");
            MetadataEditRecoveryScan result = await inspect(roots, token);
            list.ItemsSource = result.Pending;
            review.IsVisible = result.Issues.Count > 0;
            review.Header = Loc.Format("Recovery.NeedsReview", result.Issues.Count);
            issues.Text = string.Join("\n\n", result.Issues.Select(issue => issue.Journal.LocalPath + "\n" + issue.Message));
            status.Text = Loc.Format("Recovery.Summary", result.Pending.Count, result.Issues.Count);
            UpdateSelection();
        }
        async Task Execute(Func<CancellationToken, Task> action)
        {
            if (busy || !IsVisible) return;
            using CancellationTokenSource current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            operation = current; SetBusy(true);
            try { await action(current.Token); }
            catch (OperationCanceledException) { status.Text = Loc.Get("Recovery.ScanCancelled"); }
            catch (Exception exception) { status.Text = exception.Message; }
            finally
            {
                operation = null; SetBusy(false);
                if (closeRequested) Close(false);
            }
        }
        list.SelectionChanged += (_, _) => UpdateSelection();
        scan.Click += async (_, _) => await Execute(RefreshAsync);
        choose.Click += async (_, _) =>
        {
            string? selected = chooseFolder is not null ? await chooseFolder(this) : await ChooseFolderAsync();
            if (selected is null || !IsVisible) return;
            roots = [selected]; list.ItemsSource = Array.Empty<PendingMetadataEdit>();
            issues.Text = details.Text = ""; review.IsVisible = false;
            UpdateScope(); await Execute(RefreshAsync);
        };
        discardButton.Click += async (_, _) =>
        {
            if (list.SelectedItem is not PendingMetadataEdit selected ||
                !await DialogChrome.ConfirmAsync(this, Loc.Get("Recovery.DiscardTitle"),
                    Loc.Get("Recovery.DiscardPrompt") + selected.StagedFile.LocalPath,
                    Loc.Get("Recovery.Discard"), "ConfirmDiscardMetadataPreview") || !IsVisible) return;
            await Execute(async token => { await discard(selected, token); await RefreshAsync(token); });
        };
        close.Click += (_, _) => RequestClose();
        StackPanel introduction = new() { Spacing = 8 };
        introduction.Children.Add(new TextBlock { Text = Loc.Get("Recovery.Intro"), TextWrapping = TextWrapping.Wrap });
        introduction.Children.Add(new ScrollViewer { Content = scope, MaxHeight = 65 });
        StackPanel scanActions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        scanActions.Children.Add(scan); scanActions.Children.Add(choose); introduction.Children.Add(scanActions);
        Grid body = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(18), RowSpacing = 10 };
        body.Children.Add(introduction); Grid.SetRow(list, 1); body.Children.Add(list);
        ScrollViewer selection = new() { Content = details, MaxHeight = 65 };
        Grid.SetRow(selection, 2); body.Children.Add(selection);
        Grid.SetRow(review, 3); body.Children.Add(review);
        ScrollViewer messages = new() { Content = status, MaxHeight = 60 }; Grid.SetRow(messages, 4); body.Children.Add(messages);
        Grid actions = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        actions.Children.Add(discardButton); Grid.SetColumn(close, 1); actions.Children.Add(close);
        discardButton.HorizontalAlignment = HorizontalAlignment.Left;
        Border footer = new() { Child = actions, Padding = new Thickness(14, 10), [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush") };
        Grid content = new() { RowDefinitions = new RowDefinitions("*,51") };
        content.Children.Add(new ScrollViewer { Content = body }); Grid.SetRow(footer, 1); content.Children.Add(footer);
        Content = DialogChrome.Wrap(this, content, () => { if (busy) RequestClose(); return !busy; });
        Opened += async (_, _) => { UpdateScope(); if (roots.Length > 0) await Execute(RefreshAsync); };
        Closing += (_, args) => { if (busy) { args.Cancel = true; RequestClose(); } };
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; RequestClose(); } };
    }

    private readonly CancellationTokenSource lifetime;

    private async Task<string?> ChooseFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        { Title = Loc.Get("Recovery.PickTitle"), AllowMultiple = false });
        return folders.Count > 0 && folders[0].Path is { IsFile: true } path ? path.LocalPath : null;
    }
}
