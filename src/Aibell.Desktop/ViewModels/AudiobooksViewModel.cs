using Aibell.Desktop.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using Aibell.Application.Audiobooks;
using Aibell.Domain.Audiobooks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

public sealed partial class AudiobookItem(AudiobookShelfEntry entry, bool fileExists) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Author), nameof(Meta), nameof(StateText), nameof(CanResume), nameof(IsFinished))]
    private AudiobookShelfEntry entry = entry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(CanPlay), nameof(CanResume))]
    private bool fileExists = fileExists;

    public string Title => Entry.Title;
    public string Author => string.IsNullOrWhiteSpace(Entry.Author) ? Loc.Get("Book.UnknownAuthor") : Entry.Author;
    public bool IsFinished => ResumeRules.IsFinished(Entry.SavedPosition, Entry.Duration);
    public bool CanPlay => FileExists;
    public bool CanResume => FileExists && Entry.HasSavedPosition && !IsFinished;
    public string Meta => Entry.Duration > TimeSpan.Zero ? Loc.Format("Book.Length", Format(Entry.Duration)) : Loc.Get("Book.LengthUnknown");
    public string StateText => !FileExists ? Loc.Get("Book.Missing")
        : IsFinished ? Loc.Get("Book.Finished")
        : Entry.HasSavedPosition ? Loc.Format("Book.SavedOf", Format(Entry.SavedPosition), Format(Entry.Duration))
        : Loc.Get("Book.NoSaved");

    public static string Format(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes}:{value.Seconds:00}";
}

public sealed record AudiobookChapterRow(AudiobookChapter Chapter, string Time, string Title);

/// <summary>
/// Drives the standalone audiobook shelf. Nothing plays or saves on its own: every action is a user click, and the shared
/// engine is reached only through <see cref="IAudiobookPlaybackHandoff"/>. Shutdown cancels and drains every operation.
/// </summary>
public sealed partial class AudiobooksViewModel : ObservableObject, IDisposable
{
    private readonly AudiobookShelfService service;
    private readonly IAudiobookPlaybackHandoff? handoff;
    private readonly HashSet<Task> running = [];
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private AudiobookItem? chaptersItem;
    private bool cancelOnSelectionChange;
    private bool cancelOnChapterSelectionChange;
    private bool shutdown;
    private bool disposed;

    [ObservableProperty]
    private string statusMessage = Loc.Get("Book.StatusIntro");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(CanPlay), nameof(CanResume), nameof(CanSave), nameof(CanRemove), nameof(CanShowChapters), nameof(CanPlayChapter))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(CanResume), nameof(CanRemove), nameof(SelectedState), nameof(CanShowChapters))]
    private AudiobookItem? selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayChapter))]
    private AudiobookChapterRow? selectedChapter;

    [ObservableProperty]
    private bool chaptersVisible;

    [ObservableProperty]
    private string chaptersMessage = "";

    public ObservableCollection<AudiobookItem> Items { get; } = [];
    public ObservableCollection<AudiobookChapterRow> Chapters { get; } = [];
    public bool CanShowChapters => !IsBusy && !shutdown && handoff is not null && service.SupportsChapters && SelectedItem is { CanPlay: true };
    public bool CanPlayChapter => !IsBusy && !shutdown && handoff is not null && ChaptersItem is not null && SelectedChapter is not null;
    public bool HasItems => Items.Count > 0;
    public bool CanAdd => !IsBusy && !shutdown;
    public bool CanPlay => !IsBusy && !shutdown && handoff is not null && SelectedItem is { CanPlay: true };
    public bool CanResume => !IsBusy && !shutdown && handoff is not null && SelectedItem is { CanResume: true };
    public bool CanSave => !IsBusy && !shutdown && handoff is not null && Items.Count > 0;
    public bool CanRemove => !IsBusy && !shutdown && SelectedItem is not null;
    private AudiobookItem? ChaptersItem => ReferenceEquals(chaptersItem, SelectedItem) ? chaptersItem : null;
    public string SelectedState => SelectedItem?.StateText ?? "";

    public AudiobooksViewModel(AudiobookShelfService service, IAudiobookPlaybackHandoff? handoff = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        this.service = service; this.handoff = handoff;
        Items.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasItems)); OnPropertyChanged(nameof(CanSave)); };
    }

    public Task LoadAsync() => RunAsync(async token =>
    {
        IReadOnlyList<AudiobookShelfEntry> entries = await service.ListAsync(token);
        token.ThrowIfCancellationRequested();
        bool[] exists = await Task.Run(() => entries.Select(service.Exists).ToArray(), token);
        token.ThrowIfCancellationRequested();
        Replace(entries, exists);
        StatusMessage = entries.Count == 0 ? Loc.Get("Book.Empty")
            : Loc.Plural("Book.OnShelf", entries.Count);
    }, Loc.Get("Book.ReadFailed"));

    /// <summary>Runs the picker under the window's lifetime; cancelling or closing abandons the native dialog without waiting.</summary>
    public Task PickAndAddAsync(Func<Task<Uri?>> pick) => RunAsync(async token =>
    {
        Uri? file = await pick().WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (file is null) { StatusMessage = Loc.Get("Book.NoFile"); return; }
        await AddCoreAsync(file, token);
    }, Loc.Get("Book.AddFailed"));

    public Task AddAsync(Uri file) => RunAsync(token => AddCoreAsync(file, token), Loc.Get("Book.AddFailed"));

    private async Task AddCoreAsync(Uri file, CancellationToken token)
    {
        AudiobookAddResult result = await service.AddAsync(file, token);
        token.ThrowIfCancellationRequested();
        IReadOnlyList<AudiobookShelfEntry> entries = await service.ListAsync(token);
        token.ThrowIfCancellationRequested();
        bool[] exists = await Task.Run(() => entries.Select(service.Exists).ToArray(), token);
        token.ThrowIfCancellationRequested();
        Replace(entries, exists);
        SelectedItem = Items.FirstOrDefault(item => item.Entry.Location == result.Entry.Location);
        StatusMessage = result.Outcome == AudiobookAddOutcome.Added
            ? Loc.Format("Book.Added", result.Entry.Title)
            : Loc.Format("Book.AlreadyOnShelf", result.Entry.Title);
    }

    public Task PlayAsync(AudiobookItem? item, bool resume) => RunAsync(async token =>
    {
        if (item is null) return;
        if (handoff is null) { StatusMessage = Loc.Get("Book.NoPlayback"); return; }
        TimeSpan start = await service.PlayAsync(item.Entry, resume, handoff, token);
        token.ThrowIfCancellationRequested();
        item.FileExists = true;
        StatusMessage = start > TimeSpan.Zero
            ? Loc.Format("Book.PlayingFrom", item.Title, AudiobookItem.Format(start))
            : resume && item.IsFinished ? Loc.Format("Book.FinishedRestart", item.Title) : Loc.Format("Book.PlayingStart", item.Title);
    }, Loc.Get("Book.PlayFailed"), item);

    partial void OnSelectedItemChanged(AudiobookItem? value)
    {
        if (cancelOnSelectionChange) CancelOperation();
        // A chapter list belongs to the book it was read from; any other selection hides it so a stale list can never be played.
        if (!ReferenceEquals(value, chaptersItem)) ClearChapters();
    }

    partial void OnSelectedChapterChanged(AudiobookChapterRow? value)
    {
        if (cancelOnChapterSelectionChange) CancelOperation();
    }

    private void ClearChapters()
    {
        chaptersItem = null;
        Chapters.Clear();
        SelectedChapter = null;
        ChaptersVisible = false;
        ChaptersMessage = "";
        OnPropertyChanged(nameof(CanPlayChapter));
    }

    /// <summary>Reads the selected book's chapters fresh. Nothing plays; a selection change while reading discards the result.</summary>
    public Task ShowChaptersAsync(AudiobookItem? item) => RunAsync(async token =>
    {
        if (item is null) return;
        ClearChapters();
        AudiobookChapterCatalog catalog = await service.ListChaptersAsync(item.Entry, token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(item, SelectedItem)) { StatusMessage = Loc.Get("Book.SelectionChanged"); return; }
        foreach (AudiobookChapter chapter in catalog.Chapters)
            Chapters.Add(new(chapter, AudiobookItem.Format(chapter.Start), chapter.Title));
        chaptersItem = item;
        ChaptersVisible = true;
        ChaptersMessage = catalog.Chapters.Count == 0
            ? Loc.Get("Book.NoChaptersHelp")
            : catalog.Truncated ? Loc.Format("Book.ShowingFirst", catalog.Chapters.Count) : "";
        StatusMessage = catalog.Chapters.Count == 0 ? Loc.Format("Book.NoChapters", item.Title)
            : Loc.Plural("Book.ChapterCount", catalog.Chapters.Count, item.Title);
    }, Loc.Get("Book.ChaptersFailed"), item, cancelOnSelectionChange: true);

    /// <summary>Plays the chosen chapter through the shared engine. Only this explicit call starts playback; choosing a row never does.</summary>
    public Task PlayChapterAsync() => RunAsync(async token =>
    {
        if (handoff is null) { StatusMessage = Loc.Get("Book.NoPlayback"); return; }
        if (ChaptersItem is not { } item || SelectedChapter is not { } row) return;
        cancelOnChapterSelectionChange = true;
        try
        {
            TimeSpan start = await service.PlayChapterAsync(item.Entry, row.Chapter, handoff, token);
            token.ThrowIfCancellationRequested();
            item.FileExists = true;
            StatusMessage = Loc.Format("Book.PlayingChapter", item.Title, row.Title, AudiobookItem.Format(start));
        }
        finally { cancelOnChapterSelectionChange = false; }
    }, Loc.Get("Book.ChapterFailed"), ChaptersItem, cancelOnSelectionChange: true);

    public Task SaveCurrentAsync() => RunAsync(async token =>
    {
        if (handoff is null) { StatusMessage = Loc.Get("Book.NoPlayback"); return; }
        AudiobookSaveResult result = await service.SaveCurrentAsync(handoff, token);
        token.ThrowIfCancellationRequested();
        switch (result.Outcome)
        {
            case AudiobookSaveOutcome.Saved when result.Entry is { } saved:
                AudiobookItem? row = Items.FirstOrDefault(item => item.Entry.Location == saved.Location);
                if (row is not null) row.Entry = saved;
                OnPropertyChanged(nameof(SelectedState)); OnPropertyChanged(nameof(CanResume));
                StatusMessage = Loc.Format("Book.SavedPos", AudiobookItem.Format(result.Position), saved.Title);
                break;
            case AudiobookSaveOutcome.NotOnShelf:
                StatusMessage = Loc.Get("Book.NotOnShelf");
                break;
            default:
                StatusMessage = Loc.Get("Book.NothingPlaying");
                break;
        }
    }, Loc.Get("Book.SaveFailed"));

    public Task RemoveSelectedAsync() => RunAsync(async token =>
    {
        if (SelectedItem is not { } item) return;
        bool removed = await service.RemoveAsync(item.Entry, token);
        token.ThrowIfCancellationRequested();
        Items.Remove(item);
        SelectedItem = null;
        StatusMessage = removed ? Loc.Format("Book.RemovedFromShelf", item.Title) : Loc.Get("Book.AlreadyRemoved");
    }, Loc.Get("Book.RemoveFailed"));

    /// <summary>Cancels the operation in flight (a native dialog wait or a slow metadata read).</summary>
    public void CancelOperation() => operation?.Cancel();

    /// <summary>Cancels and drains all in-flight work. Idempotent; the view model stays inert afterwards.</summary>
    public async Task ShutdownAsync()
    {
        shutdown = true;
        // Signal the accepted operation before asynchronous linked-token callbacks can lag behind a read completion.
        Task operationCancellation = operation?.CancelAsync() ?? Task.CompletedTask;
        await lifetime.CancelAsync();
        await operationCancellation;
        Task[] pending;
        lock (running) pending = [.. running];
        try { await Task.WhenAll(pending); } catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; shutdown = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private Task RunAsync(Func<CancellationToken, Task> work, string failure, AudiobookItem? subject = null, bool cancelOnSelectionChange = false)
    {
        if (shutdown || IsBusy) return Task.CompletedTask;
        IsBusy = true;
        this.cancelOnSelectionChange = cancelOnSelectionChange;
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = source;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (running) running.Add(completion.Task);
        _ = RunCoreAsync(work, failure, subject, source, completion);
        return completion.Task;
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> work, string failure, AudiobookItem? subject, CancellationTokenSource source,
        TaskCompletionSource completion)
    {
        try { await work(source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            if (!shutdown) StatusMessage = Loc.Get("Common.Cancelled");
        }
        catch (Exception exception)
        {
            if (!shutdown)
            {
                if (exception is FileNotFoundException && subject is not null) subject.FileExists = false;
                StatusMessage = $"{failure}: {exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(operation, source))
            {
                operation = null;
                cancelOnSelectionChange = false;
            }
            source.Dispose();
            if (!shutdown) IsBusy = false;
            lock (running) running.Remove(completion.Task);
            completion.SetResult();
        }
    }

    private void Replace(IReadOnlyList<AudiobookShelfEntry> entries, bool[] exists)
    {
        Uri? selected = SelectedItem?.Entry.Location;
        Items.Clear();
        for (int index = 0; index < entries.Count; index++) Items.Add(new(entries[index], exists[index]));
        SelectedItem = selected is null ? null : Items.FirstOrDefault(item => item.Entry.Location == selected);
    }
}
