using System.Diagnostics;
using System.Text.Json;

namespace Aibell.Desktop.Services;

/// <summary>What Aibell was doing when a session ended without shutting down: written while it runs, read on the next start.</summary>
public sealed record SessionActivity(string Kind, string? Path, string? Engine);

public sealed record PreviousSessionCrash(DateTimeOffset Started, DateTimeOffset? ActivityAt, SessionActivity? Activity);

/// <summary>
/// Detects that the previous run ended without a clean shutdown. A crash inside native code (a decoder, the audio device) cannot be
/// caught by managed code and leaves nothing behind, so the running session keeps a small marker file with its last activity
/// (the track being played and the player used). A clean shutdown deletes it; if the next start finds it, the process that wrote it
/// is gone and the session crashed, was killed or lost power. The marker is replaced atomically and holds no media content.
/// </summary>
public sealed class SessionMonitor
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string path;
    private readonly TimeProvider time;
    private readonly int processId;
    private DateTimeOffset started;
    private SessionActivity? activity;
    private DateTimeOffset? activityAt;
    private bool running;

    public SessionMonitor(string logDirectory, TimeProvider? time = null, int? processId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        path = Path.Combine(logDirectory, "session.json");
        this.time = time ?? TimeProvider.System;
        this.processId = processId ?? Environment.ProcessId;
    }

    public string MarkerPath => path;

    /// <summary>The previous session, when <see cref="Begin"/> found that it did not end cleanly.</summary>
    public PreviousSessionCrash? PreviousCrash { get; private set; }

    /// <summary>Starts a session. Returns the previous one when it did not end cleanly.</summary>
    public PreviousSessionCrash? Begin()
    {
        PreviousSessionCrash? crashed = null;
        lock (gate)
        {
            try
            {
                if (File.Exists(path)) crashed = ReadPrevious();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable marker still means the last run did not clean up after itself.
                crashed = new PreviousSessionCrash(time.GetUtcNow(), null, null);
            }
            PreviousCrash = crashed;
            started = time.GetUtcNow();
            activity = null;
            activityAt = null;
            running = true;
            Write();
        }
        return crashed;
    }

    /// <summary>Records what is about to happen (for example <c>playing</c> a track). Never throws.</summary>
    public void SetActivity(SessionActivity? next)
    {
        lock (gate)
        {
            if (!running) return;
            activity = next;
            activityAt = next is null ? null : time.GetUtcNow();
            Write();
        }
    }

    /// <summary>Marks a clean shutdown by removing the marker.</summary>
    public void End()
    {
        lock (gate)
        {
            if (!running) return;
            running = false;
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private PreviousSessionCrash? ReadPrevious()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        // Another running Aibell (a second window, or two profiles sharing a folder) owns the marker: that is not a crash.
        if (root.TryGetProperty("pid", out JsonElement pid) && pid.TryGetInt32(out int other) && other != processId && IsAibell(other)) return null;
        DateTimeOffset begun = root.TryGetProperty("started", out JsonElement s) && s.TryGetDateTimeOffset(out DateTimeOffset parsed) ? parsed : time.GetUtcNow();
        SessionActivity? last = null;
        DateTimeOffset? lastAt = null;
        if (root.TryGetProperty("activity", out JsonElement a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("kind", out JsonElement kind))
        {
            last = new SessionActivity(kind.GetString() ?? "", Text(a, "path"), Text(a, "engine"));
            lastAt = root.TryGetProperty("activityAt", out JsonElement at) && at.TryGetDateTimeOffset(out DateTimeOffset when) ? when : null;
        }
        return new PreviousSessionCrash(begun, lastAt, last);
    }

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsAibell(int pid)
    {
        try
        {
            using Process other = Process.GetProcessById(pid);
            using Process self = Process.GetCurrentProcess();
            return !other.HasExited && string.Equals(other.ProcessName, self.ProcessName, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { pid = processId, started, activity, activityAt }, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without a marker a crash goes unreported, but playback must not depend on a writable log folder.
        }
    }
}
