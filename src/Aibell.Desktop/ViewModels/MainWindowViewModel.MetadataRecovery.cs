using Aibell.Desktop.Localization;
using Aibell.Application.Library;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? recoveryScanCancellation;
    public bool CanReviewMetadataRecovery => metadataEditRecovery is not null;

    public void CancelMetadataRecoveryScan() => recoveryScanCancellation?.Cancel();

    public Task<MetadataEditRecoveryScan> ScanMetadataRecoveryAsync(IReadOnlyCollection<string> folders,
        CancellationToken cancellationToken = default) => BeginMetadataOperation(async () =>
    {
        if (metadataEditRecovery is null) throw new NotSupportedException("Metadata recovery is unavailable.");
        IsBusy = true;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        recoveryScanCancellation = cancellation;
        try
        {
            MetadataEditRecoveryScan scan = await metadataEditRecovery.ScanRecursivelyAsync(folders, cancellation.Token);
            StatusMessage = Loc.Format("Recovery.Found", scan.Pending.Count, scan.Issues.Count);
            return scan;
        }
        finally { recoveryScanCancellation = null; IsBusy = false; }
    });

    public Task DiscardMetadataPreviewAsync(PendingMetadataEdit edit, CancellationToken cancellationToken = default) =>
        BeginMetadataOperation(async () =>
        {
            if (metadataEditRecovery is null) throw new NotSupportedException("Metadata recovery is unavailable.");
            IsBusy = true;
            try
            {
                await metadataEditRecovery.DiscardAsync(edit, cancellationToken);
                StatusMessage = Loc.Get("Recovery.Discarded");
            }
            finally { IsBusy = false; }
        });
}
