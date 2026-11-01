using Aibell.Desktop.Localization;
using System.Diagnostics;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private bool CanRunUserAction => !closeInProgress && !disposed;

    private async Task RunUserActionAsync(Func<Task> action)
    {
        if (!CanRunUserAction) return;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Trace.WriteLine($"Desktop action failed: {exception}");
            if (CanRunUserAction) viewModel.StatusMessage = Loc.Format("Main.StatusOperationFailed", exception.Message);
        }
    }

    private Task RunLibraryViewActionAsync(Func<CancellationToken, Task> action) =>
        RunUserActionAsync(() => action(ResetLibraryViewCancellation()));
}
