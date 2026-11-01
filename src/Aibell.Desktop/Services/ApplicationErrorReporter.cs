using Aibell.Desktop.Localization;
using System.Text;
using Avalonia.Threading;

namespace Aibell.Desktop.Services;

/// <summary>
/// Application-wide error presentation: anything that escapes a handler is written to a local log (rotated at 1 MB), surfaced
/// once as a short status message, and, for UI-thread exceptions, kept from taking the application down. Logging itself never throws.
/// </summary>
public sealed class ApplicationErrorReporter
{
    public const long MaximumLogBytes = 1024 * 1024;
    private readonly object gate = new();
    private readonly TimeProvider time;
    private bool attached;

    public ApplicationErrorReporter(string logDirectory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        LogDirectory = logDirectory;
        this.time = time ?? TimeProvider.System;
    }

    public string LogDirectory { get; }
    public string LogPath => Path.Combine(LogDirectory, "errors.log");

    /// <summary>Raised with a one-line, user-readable summary. May be raised from any thread.</summary>
    public event Action<string>? Reported;

    /// <summary>Raised with the full report, for the error window. May be raised from any thread.</summary>
    public event Action<ErrorReport>? ReportedDetailed;

    /// <summary>Logs an exception. <paramref name="context"/> (what was being done, the file, the player) is logged and shown with it.</summary>
    public ErrorReport Report(Exception exception, string source, string? context = null, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string summary = Summarize(exception);
        string details = (string.IsNullOrWhiteSpace(context) ? "" : context.TrimEnd() + Environment.NewLine + Environment.NewLine) + exception;
        return Announce(new ErrorReport(Loc.Get("ErrorDialog.Title"), message ?? summary, details + Environment.NewLine + Environment.NewLine + Loc.Format("ErrorDialog.LogLocation", LogPath),
            source, ShowDialog: source is not ("task" or "appdomain")), source, details, message ?? Loc.Format("Error.Reported", summary, LogPath));
    }

    /// <summary>Logs a problem that has no exception of its own (a fault the player reported, or the previous session's crash).</summary>
    public ErrorReport Report(string title, string message, string details, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return Announce(new ErrorReport(title, message, details + Environment.NewLine + Environment.NewLine + Loc.Format("ErrorDialog.LogLocation", LogPath), source),
            source, details, message);
    }

    private ErrorReport Announce(ErrorReport report, string source, string logged, string status)
    {
        try { Append(source, report.Message, logged); }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        Reported?.Invoke(status);
        ReportedDetailed?.Invoke(report);
        return report;
    }

    /// <summary>Hooks the process-wide and UI-thread unhandled-exception events. Call once, on the UI thread.</summary>
    public void Attach()
    {
        if (attached) return;
        attached = true;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) Report(exception, args.IsTerminating ? "fatal" : "appdomain");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Report(args.Exception, "task");
            args.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            Report(args.Exception, "ui");
            // Keep the window usable after an ordinary failure; memory exhaustion is not something to continue from.
            args.Handled = args.Exception is not OutOfMemoryException;
        };
    }

    private static string Summarize(Exception exception)
    {
        string message = exception.Message.ReplaceLineEndings(" ").Trim();
        if (message.Length > 160) message = message[..160] + "…";
        return $"{exception.GetType().Name}: {message}";
    }

    private void Append(string source, string message, string details)
    {
        lock (gate)
        {
            Directory.CreateDirectory(LogDirectory);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaximumLogBytes)
                File.Move(LogPath, Path.Combine(LogDirectory, "errors.old.log"), overwrite: true);
            StringBuilder entry = new();
            entry.Append(time.GetUtcNow().ToString("u", System.Globalization.CultureInfo.InvariantCulture)).Append(" [").Append(source).AppendLine("]");
            entry.AppendLine(message);
            entry.AppendLine(details);
            entry.AppendLine();
            File.AppendAllText(LogPath, entry.ToString(), Encoding.UTF8);
        }
    }
}
