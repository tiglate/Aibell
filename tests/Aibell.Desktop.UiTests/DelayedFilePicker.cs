using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Aibell.Desktop.Services;

namespace Aibell.Desktop.UiTests;

internal sealed class DelayedFilePicker(Uri location) : IDesktopFilePicker
{
    public int Calls { get; private set; }
    public bool Started => Calls > 0;
    public bool Cancelled { get; set; }
    public Exception? Failure { get; set; }
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private async Task<Uri?> PickAsync()
    {
        Calls++; await Release.Task;
        if (Failure is not null) throw Failure;
        return Cancelled ? null : location;
    }
    public Task<Uri?> PickFolderAsync(Window owner, FolderPickerOpenOptions options) => PickAsync();
    public Task<Uri?> PickFileAsync(Window owner, FilePickerOpenOptions options) => PickAsync();
    public async Task<IReadOnlyList<Uri>> PickFilesAsync(Window owner, FilePickerOpenOptions options) => await PickAsync() is { } picked ? [picked] : [];
    public Task<Uri?> PickSaveFileAsync(Window owner, FilePickerSaveOptions options) => PickAsync();
}
