using Aibell.Desktop.Services;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private ErrorDialog? errorDialog;

    private void InitializeErrorReporting() => viewModel.ErrorReportAvailable += ShowErrorReport;

    /// <summary>
    /// Opens the error window, or adds to the one already open: a run of unplayable files in a row produces one window with a count,
    /// not one window per file. It is not modal, so the transport keys keep working.
    /// </summary>
    internal void ShowErrorReport(ErrorReport report)
    {
        if (closeInProgress || disposed || !IsVisible) return;
        if (errorDialog is { } open)
        {
            open.Append(report);
            return;
        }
        ErrorDialog dialog = new(report);
        errorDialog = dialog;
        dialog.Closed += (_, _) => { if (ReferenceEquals(errorDialog, dialog)) errorDialog = null; };
        dialog.Show(this);
    }

    internal ErrorDialog? CurrentErrorDialog => errorDialog;
}
