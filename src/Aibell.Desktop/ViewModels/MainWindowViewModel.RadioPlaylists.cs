using Aibell.Desktop.Localization;
using Aibell.Application.Radio;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? radioPlaylistPreparation;
    public bool CanImportRadioPlaylist => radioPlaylistFiles is not null && CanManageRadio;
    public void CancelRadioPlaylistPreparation() => radioPlaylistPreparation?.Cancel();

    public Task<RadioPlaylistImportPreview> PrepareRadioPlaylistAsync(Uri source, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(async () =>
        {
            if (!CanImportRadioPlaylist) throw new InvalidOperationException("Radio import is unavailable. Wait for the current operation to finish.");
            using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            radioPlaylistPreparation = operation;
            IsBusy = true;
            try { return await radioPlaylistFiles!.PrepareAsync(source, operation.Token); }
            finally { radioPlaylistPreparation = null; IsBusy = false; }
        });

    public Task<RadioStationImportResult> ImportRadioPlaylistAsync(RadioPlaylistImportPreview preview, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(async () =>
        {
            if (!CanImportRadioPlaylist) throw new InvalidOperationException("Radio import is unavailable. Wait for the current operation to finish.");
            IsBusy = true;
            try
            {
                RadioStationImportResult result = await radioPlaylistFiles!.ImportAsync(preview, cancellationToken);
                try
                {
                    await RefreshRadioAsync(CancellationToken.None);
                    StatusMessage = Loc.Format("RadioFile.Imported", result.Imported, preview.Issues.Count, preview.Duplicates + result.Duplicates);
                }
                catch (Exception exception)
                { StatusMessage = Loc.Format("RadioFile.ImportedNoRefresh", result.Imported, exception.Message); }
                return result;
            }
            finally { IsBusy = false; }
        });
}
