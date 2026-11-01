using Aibell.Application.Discs;
using Aibell.Audio.Cdda;
using Aibell.Domain.Radio;

namespace Aibell.Playback.Core;

/// <summary>
/// Chooses a decoder for a track location: the managed decoders first when <c>managedCodecs</c> is set (the "C# Playback" engine), miniaudio for
/// the common formats, Media Foundation for the rest on Windows. Audio CD tracks (<c>cdda:</c>) are read from the drive in C# when <c>drives</c> is given.
/// </summary>
/// <param name="nativeDecoderFallback">The decoders that are not written in C# (miniaudio's), tried last; null leaves the managed decoders as the only ones.</param>
internal sealed class AudioSourceFactory(HttpClient? http, HlsOptions? hlsOptions = null, bool managedCodecs = false, string? soundFontPath = null,
    IOpticalDriveService? drives = null, Func<string, ICdAudioReader>? openDisc = null, NativeDecoderFallback? nativeDecoderFallback = null)
{
    private readonly Func<string, ICdAudioReader> openDisc = openDisc ?? CdAudioReader.Open;

    // The user's SoundFont, read on the first MIDI file (it can be large). A missing or damaged bank leaves the built-in instruments in charge.
    private readonly Lazy<Aibell.Audio.Midi.SoundFont?> soundFont = new(() => LoadSoundFont(soundFontPath));

    private static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".oga", ".wav", ".wave"
    };

    // MIDI has no native or Windows decoder: the managed synthesizer plays it whatever the playback engine setting is.
    private static readonly HashSet<string> MidiExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mid", ".midi", ".rmi" };

    // SPC files (a snapshot of the SNES sound unit, alone or in an RSN archive) have no native or Windows decoder either: the managed emulator plays them on every engine setting.
    private static readonly HashSet<string> SpcExtensions = new(StringComparer.OrdinalIgnoreCase) { ".spc", ".rsn" };

    // MP4 audio files: the managed decoders read them on every system; Media Foundation is the Windows fallback (and the default engine's first choice there).
    private static readonly HashSet<string> Mp4Extensions = new(StringComparer.OrdinalIgnoreCase) { ".m4a", ".m4b", ".mp4", ".3gp", ".3g2" };

    // Only Media Foundation can play these.
    private static readonly HashSet<string> WindowsOnlyExtensions = new(StringComparer.OrdinalIgnoreCase) { ".wma" };

    private const string ChooseVlc = " Choose VLC under Preferences > Playback to play this.";

    public static string UnsupportedMessage(string what) => what + ChooseVlc;

    public async Task<IPcmSource> OpenAsync(Uri location, uint sampleRate, CancellationToken cancellationToken)
    {
        if (DiscLocation.TryParse(location, out string driveId, out int trackNumber))
        {
            if (drives is null) throw new NotSupportedException(UnsupportedMessage("The built-in player cannot play audio CDs."));
            return await OpenDiscTrackAsync(drives, driveId, trackNumber, sampleRate, cancellationToken).ConfigureAwait(false);
        }
        bool network = location.Scheme is "http" or "https";
        if (!network && !location.IsFile) throw new NotSupportedException(UnsupportedMessage($"The built-in player cannot open {location.Scheme} locations."));
        string extension = Path.GetExtension(network ? location.AbsolutePath : location.LocalPath);

        // Local ADTS files keep going to Media Foundation on Windows (it can seek them); streams and every other system use the built-in decoder.
        bool adtsExtension = extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) || extension.Equals(".adts", StringComparison.OrdinalIgnoreCase);
        bool mp4Extension = Mp4Extensions.Contains(extension);
        if (WindowsOnlyExtensions.Contains(extension) || (adtsExtension && !network && OperatingSystem.IsWindows()) || (mp4Extension && OperatingSystem.IsWindows() && !managedCodecs))
        {
            if (!OperatingSystem.IsWindows())
                throw new NotSupportedException(UnsupportedMessage($"The built-in player cannot decode {extension} files on this system."));
            // Opening can touch the network and Windows codecs, so it never runs on the caller's (UI) thread.
            return await Task.Run(() => OpenMediaFoundation(location, sampleRate), cancellationToken).ConfigureAwait(false);
        }

        // HLS: fetch the segments ourselves and hand the decoders their bytes; there is no Media Foundation fallback for a live manifest.
        bool hls = network && RadioStation.IsHlsStream(location);
        // Unknown or missing extensions (internet radio) go to the native decoders first.
        Stream stream = await OpenStreamAsync(location, network, hls, cancellationToken).ConfigureAwait(false);
        IPcmSource? opened;
        try
        {
            opened = await Task.Run(() => OpenDecoder(stream, extension, mp4Extension, sampleRate), cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException) when (OperatingSystem.IsWindows() && !hls)
        {
            // The managed AAC decoder refused (HE-AAC, ...); Media Foundation may still know the stream.
            stream.Dispose();
            return await Task.Run(() => OpenMediaFoundation(location, sampleRate), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        if (opened is not null) return opened;
        if (mp4Extension && !OperatingSystem.IsWindows())
        {
            stream.Dispose();
            throw new NotSupportedException(UnsupportedMessage($"The built-in player cannot decode the audio in this {extension} file on this system."));
        }
        if (OperatingSystem.IsWindows() && !hls && !NativeExtensions.Contains(extension) && !MidiExtensions.Contains(extension) && !SpcExtensions.Contains(extension)) return await Task.Run(() => OpenMediaFoundation(location, sampleRate), cancellationToken).ConfigureAwait(false);
        throw new NotSupportedException(UnsupportedMessage(hls
            ? "The built-in player does not recognize the audio inside this HLS stream."
            : "The built-in player does not recognize this audio format."));
    }

    // The managed decoders first when asked for (and always for MP4, MIDI and SPC, which nothing else plays off Windows), then the managed AAC reader and miniaudio.
    // The stream stays open when nothing recognizes it.
    private IPcmSource? OpenDecoder(Stream stream, string extension, bool mp4Extension, uint sampleRate)
    {
        bool midi = MidiExtensions.Contains(extension);
        if (managedCodecs || mp4Extension || midi || SpcExtensions.Contains(extension))
        {
            IPcmSource? managed = ManagedPcmSource.TryOpen(stream, sampleRate, midi ? soundFont.Value : null);
            if (managed is not null) return managed;
        }
        if (mp4Extension) return null;
        return (IPcmSource?)TryOpenManaged(stream, sampleRate) ?? nativeDecoderFallback?.Invoke(stream, sampleRate);
    }

    private async Task<IPcmSource> OpenDiscTrackAsync(IOpticalDriveService drives, string driveId, int trackNumber, uint sampleRate, CancellationToken cancellationToken)
    {
        DiscToc toc = await drives.ReadTocAsync(new OpticalDrive(driveId, driveId), cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("There is no audio disc in the drive.");
        int index = trackNumber - toc.FirstTrackNumber;
        if ((uint)index >= (uint)toc.TrackCount) throw new IOException($"The disc has no track {trackNumber}.");
        (int start, int end) = toc.SectorRangeOf(index);
        // Opening the drive can block while it spins up, so it never runs on the caller's (UI) thread.
        return await Task.Run(() =>
        {
            ICdAudioReader reader = openDisc(driveId);
            try
            {
                return (IPcmSource)ManagedPcmSource.FromDecoder(new CddaDecoder(reader, start, end), sampleRate);
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Aibell.Audio.Midi.SoundFont? LoadSoundFont(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Aibell.Audio.Midi.SoundFont.Load(path);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The managed AAC decoder, when the stream is ADTS or MPEG-TS; null for anything else (it then goes to miniaudio).</summary>
    private static AacPcmSource? TryOpenManaged(Stream stream, uint sampleRate)
    {
        if (stream is HlsAudioStream hls)
        {
            return hls.FirstSegmentKind switch
            {
                HlsSegmentKind.MpegTransportStream => AacPcmSource.TryOpen(stream, sampleRate, () => hls.SegmentsStarted, Aibell.Audio.Aac.AacContainer.MpegTs),
                HlsSegmentKind.Aac => AacPcmSource.TryOpen(stream, sampleRate, () => hls.SegmentsStarted, Aibell.Audio.Aac.AacContainer.Adts),
                _ => null
            };
        }
        return AacPcmSource.TryOpen(stream, sampleRate);
    }

    private static MediaFoundationSource OpenMediaFoundation(Uri location, uint sampleRate)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return MediaFoundationSource.Open(location.IsFile ? location.LocalPath : location.AbsoluteUri, sampleRate);
    }

    private async Task<Stream> OpenStreamAsync(Uri location, bool network, bool hls, CancellationToken cancellationToken)
    {
        if (network)
        {
            if (http is null) throw new NotSupportedException(UnsupportedMessage("No network client is available to the built-in player."));
            if (hls) return await HlsAudioStream.OpenAsync(http, location, cancellationToken, hlsOptions).ConfigureAwait(false);
            return await HttpAudioStream.OpenAsync(http, location, cancellationToken).ConfigureAwait(false);
        }
        if (Aibell.Audio.Spc.RsnArchive.TryParseLocation(location, out string archive, out string entry))
        {
            // One SPC out of an RSN archive: unpack it (RAR decoding can take a moment, so not on the caller's thread).
            return await Task.Run(() =>
            {
                try
                {
                    using FileStream stream = File.OpenRead(archive);
                    return (Stream)new MemoryStream(Aibell.Audio.Spc.RsnArchive.ReadTrack(stream, entry), writable: false);
                }
                catch (InvalidDataException exception)
                {
                    throw new NotSupportedException("The RSN archive cannot be read: " + exception.Message, exception);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        return new FileStream(location.LocalPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan
        });
    }
}
