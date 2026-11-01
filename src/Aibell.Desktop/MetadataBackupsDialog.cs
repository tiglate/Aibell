using Aibell.Desktop.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Aibell.Application.Library;
using Aibell.Domain.Media;

namespace Aibell.Desktop;

public sealed class MetadataBackupsDialog : Window
{
    public MetadataBackupsDialog(Track track, Func<CancellationToken, Task<MetadataBackupScan>> inspect,
        Func<MetadataFileBackup, CancellationToken, Task> restore, Func<MetadataBackupInfo, CancellationToken, Task> delete,
        CancellationToken cancellationToken = default)
    {
        Title = Loc.Get("Backups.Title"); Width = 700; Height = 500; MinWidth = 570; MinHeight = 410;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = lifetime.Token;
        bool busy = false;
        ListBox list = new()
        {
            Name = "MetadataBackupList",
            MinHeight = 100,
            ItemTemplate = new FuncDataTemplate<MetadataBackupInfo>((item, _) => new TextBlock
            { Text = item?.DisplayName, Margin = new Thickness(5), TextWrapping = TextWrapping.Wrap })
        };
        Avalonia.Automation.AutomationProperties.SetName(list, Loc.Get("Backups.ListAutomation"));
        SelectableTextBlock details = new() { Name = "MetadataBackupDetails", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        TextBlock status = new() { Name = "MetadataBackupStatus", TextWrapping = TextWrapping.Wrap };
        Button refresh = new() { Name = "RefreshMetadataBackups", Content = Loc.Get("Pod.Refresh"), Height = 30 },
            restoreButton = new() { Name = "RestoreSelectedMetadataBackup", Content = Loc.Get("Backups.Restore"), Height = 30, IsEnabled = false },
            deleteButton = new() { Name = "DeleteMetadataBackup", Content = Loc.Get("Backups.Delete"), Height = 30, IsEnabled = false },
            close = new() { Content = Loc.Get("Common.Close"), Width = 92, Height = 30 };
        void UpdateActions()
        {
            restoreButton.IsEnabled = !busy && list.SelectedItem is MetadataBackupInfo { CanRestore: true };
            deleteButton.IsEnabled = !busy && list.SelectedItem is MetadataBackupInfo;
            details.Text = list.SelectedItem is MetadataBackupInfo selected ? selected.Backup.Backup.LocalPath : "";
        }
        list.SelectionChanged += (_, _) => UpdateActions();
        async Task RefreshAsync()
        {
            MetadataBackupScan scan = await inspect(token);
            list.ItemsSource = scan.Backups;
            long bytes = scan.Backups.Sum(backup => backup.Size);
            status.Text = Loc.Format("Backups.Summary", scan.Backups.Count, (bytes / 1048576d).ToString("N2", System.Globalization.CultureInfo.CurrentCulture)) +
                (scan.Issues.Count == 0 ? "" : Loc.Format("Backups.Unverified", scan.Issues.Count)) +
                Loc.Get("Backups.Rules");
            if (scan.Issues.Count > 0)
                status.Text += "\n" + string.Join("\n", scan.Issues.Select(issue => Path.GetFileName(issue.Journal.LocalPath) + ": " + issue.Message));
            UpdateActions();
        }
        async Task Execute(Func<Task> action)
        {
            if (busy) return;
            busy = true; list.IsEnabled = refresh.IsEnabled = close.IsEnabled = false; UpdateActions();
            try { await action(); }
            catch (OperationCanceledException) { status.Text = Loc.Get("Common.OperationCancelled"); }
            catch (Exception exception) { status.Text = exception.Message; }
            finally { busy = false; list.IsEnabled = refresh.IsEnabled = close.IsEnabled = true; UpdateActions(); }
        }
        refresh.Click += async (_, _) => await Execute(RefreshAsync);
        restoreButton.Click += async (_, _) =>
        {
            if (list.SelectedItem is not MetadataBackupInfo { CanRestore: true } selected ||
                !await DialogChrome.ConfirmAsync(this, Loc.Get("Backups.RestoreTitle"),
                    Loc.Get("Backups.RestorePrompt"),
                    Loc.Get("Backups.RestoreAccept"), "ConfirmRestoreMetadata")) return;
            await Execute(async () => { await restore(selected.Backup, token); busy = false; Close(true); });
        };
        deleteButton.Click += async (_, _) =>
        {
            if (list.SelectedItem is not MetadataBackupInfo selected ||
                !await DialogChrome.ConfirmAsync(this, Loc.Get("Backups.DeleteTitle"),
                    Loc.Format("Backups.DeletePrompt", selected.Backup.Backup.LocalPath),
                    Loc.Get("Dialog.Delete"), "ConfirmDeleteMetadataBackup")) return;
            await Execute(async () => { await delete(selected, token); await RefreshAsync(); });
        };
        close.Click += (_, _) => Close(false);
        Grid body = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(18), RowSpacing = 12 };
        body.Children.Add(new TextBlock { Text = track.Title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetRow(list, 1); body.Children.Add(list); Grid.SetRow(details, 2); body.Children.Add(details);
        ScrollViewer messages = new() { Content = status, MaxHeight = 100 }; Grid.SetRow(messages, 3); body.Children.Add(messages);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(refresh); actions.Children.Add(restoreButton); actions.Children.Add(deleteButton);
        Grid buttons = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") }; buttons.Children.Add(actions); Grid.SetColumn(close, 1); buttons.Children.Add(close);
        Border footer = new() { Child = buttons, Padding = new Thickness(14, 10), [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush") };
        Grid content = new() { RowDefinitions = new RowDefinitions("*,51") }; content.Children.Add(body); Grid.SetRow(footer, 1); content.Children.Add(footer);
        Content = DialogChrome.Wrap(this, content, () => !busy);
        Opened += async (_, _) => await Execute(RefreshAsync);
        Closing += (_, args) => args.Cancel = busy;
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
        KeyDown += (_, args) => { if (args.Key == Key.Escape && !busy) { args.Handled = true; Close(false); } };
    }

    private readonly CancellationTokenSource lifetime;
}
