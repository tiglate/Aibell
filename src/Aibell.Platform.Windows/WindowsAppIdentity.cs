using System.Runtime.InteropServices;
using Aibell.Platform.Windows.Native;
using Microsoft.Win32;

namespace Aibell.Platform.Windows;

/// <summary>
/// Names the process for the Windows shell. Without an AppUserModelID an unpackaged app is listed by its executable name
/// (for example in the media card). The ID is set for the process and its display name is registered for the current user
/// under <c>HKCU\Software\Classes\AppUserModelId</c>, which is how the shell resolves an ID that has no Start-menu shortcut.
/// </summary>
public static class WindowsAppIdentity
{
    internal const string RegistryRoot = @"Software\Classes\AppUserModelId";
    private const int MaxAppIdLength = 128;

    /// <summary>
    /// Call before the first window is created. Returns false when the process ID could not be set (non-Windows host or a
    /// shell failure); a failure to write the display name is traced and does not change the result.
    /// </summary>
    public static bool Register(string appId, string displayName) => Register(appId, displayName, RegistryRoot);

    internal static bool Register(string appId, string displayName, string registryRoot)
    {
        Validate(appId, displayName);
        if (!OperatingSystem.IsWindows()) return false;
        int hr = NativeMethods.SetCurrentProcessExplicitAppUserModelID(appId);
        if (hr < 0)
        {
            System.Diagnostics.Trace.WriteLine($"AppUserModelID could not be set: 0x{hr:X8}");
            return false;
        }
        WriteDisplayName(appId, displayName, registryRoot);
        return true;
    }

    /// <summary>The ID currently set for this process, or null when none is set.</summary>
    internal static string? CurrentProcessId()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (NativeMethods.GetCurrentProcessExplicitAppUserModelID(out nint buffer) < 0 || buffer == 0) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { Marshal.FreeCoTaskMem(buffer); }
    }

    internal static void Validate(string appId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrEmpty(appId);
        // The shell rejects IDs over 128 characters, and spaces break the registry/shortcut lookup.
        if (appId.Length > MaxAppIdLength || appId.Any(char.IsWhiteSpace) || appId.Contains('\\'))
            throw new ArgumentException("An AppUserModelID is at most 128 characters with no spaces or backslashes.", nameof(appId));
    }

    private static void WriteDisplayName(string appId, string displayName, string registryRoot)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{registryRoot}\{appId}", writable: true);
            // Written only when different, so a normal start does not touch the registry.
            if (key.GetValue("DisplayName") is not string current || current != displayName) key.SetValue("DisplayName", displayName, RegistryValueKind.String);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            System.Diagnostics.Trace.WriteLine($"AppUserModelID display name was not registered: {exception.Message}");
        }
    }
}
