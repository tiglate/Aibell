using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AibellTools;

/// <summary>
/// Exercises a self-contained Linux publish under Xvfb (start it with <c>xvfb-run -a</c>) with an isolated profile: startup, the
/// Preferences shortcut, native close events, library initialization and, optionally, MPRIS commands on a private D-Bus.
/// Needs xdotool, and dbus-daemon plus dbus-send for --media-session. The window titles it looks for are English: set AIBELL_LANGUAGE=en.
/// </summary>
internal static partial class LinuxSmoke
{
    public static int Run(string[] args)
    {
        Options options = new(args, new HashSet<string> { "--profile-override", "--media-session", "--keyboard-close", "--sigterm" }, new HashSet<string>());
        string binary = Path.GetFullPath(options.Positional.FirstOrDefault() ?? Path.Combine(RepositoryRoot.Find(), "artifacts/linux-x64/Aibell.Desktop"));
        if (!File.Exists(binary)) throw new ToolException($"Publish first: missing {binary}");
        bool profileOverride = options.Has("--profile-override"), media = options.Has("--media-session"), keyboard = options.Has("--keyboard-close"), sigterm = options.Has("--sigterm");
        string root = Directory.CreateTempSubdirectory("aibell-publish-smoke-").FullName;
        Process? bus = null;
        Process? application = null;
        string log = Path.Combine(root, "application.log");
        try
        {
            string? address = media ? StartBus(out bus) : null;
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            ProcessStartInfo start = new(binary) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["XDG_DATA_HOME"] = Path.Combine(root, "data");
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(root, "config");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(root, "cache");
            start.Environment.Remove("AIBELL_DATA_DIRECTORY");
            if (profileOverride) start.Environment["AIBELL_DATA_DIRECTORY"] = Path.Combine(root, "profile-override");
            // Never advertise a smoke-test player on the user's real session bus.
            start.Environment["DBUS_SESSION_BUS_ADDRESS"] = address ?? $"unix:path={Path.Combine(root, "unavailable-bus")}";
            start.Environment["DOTNET_ROOT"] = Path.Combine(root, "no-system-dotnet");
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            application = Process.Start(start) ?? throw new ToolException("Could not start the published application.");
            using StreamWriter output = new(log) { AutoFlush = true };
            application.OutputDataReceived += (_, e) => { lock (output) output.WriteLine(e.Data); };
            application.ErrorDataReceived += (_, e) => { lock (output) output.WriteLine(e.Data); };
            application.BeginOutputReadLine();
            application.BeginErrorReadLine();

            long window = WindowFor(application.Id, "^Aibell$");
            if (media) ExerciseMediaSession(start.Environment, application.Id);
            XdoTool("windowfocus", "--sync", window.ToString(System.Globalization.CultureInfo.InvariantCulture));
            XdoTool("key", "ctrl+comma");
            long preferences = WindowFor(application.Id, "^Preferences$");
            if (preferences == window) throw new ToolException("Preferences lookup returned the main window");
            // Exercise actual native close events for the owned dialog and the main window.
            CloseWindow(preferences);
            Thread.Sleep(200);
            if (sigterm)
            {
                // What `kill`, a service manager or Ctrl+C in a terminal does: the application must close itself, not ignore the signal.
                (int killed, _, string killError) = RepositoryRoot.Run("kill", ["-TERM", application.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                if (killed != 0) throw new ToolException($"kill -TERM failed: {killError.Trim()}");
            }
            else if (keyboard)
            {
                XdoTool("windowfocus", "--sync", window.ToString(System.Globalization.CultureInfo.InvariantCulture));
                XdoTool("key", "--clearmodifiers", "ctrl+f");
                XdoTool("type", "--clearmodifiers", "--delay", "10", "isolated keyboard smoke query");
                Thread.Sleep(350);
                XdoTool("key", "--clearmodifiers", "Escape");
                Thread.Sleep(350);
                XdoTool("key", "--clearmodifiers", "ctrl+q");
            }
            else
            {
                CloseWindow(window);
            }
            if (!application.WaitForExit(TimeSpan.FromSeconds(sigterm ? 15 : 30))) throw new ToolException(sigterm ? "The published application ignored SIGTERM." : "The published application did not exit.");
            if (application.ExitCode != 0) throw new ToolException($"Published application exited {application.ExitCode}");
            string profile = profileOverride ? Path.Combine(root, "profile-override") : Path.Combine(root, "data", "Aibell");
            if (File.Exists(Path.Combine(profile, "logs", "session.json"))) throw new ToolException("The session marker was left behind: the shutdown was not clean");
            if (profileOverride && Directory.Exists(Path.Combine(root, "data", "Aibell"))) throw new ToolException("The explicit profile also created an unexpected default library");
            using (SqliteConnection connection = new($"Data Source={Path.Combine(profile, "library.db")};Mode=ReadOnly"))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version";
                long version = (long)command.ExecuteScalar()!;
                if (version < 13) throw new ToolException($"Library did not initialize: schema {version}");
            }
            if (media)
            {
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "settings.json")));
                JsonElement settings = saved.RootElement;
                if (settings.GetProperty("Volume").GetDouble() != 0.37 || !settings.GetProperty("ShuffleEnabled").GetBoolean() || settings.GetProperty("RepeatMode").GetInt32() != 1)
                    throw new ToolException($"Media-control preferences were not persisted: {settings}");
                Console.WriteLine("Private MPRIS: discovery, properties, volume/shuffle/repeat commands, Raise and preference persistence passed.");
            }
            Console.WriteLine($"Self-contained Linux publish: startup, Preferences shortcut, {(sigterm ? "SIGTERM" : keyboard ? "Ctrl+Q" : "WM_DELETE_WINDOW")} shutdown and isolated library initialization passed.");
            return 0;
        }
        catch
        {
            if (application is { HasExited: false }) application.Kill(entireProcessTree: true);
            if (File.Exists(log)) Console.Error.WriteLine(File.ReadAllText(log));
            throw;
        }
        finally
        {
            if (bus is { HasExited: false }) bus.Kill();
            bus?.Dispose();
            application?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void XdoTool(params string[] arguments)
    {
        (int code, _, string error) = RepositoryRoot.Run("xdotool", arguments);
        if (code != 0) throw new ToolException($"xdotool {arguments[0]} failed: {error.Trim()}");
    }

    private static long WindowFor(int pid, string title, int seconds = 20)
    {
        DateTime expires = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < expires)
        {
            (int code, string output, _) = RepositoryRoot.Run("xdotool", ["search", "--all", "--onlyvisible", "--pid", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--name", title]);
            if (code == 0 && output.Trim().Length > 0) return long.Parse(output.Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
            Thread.Sleep(100);
        }
        throw new ToolException($"No visible '{title}' window from process {pid}");
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport("libX11.so.6")]
    private static unsafe partial int XSendEvent(nint display, nint window, int propagate, nint mask, byte* @event);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    // Delivers WM_DELETE_WINDOW directly; a window manager is unnecessary in Xvfb. XClientMessageEvent on 64-bit: type@0, window@32,
    // message_type@40, format@48, data.l[0]@56, inside a 192-byte XEvent.
    private static unsafe void CloseWindow(long window)
    {
        nint display = XOpenDisplay(0);
        if (display == 0) throw new ToolException("An X11 display is required; use xvfb-run -a.");
        try
        {
            byte* buffer = stackalloc byte[192];
            new Span<byte>(buffer, 192).Clear();
            *(int*)buffer = 33; // ClientMessage
            *(nint*)(buffer + 24) = display;
            *(nint*)(buffer + 32) = (nint)window;
            *(nint*)(buffer + 40) = XInternAtom(display, "WM_PROTOCOLS", 0);
            *(int*)(buffer + 48) = 32;
            *(nint*)(buffer + 56) = XInternAtom(display, "WM_DELETE_WINDOW", 0);
            if (XSendEvent(display, (nint)window, 0, 0, buffer) == 0) throw new ToolException("Could not send the native close request");
            _ = XFlush(display);
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    private static string StartBus(out Process process)
    {
        ProcessStartInfo start = new("dbus-daemon", ["--session", "--nofork", "--print-address=1"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        process = Process.Start(start) ?? throw new ToolException("The private media-control bus did not start");
        Task<string?> line = process.StandardOutput.ReadLineAsync();
        if (!line.Wait(TimeSpan.FromSeconds(10))) throw new ToolException("The private media-control bus did not start");
        string address = line.Result?.Trim() ?? "";
        return address.Length == 0 ? throw new ToolException("The private media-control bus returned no address") : address;
    }

    private static string MediaCall(IDictionary<string, string?> environment, int pid, string method, params string[] arguments)
    {
        (int code, string output, string error) = RepositoryRoot.Run("dbus-send",
            ["--session", "--print-reply", "--reply-timeout=3000", $"--dest=org.mpris.MediaPlayer2.aibell.instance{pid}", "/org/mpris/MediaPlayer2", method, .. arguments],
            environment: environment, timeout: TimeSpan.FromSeconds(5));
        return code == 0 ? output : throw new ToolException($"Media control {method} failed: {error.Trim()}");
    }

    private static void ExerciseMediaSession(IDictionary<string, string?> environment, int pid)
    {
        const string Player = "string:org.mpris.MediaPlayer2.Player";
        string result;
        DateTime expires = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                result = MediaCall(environment, pid, "org.freedesktop.DBus.Properties.GetAll", Player);
                break;
            }
            catch (ToolException) when (DateTime.UtcNow < expires)
            {
                Thread.Sleep(100);
            }
        }
        if (!result.Contains("string \"Stopped\"", StringComparison.Ordinal) || !result.Contains("string \"Metadata\"", StringComparison.Ordinal))
            throw new ToolException("Published media session did not report an idle player");
        foreach ((string name, string value) in new[] { ("Volume", "variant:double:0.37"), ("Shuffle", "variant:boolean:true"), ("LoopStatus", "variant:string:Playlist") })
            MediaCall(environment, pid, "org.freedesktop.DBus.Properties.Set", Player, $"string:{name}", value);
        // Allow queued UI notifications to publish, then verify through the bus.
        expires = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            string volume = MediaCall(environment, pid, "org.freedesktop.DBus.Properties.Get", Player, "string:Volume");
            if (VolumeReply().IsMatch(volume)) break;
            if (DateTime.UtcNow >= expires) throw new ToolException($"Media volume did not update: {volume}");
            Thread.Sleep(100);
        }
        MediaCall(environment, pid, "org.mpris.MediaPlayer2.Raise");
    }

    [GeneratedRegex(@"double\s+0\.37(?:\s|$)")]
    private static partial Regex VolumeReply();
}
