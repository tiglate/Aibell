using System.Runtime.InteropServices;

using Aibell.Playback.Core;

namespace Aibell.Playback.Miniaudio;

/// <summary>miniaudio's WAV, FLAC, MP3 and Vorbis decoders reading from a .NET <see cref="Stream"/>.</summary>
internal sealed unsafe class NativeDecoderSource : IPcmSource
{
    private readonly Stream stream;
    private readonly GCHandle handle;
    private nint decoder;

    private NativeDecoderSource(Stream stream)
    {
        this.stream = stream;
        handle = GCHandle.Alloc(this);
    }

    /// <summary>Opens a decoder that owns <paramref name="stream"/>; returns null (and disposes the stream) when the format is not recognized.</summary>
    public static NativeDecoderSource? TryOpen(Stream stream, uint sampleRate, out string? failure)
    {
        NativeDecoderSource source = new(stream);
        source.decoder = NativeAudio.aibell_decoder_open(&OnRead, &OnSeek, GCHandle.ToIntPtr(source.handle), sampleRate, out int error);
        if (source.decoder != 0)
        {
            failure = null;
            return source;
        }
        failure = $"The built-in decoder does not recognize this format (code {error}).";
        source.Dispose();
        return null;
    }

    public int Read(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(decoder == 0, this);
        fixed (float* buffer = destination)
        {
            long read = NativeAudio.aibell_decoder_read(decoder, buffer, (uint)frames);
            if (read < 0) throw new InvalidDataException($"The audio data is damaged (code {read}).");
            return (int)read;
        }
    }

    public bool Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(decoder == 0, this);
        return NativeAudio.aibell_decoder_seek(decoder, (ulong)Math.Max(0, frame)) == 0;
    }

    public void Dispose()
    {
        nint native = Interlocked.Exchange(ref decoder, 0);
        if (native != 0) NativeAudio.aibell_decoder_close(native);
        if (handle.IsAllocated) handle.Free();
        stream.Dispose();
    }

    // Exceptions must never cross into native code, and a short read would look like the end of the stream to the decoder.
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static long OnRead(nint user, byte* buffer, ulong bytes)
    {
        try
        {
            NativeDecoderSource self = (NativeDecoderSource)GCHandle.FromIntPtr(user).Target!;
            int total = 0;
            while ((ulong)total < bytes)
            {
                int read = self.stream.Read(new Span<byte>(buffer + total, (int)Math.Min(bytes - (ulong)total, int.MaxValue)));
                if (read <= 0) break;
                total += read;
            }
            return total;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnSeek(nint user, long offset, int origin)
    {
        try
        {
            NativeDecoderSource self = (NativeDecoderSource)GCHandle.FromIntPtr(user).Target!;
            if (!self.stream.CanSeek) return -1;
            self.stream.Seek(offset, origin switch { 0 => SeekOrigin.Begin, 1 => SeekOrigin.Current, _ => SeekOrigin.End });
            return 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
