using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Aibell.Desktop.Services;

public interface IDesktopFilePicker
{
    Task<Uri?> PickFolderAsync(Window owner, FolderPickerOpenOptions options);
    Task<Uri?> PickFileAsync(Window owner, FilePickerOpenOptions options);
    Task<IReadOnlyList<Uri>> PickFilesAsync(Window owner, FilePickerOpenOptions options);
    Task<Uri?> PickSaveFileAsync(Window owner, FilePickerSaveOptions options);
}
