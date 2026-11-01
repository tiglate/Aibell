using System.Diagnostics;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly HashSet<Task> libraryOperations = [];
    private readonly object libraryOperationGate = new();
    private bool libraryShutdown;

    public void BeginLibraryShutdown() => libraryShutdown = true;

    private Task RunLibraryOperation(Func<Task> operation)
    {
        if (libraryShutdown) return Task.CompletedTask;
        Task task = operation();
        lock (libraryOperationGate) libraryOperations.Add(task);
        _ = ObserveLibraryOperationAsync(task);
        return task;
    }

    private async Task ObserveLibraryOperationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Trace.WriteLine($"Library operation ended: {exception.Message}"); }
        finally { lock (libraryOperationGate) libraryOperations.Remove(task); }
    }

    public async Task WaitForLibraryOperationsAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (libraryOperationGate) pending = libraryOperations.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending); }
            catch (Exception exception) { Trace.WriteLine($"Library shutdown drain: {exception.Message}"); }
        }
    }
}
