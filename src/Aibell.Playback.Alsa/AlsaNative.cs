using System.Runtime.InteropServices;

namespace Aibell.Playback.Alsa;

/// <summary>
/// The parts of the ALSA library the player uses. ALSA is reached through the system library
/// (<c>libasound</c>), which every Linux sound stack has.
/// </summary>
internal static partial class AlsaNative
{
    private const string Library = "asound";

    /// <summary>Open the playback device in blocking mode.</summary>
    public const int Stream = 0;

    /// <summary>Interleaved samples.</summary>
    public const int AccessInterleaved = 3;

    /// <summary>The number of channels the player writes.</summary>
    public const int Channels = 2;

    /// <summary>Signed 16 bit little endian samples.</summary>
    public const int FormatS16Le = 2;

    /// <summary>Signed 32 bit floating point samples.</summary>
    public const int FormatFloatLe = 14;

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int snd_pcm_open(out nint handle, string name, int stream, int flags);

    [LibraryImport(Library)]
    public static partial int snd_pcm_close(nint handle);

    [LibraryImport(Library)]
    public static partial int snd_pcm_prepare(nint handle);

    [LibraryImport(Library)]
    public static partial int snd_pcm_drop(nint handle);

    [LibraryImport(Library)]
    public static partial int snd_pcm_drain(nint handle);

    /// <summary>
    /// Writes frames to the device. The count is in frames; interleaved data holds two samples
    /// to a frame.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint snd_pcm_writei(nint handle, nint buffer, nuint size);

    [LibraryImport(Library)]
    public static partial int snd_pcm_recover(nint handle, int error, int silent);

    [LibraryImport(Library)]
    public static partial int snd_pcm_hw_params_malloc(ref nint parameters);

    [LibraryImport(Library)]
    public static partial void snd_pcm_hw_params_free(nint parameters);

    [LibraryImport(Library)]
    public static partial int snd_pcm_hw_params_any(nint handle, nint parameters);

    [LibraryImport(Library)]
    public static partial int snd_pcm_hw_params_set_access(nint handle, nint parameters, int access);

    [LibraryImport(Library)]
    public static partial int snd_pcm_hw_params_set_format(nint handle, nint parameters, int format);

    [LibraryImport(Library)]
    public static partial int snd_pcm_hw_params_test_format(nint handle, nint parameters, int format);

    [LibraryImport(Library)]
    public static partial int snd_pcm_hw_params_set_channels(nint handle, nint parameters, uint channels);

    [LibraryImport(Library)]
    public static partial int snd_pcm_hw_params_set_rate_near(nint handle, nint parameters, ref uint rate, ref int direction);

    [LibraryImport(Library)]
    public static partial int snd_pcm_hw_params_set_period_size_near(nint handle, nint parameters, ref nuint period, ref int direction);

    [LibraryImport(Library)]
    public static partial int snd_pcm_hw_params_set_buffer_size_near(nint handle, nint parameters, ref nuint bufferSize);

    [LibraryImport(Library)]
    public static partial int snd_pcm_hw_params(nint handle, nint parameters);

    [LibraryImport(Library)]
    public static partial int snd_pcm_sw_params_malloc(ref nint parameters);

    [LibraryImport(Library)]
    public static partial void snd_pcm_sw_params_free(nint parameters);

    [LibraryImport(Library)]
    public static partial int snd_pcm_sw_params_current(nint handle, nint parameters);

    [LibraryImport(Library)]
    public static partial int snd_pcm_sw_params_set_avail_min(nint handle, nint parameters, nuint frames);

    [LibraryImport(Library)]
    public static partial int snd_pcm_sw_params_set_start_threshold(nint handle, nint parameters, nuint frames);

    [LibraryImport(Library)]
    public static partial int snd_pcm_sw_params(nint handle, nint parameters);

    /// <summary>The library's error handler.</summary>
    [LibraryImport(Library)]
    private static partial int snd_lib_error_set_handler(nint handler);

    /// <summary>
    /// Does nothing. It is installed to keep the library from writing to the error output: a
    /// desktop application reports problems through its own channels, and the library's messages
    /// would otherwise end up in the test output or the terminal.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void QuietHandler(nint file, int line, nint function, int error, nint format)
    {
    }

    private static int errorsSilenced;

    /// <summary>Keeps the library from writing to the error output. It only takes effect once.</summary>
    public static void SilenceErrors()
    {
        if (Interlocked.Exchange(ref errorsSilenced, 1) != 0) return;
        unsafe
        {
            _ = snd_lib_error_set_handler((nint)(delegate* unmanaged<nint, int, nint, int, nint, void>)&QuietHandler);
        }
    }

    /// <summary>The error message for an ALSA error code.</summary>
    public static string Error(int code) =>
        code >= 0 ? "no error" : Marshal.PtrToStringAnsi(snd_strerror(code)) ?? $"error {code}";

    [LibraryImport(Library)]
    private static partial nint snd_strerror(int code);

    /// <summary>Opens a device and sets it up for the given format.</summary>
    public static nint Open(string device, uint rate, uint channels, uint periodFrames, out string? problem, out int format, out uint actualRate, out uint actualPeriodFrames)
    {
        problem = null;
        actualRate = rate;
        actualPeriodFrames = periodFrames;
        format = FormatS16Le;
        SilenceErrors();
        if (snd_pcm_open(out nint handle, device, Stream, 0) < 0)
        {
            problem = $"The sound device {device} could not be opened.";
            return 0;
        }
        bool succeeded = false;
        try
        {
            nint parameters = 0;
            if (snd_pcm_hw_params_malloc(ref parameters) < 0)
            {
                problem = "The sound device could not be set up.";
                return 0;
            }
            nuint period = periodFrames;
            try
            {
                if (snd_pcm_hw_params_any(handle, parameters) < 0
                    || snd_pcm_hw_params_set_access(handle, parameters, AccessInterleaved) < 0
                    || snd_pcm_hw_params_set_channels(handle, parameters, channels) < 0)
                {
                    problem = "The sound device does not take this format.";
                    return 0;
                }

                // The format the device takes is chosen the way miniaudio chooses it: the float
                // format if the device has it, otherwise sixteen bit, which every device has.
                // Whatever is chosen is what the player writes.
                format = FormatFloatLe;
                if (snd_pcm_hw_params_test_format(handle, parameters, FormatFloatLe) != 0)
                {
                    format = FormatS16Le;
                    if (snd_pcm_hw_params_test_format(handle, parameters, FormatS16Le) != 0)
                    {
                        problem = "The sound device takes neither float nor sixteen bit samples.";
                        return 0;
                    }
                }
                if (snd_pcm_hw_params_set_format(handle, parameters, format) < 0)
                {
                    problem = "The sound device does not take this format.";
                    return 0;
                }

                uint wanted = rate;
                int direction = 0;
                if (snd_pcm_hw_params_set_rate_near(handle, parameters, ref wanted, ref direction) < 0)
                {
                    problem = "The sound device does not take this sample rate.";
                    return 0;
                }
                // The device may answer with another rate; that is the one it runs at.
                actualRate = wanted;
                period = Math.Max(64u, actualRate / 50u);

                direction = 0;
                if (snd_pcm_hw_params_set_period_size_near(handle, parameters, ref period, ref direction) < 0)
                {
                    problem = "The sound device does not take this period size.";
                    return 0;
                }
                actualPeriodFrames = (uint)Math.Clamp(period, 64u, 16384u);

                nuint bufferSize = period * 4;
                _ = snd_pcm_hw_params_set_buffer_size_near(handle, parameters, ref bufferSize);
                if (snd_pcm_hw_params(handle, parameters) < 0)
                {
                    problem = "The sound device rejected the settings.";
                    return 0;
                }
            }
            finally
            {
                snd_pcm_hw_params_free(parameters);
            }

            nint swParams = 0;
            if (snd_pcm_sw_params_malloc(ref swParams) >= 0 && swParams != 0)
            {
                try
                {
                    if (snd_pcm_sw_params_current(handle, swParams) >= 0)
                    {
                        _ = snd_pcm_sw_params_set_avail_min(handle, swParams, period);
                        _ = snd_pcm_sw_params_set_start_threshold(handle, swParams, period);
                        _ = snd_pcm_sw_params(handle, swParams);
                    }
                }
                finally
                {
                    snd_pcm_sw_params_free(swParams);
                }
            }

            succeeded = true;
            return handle;
        }
        finally
        {
            if (!succeeded)
            {
                _ = snd_pcm_close(handle);
            }
        }
    }
}
