using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Aibell.Desktop.Services;

public sealed class AvaloniaFilePicker : IDesktopFilePicker
{
    public async Task<Uri?> PickFolderAsync(Window owner, FolderPickerOpenOptions options)
    {
        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(options);
        try { return folders.Count > 0 ? folders[0].Path : null; }
        finally { foreach (IStorageFolder folder in folders) folder.Dispose(); }
    }

    public async Task<Uri?> PickFileAsync(Window owner, FilePickerOpenOptions options)
    {
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(options);
        try { return files.Count > 0 ? files[0].Path : null; }
        finally { foreach (IStorageFile file in files) file.Dispose(); }
    }

    public async Task<IReadOnlyList<Uri>> PickFilesAsync(Window owner, FilePickerOpenOptions options)
    {
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(options);
        try { return [.. files.Select(file => file.Path)]; }
        finally { foreach (IStorageFile file in files) file.Dispose(); }
    }

    public async Task<Uri?> PickSaveFileAsync(Window owner, FilePickerSaveOptions options)
    {
        using IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(options);
        return file?.Path;
    }
}
