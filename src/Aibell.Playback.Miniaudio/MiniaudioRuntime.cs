namespace Aibell.Playback.Miniaudio;

public static class MiniaudioRuntime
{
    /// <summary>True when the bundled audio library loads on this machine. Used to fall back to VLC when it does not.</summary>
    public static bool IsAvailable(out string? problem) => NativeAudio.TryLoad(out problem);
}
