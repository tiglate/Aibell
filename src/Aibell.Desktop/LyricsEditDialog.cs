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
using Avalonia.Media;

namespace Aibell.Desktop;

public enum LyricsEditOutcome { Cancelled, Saved, Removed }

/// <summary>
/// Type or paste lyrics for a track. Text with [mm:ss.xx] timestamps becomes synced lyrics. The lyrics are kept in Aibell's own database,
/// and can also be written into the audio file's tags (with a backup) when the file allows it.
/// </summary>
public sealed class LyricsEditDialog : Window
{
    public LyricsEditOutcome Outcome { get; private set; }

    /// <summary>The lyrics to show after a save (null after saving empty text); meaningful only when <see cref="Outcome"/> is Saved.</summary>
    public TrackLyrics? Result { get; private set; }

    public LyricsEditDialog(LyricsService service, Track track, TrackLyrics? current, string? fileWriteBlock, Func<Track, string, Task> saveToFile)
    {
        Name = "LyricsEditDialog"; Title = Loc.Get("Lyrics.EditTitle"); Width = 620; Height = 600; MinWidth = 500; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        TextBox text = new()
        {
            Name = "LyricsEditText",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Text = current?.ToText() ?? "",
            MinHeight = 280,
            FontFamily = new FontFamily("monospace"),
            MaxLength = 200_000
        };
        AutomationProperties.SetName(text, Loc.Get("Lyrics.EditTextAutomation"));
        CheckBox toFile = new() { Name = "LyricsEditToFile", Content = Loc.Get("Lyrics.SaveToFile"), IsEnabled = fileWriteBlock is null };
        if (fileWriteBlock is not null) ToolTip.SetTip(toFile, fileWriteBlock);
        else ToolTip.SetTip(toFile, Loc.Get("Lyrics.SaveToFileTip"));
        TextBlock status = new() { Name = "LyricsEditStatus", TextWrapping = TextWrapping.Wrap };
        Button save = new() { Name = "LyricsEditSave", Content = Loc.Get("Dialog.Save"), Width = 92, Height = 30, Classes = { "dialogAccept" } };
        Button cancel = new() { Content = Loc.Get("Dialog.Cancel"), Width = 92, Height = 30 };
        Button remove = new() { Name = "LyricsEditRemove", Content = Loc.Get("Lyrics.Remove"), Height = 30, IsEnabled = current is { IsUserOwned: true }, HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(remove, Loc.Get("Lyrics.RemoveTip"));
        bool busy = false;
        StackPanel form = new()
        {
            Spacing = 10,
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = Loc.Format("Lyrics.EditFor", track.Artist, track.Title), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = Loc.Get("Lyrics.EditNote"), TextWrapping = TextWrapping.Wrap, FontSize = 11 },
                text, toFile, status
            }
        };

        async Task Run(Func<Task> action)
        {
            if (busy) return;
            busy = true; form.IsEnabled = false; save.IsEnabled = cancel.IsEnabled = remove.IsEnabled = false;
            try { await action(); busy = false; Close(true); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                busy = false; form.IsEnabled = cancel.IsEnabled = save.IsEnabled = true; remove.IsEnabled = current is { IsUserOwned: true }; status.Text = exception.Message;
            }
        }

        save.Click += async (_, _) => await Run(async () =>
        {
            string value = text.Text ?? "";
            if (toFile.IsChecked == true)
            {
                await saveToFile(track, value);
                // The file now carries the lyrics (the backup is in the track's file backups); the database copy would only shadow later edits of the file.
                await service.ForgetAsync(track);
                Result = TrackLyrics.FromText(value, value, false, LyricsOrigin.Embedded, "file");
            }
            else Result = await service.SaveWrittenAsync(track, value);
            Outcome = LyricsEditOutcome.Saved;
        });
        remove.Click += async (_, _) => await Run(async () => { await service.ForgetAsync(track); Outcome = LyricsEditOutcome.Removed; });
        cancel.Click += (_, _) => Close(false);
        StackPanel right = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } };
        Grid buttons = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        buttons.Children.Add(remove); Grid.SetColumn(right, 1); buttons.Children.Add(right);
        Border footer = new() { Child = buttons, Padding = new Thickness(14, 10), [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush") };
        Grid content = new() { RowDefinitions = new RowDefinitions("*,51") };
        content.Children.Add(new ScrollViewer { Content = form }); Grid.SetRow(footer, 1); content.Children.Add(footer);
        Content = DialogChrome.Wrap(this, content, () => !busy);
        Closing += (_, args) => args.Cancel = busy;
        Opened += (_, _) => text.Focus();
        KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape && !busy) { args.Handled = true; Close(false); }
            else if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control) && !busy) { args.Handled = true; save.Focus(); await Task.Yield(); save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); }
        };
    }
}
