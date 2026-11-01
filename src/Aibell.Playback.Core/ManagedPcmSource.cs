using Aibell.Audio;
using Aibell.Audio.Aac;
using Aibell.Audio.Aiff;
using Aibell.Audio.Alac;
using Aibell.Audio.Flac;
using Aibell.Audio.Midi;
using Aibell.Audio.Mp3;
using Aibell.Audio.Opus;
using Aibell.Audio.Spc;
using Aibell.Audio.Vorbis;
using Aibell.Audio.Wav;

namespace Aibell.Playback.Core;

/// <summary>
/// Plays a stream with the managed decoders (the "C# Playback" engine): any <see cref="IAudioDecoder"/>, folded to stereo and converted to the
/// device rate. When the stream already is stereo at the device rate the decoder writes straight into the caller's buffer.
/// </summary>
internal sealed class ManagedPcmSource : IPcmSource
{
    private const int BlockFrames = 2048;

    private readonly IAudioDecoder decoder;
    private readonly int outputRate;
    private readonly ChannelMixer mixer;
    private readonly StereoResampler resampler;
    private readonly float[] native;
    private readonly float[] stereo = new float[BlockFrames * 2];
    private bool inputEnded;
    private bool disposed;

    private ManagedPcmSource(IAudioDecoder decoder, uint outputRate)
    {
        this.decoder = decoder;
        this.outputRate = (int)outputRate;
        mixer = new ChannelMixer(decoder.Channels);
        resampler = new StereoResampler(decoder.SampleRate, this.outputRate, BlockFrames);
        native = new float[BlockFrames * decoder.Channels];
    }

    /// <summary>Plays an already open decoder, which the source then owns.</summary>
    public static ManagedPcmSource FromDecoder(IAudioDecoder decoder, uint outputRate) => new(decoder, outputRate);

    /// <summary>
    /// Opens the managed decoder for the container at the start of <paramref name="stream"/>, which it then owns. Returns null (leaving the
    /// stream open, rewound to where it was) when the format is not one the managed decoders handle or the decoder refuses the file; the caller then
    /// tries another decoder.
    /// </summary>
    public static ManagedPcmSource? TryOpen(Stream stream, uint outputRate, SoundFont? soundFont = null)
    {
        if (!stream.CanSeek) return null;
        long start = stream.Position;
        Span<byte> head = stackalloc byte[AudioSniffer.HeadLength];
        int filled = 0;
        while (filled < head.Length)
        {
            int read = stream.Read(head[filled..]);
            if (read <= 0) break;
            filled += read;
        }
        long audioStart = start + AudioSniffer.Id3v2Length(head[..filled]);
        if (audioStart != start)
        {
            stream.Seek(audioStart, SeekOrigin.Begin);
            filled = 0;
            while (filled < head.Length)
            {
                int read = stream.Read(head[filled..]);
                if (read <= 0) break;
                filled += read;
            }
        }
        AudioContainer container = AudioSniffer.Detect(head[..filled]);
        stream.Seek(audioStart, SeekOrigin.Begin);
        try
        {
            IAudioDecoder? decoder = container switch
            {
                AudioContainer.Wav => WavDecoder.Open(stream),
                AudioContainer.Aiff => AiffDecoder.Open(stream),
                AudioContainer.Flac => FlacDecoder.Open(stream),
                AudioContainer.OggVorbis => VorbisDecoder.Open(stream),
                AudioContainer.OggOpus => OggOpusDecoder.Open(stream),
                AudioContainer.Mp4 => OpenMp4(stream, audioStart),
                AudioContainer.Midi => MidiDecoder.Open(stream, (int)outputRate, soundFont),
                AudioContainer.Spc => SpcDecoder.Open(stream),
                AudioContainer.Unknown when Mp3Decoder.StartsWithFrame(head[..filled]) => Mp3Decoder.Open(stream),
                _ => null
            };
            if (decoder is null) return null;
            return new ManagedPcmSource(decoder, outputRate);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            stream.Seek(start, SeekOrigin.Begin);
            return null;
        }
    }

    // Apple Lossless first; a file without an ALAC track may hold AAC. The failed attempt moved the stream, so it is rewound for the second.
    private static IAudioDecoder OpenMp4(Stream stream, long audioStart)
    {
        try
        {
            return AlacDecoder.Open(stream);
        }
        catch (NotSupportedException)
        {
            stream.Seek(audioStart, SeekOrigin.Begin);
            return Mp4AacDecoder.Open(stream);
        }
    }

    public int Read(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (resampler.Passthrough && decoder.Channels == 2) return decoder.Read(destination, frames);
        int produced = 0;
        while (produced < frames)
        {
            int got = resampler.Pull(destination[(produced * 2)..], frames - produced);
            produced += got;
            if (got > 0) continue;
            if (inputEnded) break;
            int decoded = decoder.Read(native, BlockFrames);
            if (decoded <= 0)
            {
                inputEnded = true;
                break;
            }
            mixer.ToStereo(native, stereo, decoded);
            resampler.Push(stereo, decoded);
        }
        return produced;
    }

    public bool Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long target = decoder.SampleRate == outputRate ? frame : (long)(frame * (double)decoder.SampleRate / outputRate);
        if (!decoder.Seek(Math.Max(0, target))) return false;
        resampler.Reset();
        inputEnded = false;
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        decoder.Dispose();
    }
}
