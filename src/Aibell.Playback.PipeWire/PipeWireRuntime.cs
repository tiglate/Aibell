using System.Runtime.InteropServices;

namespace Aibell.Playback.PipeWire;

/// <summary>
/// Whether the managed PipeWire output can be used on this machine: there has to be a running
/// daemon to talk to. The check is cheap and does not open a connection.
/// </summary>
public static class PipeWireRuntime
{
    /// <summary>The name of the daemon's socket inside the runtime directory.</summary>
    private const string SocketName = "pipewire-0";

    /// <summary>True when a PipeWire daemon is running and can be talked to.</summary>
    public static bool IsAvailable(out string? problem)
    {
        if (!OperatingSystem.IsLinux())
        {
            problem = "PipeWire is only used on Linux.";
            return false;
        }

        string? path = SocketPath();
        if (path is null)
        {
            problem = "There is no runtime directory, so there is no PipeWire socket.";
            return false;
        }
        if (!File.Exists(path))
        {
            problem = "No PipeWire daemon is running.";
            return false;
        }
        if (!NativeLibrary.TryLoad(NativeMethods.PipeWire, out nint library))
        {
            problem = "The PipeWire library (libpipewire-0.3.so.0) is not installed.";
            return false;
        }
        NativeLibrary.Free(library);
        problem = null;
        return true;
    }

    /// <summary>The path of the daemon's socket, or null when there is no runtime directory.</summary>
    public static string? SocketPath()
    {
        string? runtimeDir = Environment.GetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR")
            ?? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtimeDir)) return null;
        return Path.Combine(runtimeDir, SocketName);
    }
}
