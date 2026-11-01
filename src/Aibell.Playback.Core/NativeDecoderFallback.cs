namespace Aibell.Playback.Core;

/// <summary>Opens a decoder that is not written in C# (the native miniaudio decoders); null when the format is not recognized.</summary>
internal delegate IPcmSource? NativeDecoderFallback(Stream stream, uint sampleRate);
