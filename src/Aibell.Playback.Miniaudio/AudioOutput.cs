using System.Runtime.InteropServices;

namespace Aibell.Playback.Miniaudio;

/// <summary>The playback device. The callback runs on miniaudio's audio thread and must not block or allocate.</summary>
internal sealed unsafe class AudioOutput : IDisposable
{
    public delegate void RenderCallback(Span<float> output);

    private readonly RenderCallback render;
    private readonly GCHandle handle;
    private nint output;
    private bool running;

    public AudioOutput(RenderCallback render, bool useNullBackend)
    {
        this.render = render;
        handle = GCHandle.Alloc(this);
        output = NativeAudio.aibell_output_open(0, useNullBackend ? 1 : 0, &OnPull, GCHandle.ToIntPtr(handle), out int error);
        if (output == 0)
        {
            handle.Free();
            throw new InvalidOperationException($"No audio output device could be opened (code {error}).");
        }
        SampleRate = NativeAudio.aibell_output_sample_rate(output);
    }

    public uint SampleRate { get; }

    public void Start()
    {
        if (running) return;
        int result = NativeAudio.aibell_output_start(output);
        if (result != 0) throw new InvalidOperationException($"The audio output device could not be started (code {result}).");
        running = true;
    }

    public void Stop()
    {
        if (!running) return;
        running = false;
        _ = NativeAudio.aibell_output_stop(output);
    }

    public void Dispose()
    {
        nint native = Interlocked.Exchange(ref output, 0);
        if (native == 0) return;
        running = false;
        NativeAudio.aibell_output_close(native);
        handle.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnPull(nint user, float* buffer, uint frames)
    {
        Span<float> span = new(buffer, (int)frames * NativeAudio.Channels);
        try
        {
            ((AudioOutput)GCHandle.FromIntPtr(user).Target!).render(span);
        }
        catch (Exception)
        {
            span.Clear();
        }
    }
}
