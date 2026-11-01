using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Aibell.Application.Playback;
using Aibell.Application.Settings;
using Aibell.Desktop.Localization;
using Aibell.Desktop.Services;
using Aibell.Domain.Media;
using Avalonia.Threading;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private ApplicationErrorReporter? errorReporter;
    private SessionMonitor? sessionMonitor;
    private string? lastReportedFault;

    /// <summary>Raised on the UI thread for a problem that should open the error window (with its Details button).</summary>
    public event Action<ErrorReport>? ErrorReportAvailable;

    public void ConfigureErrorReporting(ApplicationErrorReporter reporter, SessionMonitor? session)
    {
        errorReporter = reporter;
        sessionMonitor = session;
        reporter.ReportedDetailed += report =>
        {
            if (report.ShowDialog) Dispatcher.UIThread.Post(() => ErrorReportAvailable?.Invoke(report));
        };
    }

    /// <summary>Tells the user (and the log) that the previous run ended without a clean shutdown, and what it was doing.</summary>
    public void ReportPreviousSession(PreviousSessionCrash crash)
    {
        ArgumentNullException.ThrowIfNull(crash);
        if (errorReporter is null) return;
        string message = Loc.Get("Crash.Message");
        StringBuilder details = new();
        details.AppendLine(CultureInfo.InvariantCulture, $"Session started: {crash.Started.ToString("u", CultureInfo.InvariantCulture)}");
        if (crash.Activity is { Kind: "playing" } playing)
        {
            string player = playing.Engine == nameof(PlaybackEngineKind.Vlc) ? Loc.Get("Crash.EngineVlc") : Loc.Get("Crash.EngineBuiltIn");
            message += " " + Loc.Format("Crash.WhilePlaying", playing.Path is null ? "?" : System.IO.Path.GetFileName(playing.Path), player);
            if (playing.Engine != nameof(PlaybackEngineKind.Vlc)) message += " " + Loc.Get("Playback.TryVlc");
            details.AppendLine(CultureInfo.InvariantCulture, $"Last activity: playing a track with the {playing.Engine ?? "unknown"} player at {crash.ActivityAt?.ToString("u", CultureInfo.InvariantCulture) ?? "an unknown time"}");
            details.AppendLine(CultureInfo.InvariantCulture, $"Track: {playing.Path}");
        }
        else details.AppendLine("Last activity: none recorded");
        details.AppendLine("The process ended without a clean shutdown: a crash inside native code (a decoder or the audio device), the process being killed, or a power loss. Managed code cannot log a native crash, so this note is all that was saved.");
        details.AppendLine(Environment.NewLine + DescribeEnvironment());
        errorReporter.Report(Loc.Get("Crash.Title"), message, details.ToString(), "previous-session");
    }

    private void NoteSessionActivity(Track track) =>
        sessionMonitor?.SetActivity(new SessionActivity("playing", track.Location.IsFile ? track.Location.LocalPath : track.Location.AbsoluteUri, activePlaybackEngine.ToString()));

    private void ClearSessionActivity() => sessionMonitor?.SetActivity(null);

    /// <summary>Logs a playback failure with its context and offers it in the error window. Repeats of the same fault are folded into one.</summary>
    private void ReportPlaybackFailure(Track? track, string message, Exception? exception, string? engineDetails)
    {
        StatusMessage = message;
        string key = $"{track?.Id}|{message}";
        if (key == lastReportedFault || errorReporter is null) return;
        lastReportedFault = key;
        string context = DescribePlaybackContext(track);
        string hint = activePlaybackEngine != PlaybackEngineKind.Vlc ? " " + Loc.Get("Playback.TryVlc") : "";
        string title = Loc.Get("Playback.FailedTitle");
        if (exception is not null) errorReporter.Report(exception, "playback", context, message + hint);
        else errorReporter.Report(title, message + hint, context + Environment.NewLine + Environment.NewLine + (engineDetails ?? message), "playback");
    }

    private string DescribePlaybackContext(Track? track)
    {
        StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture, $"Player: {activePlaybackEngine switch { PlaybackEngineKind.Vlc => "VLC", PlaybackEngineKind.Managed => "miniaudio (Managed)", PlaybackEngineKind.PipeWire => "PipeWire (Managed)", PlaybackEngineKind.Alsa => "ALSA (Managed)", _ => "miniaudio" }}");
        if (track is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Track: {track.Title} — {track.Artist}");
            string location = track.Location.IsFile ? track.Location.LocalPath : track.Location.AbsoluteUri;
            text.AppendLine(CultureInfo.InvariantCulture, $"Location: {location}");
            if (track.Location.IsFile)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Format: {System.IO.Path.GetExtension(location).TrimStart('.').ToLowerInvariant()}");
                try { text.AppendLine(CultureInfo.InvariantCulture, $"Size: {new FileInfo(location).Length.ToString("N0", CultureInfo.InvariantCulture)} bytes"); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { text.AppendLine("Size: unavailable"); }
            }
        }
        text.Append(DescribeEnvironment());
        return text.ToString();
    }

    private static string DescribeEnvironment() =>
        $"Aibell {typeof(MainWindowViewModel).Assembly.GetName().Version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}" +
        Environment.NewLine + $"Time: {DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture)}";
}
