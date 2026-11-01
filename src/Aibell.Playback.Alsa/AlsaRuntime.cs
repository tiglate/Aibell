namespace Aibell.Playback.Alsa;

/// <summary>
/// Whether the ALSA output can be used on this machine: there has to be a sound device to open.
/// The check opens and closes the default device, which is the only way to be sure.
/// </summary>
public static class AlsaRuntime
{
    /// <summary>True when a sound device can be opened.</summary>
    public static bool IsAvailable(out string? problem)
    {
        if (!OperatingSystem.IsLinux())
        {
            problem = "ALSA is only used on Linux.";
            return false;
        }
        try
        {
            using AlsaOutput output = new(_ => { });
            problem = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            problem = "The ALSA sound library could not be used: " + exception.Message;
            return false;
        }
    }
}
