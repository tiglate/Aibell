using Aibell.Desktop.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

/// <summary>
/// The Audiobooks workspace. Opening it only reads the local shelf. The view owns no work: its <see cref="AudiobooksViewModel"/>
/// is created, loaded and shut down by the host (the main window), so navigating away only hides it.
/// </summary>
public sealed partial class AudiobooksView : UserControl
{
    private static FilePickerFileType AudioFiles => new(Loc.Get("Book.AudioFiles"))
    {
        Patterns = ["*.mp3", "*.m4a", "*.m4b", "*.aac", "*.ogg", "*.oga", "*.opus", "*.flac", "*.wav"]
    };

    public AudiobooksView() => InitializeComponent();

    /// <summary>The picker used by Add; the host sets it because the view is created from XAML.</summary>
    public IDesktopFilePicker? Picker { get; set; }

    private AudiobooksViewModel? Model => DataContext as AudiobooksViewModel;

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private async void Add(object? sender, RoutedEventArgs args) => await AddAsync();

    internal async Task AddAsync()
    {
        if (Model is not { } m) return;
        if (Picker is null || Owner is not { } owner) { m.StatusMessage = Loc.Get("Book.FileSelectionUnavailable"); return; }
        await m.PickAndAddAsync(() => Picker.PickFileAsync(owner, new FilePickerOpenOptions
        {
            Title = Loc.Get("Book.AddTitle"),
            AllowMultiple = false,
            FileTypeFilter = [AudioFiles]
        }));
    }

    private void CancelOperation(object? sender, RoutedEventArgs args) => Model?.CancelOperation();
    private async void Play(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.PlayAsync(m.SelectedItem, resume: false); }
    private async void Resume(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.PlayAsync(m.SelectedItem, resume: true); }
    private async void ListChapters(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.ShowChaptersAsync(m.SelectedItem); }
    private async void PlaySelectedChapter(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.PlayChapterAsync(); }
    private async void SavePosition(object? sender, RoutedEventArgs args) { if (Model is { } m) await m.SaveCurrentAsync(); }

    private async void ItemDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (Model is { SelectedItem: { } item } m) await m.PlayAsync(item, resume: item.CanResume);
    }

    private async void Remove(object? sender, RoutedEventArgs args) => await RemoveSelectedAsync();

    internal async Task RemoveSelectedAsync()
    {
        if (Model is not { SelectedItem: { } selected } m || Owner is not { } owner) return;
        if (!await DialogChrome.ConfirmAsync(owner, Loc.Get("Book.RemoveTitle"),
                Loc.Format("Book.RemovePrompt", selected.Title),
                Loc.Get("Dialog.Remove"), "ConfirmRemoveAudiobook")) return;
        await m.RemoveSelectedAsync();
    }
}
