using System.Runtime.InteropServices;

namespace Aibell.Playback.Miniaudio;

/// <summary>The C shim over miniaudio (native/aibell-audio). Output and decoding are always interleaved stereo float.</summary>
internal static unsafe partial class NativeAudio
{
    public const string Library = "aibell_audio";
    public const int Channels = 2;
    private const int ExpectedAbiVersion = 1;

    [LibraryImport(Library)]
    private static partial int aibell_abi_version();

    [LibraryImport(Library)]
    public static partial nint aibell_output_open(uint sampleRate, int useNull,
        delegate* unmanaged[Cdecl]<nint, float*, uint, void> pull, nint user, out int error);

    [LibraryImport(Library)]
    public static partial uint aibell_output_sample_rate(nint output);

    [LibraryImport(Library)]
    public static partial int aibell_output_start(nint output);

    [LibraryImport(Library)]
    public static partial int aibell_output_stop(nint output);

    [LibraryImport(Library)]
    public static partial void aibell_output_close(nint output);

    [LibraryImport(Library)]
    public static partial nint aibell_decoder_open(delegate* unmanaged[Cdecl]<nint, byte*, ulong, long> read,
        delegate* unmanaged[Cdecl]<nint, long, int, int> seek, nint user, uint sampleRate, out int error);

    [LibraryImport(Library)]
    public static partial long aibell_decoder_read(nint decoder, float* output, uint frames);

    [LibraryImport(Library)]
    public static partial int aibell_decoder_seek(nint decoder, ulong frame);

    [LibraryImport(Library)]
    public static partial long aibell_decoder_length(nint decoder);

    [LibraryImport(Library)]
    public static partial void aibell_decoder_close(nint decoder);

    /// <summary>True when the native library loads and speaks the ABI this code was written for.</summary>
    public static bool TryLoad(out string? problem)
    {
        try
        {
            int version = aibell_abi_version();
            problem = version == ExpectedAbiVersion ? null : $"The Aibell audio library has ABI {version}; {ExpectedAbiVersion} is required.";
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            problem = "The Aibell audio library could not be loaded: " + exception.Message;
        }
        return problem is null;
    }
}
