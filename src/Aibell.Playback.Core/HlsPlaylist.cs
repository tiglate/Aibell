using System.Globalization;

namespace Aibell.Playback.Core;

/// <summary>One entry of a master playlist (<c>EXT-X-STREAM-INF</c>) or an audio rendition (<c>EXT-X-MEDIA</c>).</summary>
internal sealed record HlsVariant(Uri Uri, long Bandwidth, string? Codecs, bool HasVideo);

internal sealed record HlsSegment(Uri Uri, long Sequence, double Duration);

internal abstract record HlsPlaylist;

internal sealed record HlsMasterPlaylist(IReadOnlyList<HlsVariant> Variants, IReadOnlyList<HlsVariant> AudioRenditions) : HlsPlaylist;

internal sealed record HlsMediaPlaylist(long MediaSequence, double TargetDuration, bool EndList, IReadOnlyList<HlsSegment> Segments) : HlsPlaylist;

/// <summary>
/// A strict, bounded reader for the subset of RFC 8216 the built-in player needs: master playlists (to pick one variant) and media
/// playlists (segment list, sequence numbers, live/endlist). Encryption, byte ranges and fMP4 initialization sections are refused
/// with the usual "choose VLC" message rather than played wrongly.
/// </summary>
internal static class HlsPlaylistParser
{
    internal const int MaximumLines = 20000;
    internal const int MaximumSegments = 5000;
    internal const int MaximumVariants = 200;
    internal const int MaximumUriLength = 4096;

    public static HlsPlaylist Parse(string text, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(baseUri);
        string[] lines = text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length > MaximumLines) throw new InvalidDataException("The HLS playlist has too many lines.");
        int index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0) index++;
        if (index >= lines.Length || lines[index].Trim() != "#EXTM3U") throw new InvalidDataException("The HLS playlist does not start with #EXTM3U.");

        List<HlsVariant> variants = [];
        List<HlsVariant> renditions = [];
        List<HlsSegment> segments = [];
        long mediaSequence = 0;
        double targetDuration = 0;
        bool endList = false;
        double? pendingDuration = null;
        Dictionary<string, string>? pendingVariant = null;

        for (index++; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                if (!line.StartsWith("#EXT", StringComparison.Ordinal)) continue;
                (string tag, string value) = SplitTag(line);
                switch (tag)
                {
                    case "#EXT-X-TARGETDURATION":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out targetDuration) || targetDuration <= 0 || double.IsInfinity(targetDuration))
                            throw new InvalidDataException("The HLS target duration is invalid.");
                        break;
                    case "#EXT-X-MEDIA-SEQUENCE":
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out mediaSequence))
                            throw new InvalidDataException("The HLS media sequence is invalid.");
                        break;
                    case "#EXT-X-ENDLIST":
                        endList = true;
                        break;
                    case "#EXTINF":
                        int comma = value.IndexOf(',');
                        string duration = comma < 0 ? value : value[..comma];
                        if (!double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0 || double.IsInfinity(seconds))
                            throw new InvalidDataException("An HLS segment duration is invalid.");
                        pendingDuration = seconds;
                        break;
                    case "#EXT-X-STREAM-INF":
                        pendingVariant = ParseAttributes(value);
                        break;
                    case "#EXT-X-MEDIA":
                        Dictionary<string, string> media = ParseAttributes(value);
                        if (media.GetValueOrDefault("TYPE") == "AUDIO" && media.TryGetValue("URI", out string? mediaUri) && variants.Count + renditions.Count < MaximumVariants)
                            renditions.Add(new HlsVariant(ResolveUri(baseUri, mediaUri), 0, null, false));
                        break;
                    case "#EXT-X-KEY" or "#EXT-X-SESSION-KEY":
                        if (tag == "#EXT-X-KEY" && ParseAttributes(value).GetValueOrDefault("METHOD", "NONE") != "NONE")
                            throw new NotSupportedException(AudioSourceFactory.UnsupportedMessage("The built-in player cannot play encrypted HLS streams."));
                        break;
                    case "#EXT-X-MAP":
                        throw new NotSupportedException(AudioSourceFactory.UnsupportedMessage("The built-in player cannot play HLS streams made of fragmented MP4 segments."));
                    case "#EXT-X-BYTERANGE":
                        throw new NotSupportedException(AudioSourceFactory.UnsupportedMessage("The built-in player cannot play HLS streams that use byte ranges."));
                }
                continue;
            }

            // A URI line belongs to the tag that precedes it.
            if (pendingVariant is not null)
            {
                if (variants.Count >= MaximumVariants) throw new InvalidDataException("The HLS master playlist has too many variants.");
                long.TryParse(pendingVariant.GetValueOrDefault("BANDWIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out long bandwidth);
                pendingVariant.TryGetValue("CODECS", out string? codecs);
                bool video = pendingVariant.ContainsKey("RESOLUTION") || codecs is not null && HasVideoCodec(codecs);
                variants.Add(new HlsVariant(ResolveUri(baseUri, line), bandwidth, codecs, video));
                pendingVariant = null;
            }
            else if (pendingDuration is { } segmentDuration)
            {
                if (segments.Count >= MaximumSegments) throw new InvalidDataException("The HLS playlist has too many segments.");
                segments.Add(new HlsSegment(ResolveUri(baseUri, line), mediaSequence + segments.Count, segmentDuration));
                pendingDuration = null;
            }
            else throw new InvalidDataException("The HLS playlist has a URI without a describing tag.");
        }

        if (variants.Count > 0 && segments.Count > 0) throw new InvalidDataException("The HLS playlist mixes master and media tags.");
        if (variants.Count > 0 || renditions.Count > 0 && segments.Count == 0 && targetDuration == 0)
            return new HlsMasterPlaylist(variants, renditions);
        if (segments.Count == 0) throw new InvalidDataException("The HLS playlist lists no segments.");
        if (targetDuration <= 0) targetDuration = Math.Max(1, Math.Ceiling(segments.Max(segment => segment.Duration)));
        return new HlsMediaPlaylist(mediaSequence, targetDuration, endList, segments);
    }

    /// <summary>
    /// Picks the cheapest variant the built-in player has the best chance with: audio-only before video, MP3, then AAC-LC, before other codecs,
    /// then the lowest bandwidth. Audio-only renditions of a video master are used when no variant is audio-only.
    /// </summary>
    public static HlsVariant ChooseVariant(HlsMasterPlaylist master)
    {
        ArgumentNullException.ThrowIfNull(master);
        IEnumerable<HlsVariant> candidates = master.Variants;
        if (master.Variants.Count == 0 || master.Variants.All(variant => variant.HasVideo) && master.AudioRenditions.Count > 0)
            candidates = master.AudioRenditions;
        HlsVariant? best = candidates
            .OrderBy(variant => variant.HasVideo ? 1 : 0)
            .ThenBy(variant => CodecRank(variant.Codecs))
            .ThenBy(variant => variant.Bandwidth == 0 ? long.MaxValue : variant.Bandwidth)
            .FirstOrDefault();
        return best ?? throw new InvalidDataException("The HLS master playlist lists no playable variant.");
    }

    /// <summary>Resolves a playlist reference against its base, allowing only http(s) without credentials and never downgrading https.</summary>
    internal static Uri ResolveUri(Uri baseUri, string reference)
    {
        if (reference.Length == 0 || reference.Length > MaximumUriLength || reference.Any(char.IsControl))
            throw new InvalidDataException("An HLS playlist contains an invalid URI.");
        if (!Uri.TryCreate(baseUri, reference, out Uri? resolved) || !resolved.IsAbsoluteUri)
            throw new InvalidDataException("An HLS playlist contains an invalid URI.");
        return ValidateAddress(resolved, baseUri);
    }

    internal static Uri ValidateAddress(Uri resolved, Uri origin)
    {
        if (resolved.Scheme is not ("http" or "https") || resolved.UserInfo.Length > 0 || string.IsNullOrWhiteSpace(resolved.Host))
            throw new InvalidDataException("An HLS playlist refers to an address the player does not use.");
        if (origin.Scheme == "https" && resolved.Scheme != "https")
            throw new InvalidDataException("An HLS playlist tried to downgrade an https stream to http.");
        return resolved.Fragment.Length > 0 ? new Uri(resolved.GetLeftPart(UriPartial.Query)) : resolved;
    }

    private static bool HasVideoCodec(string codecs) => codecs.Split(',').Select(codec => codec.Trim()).Any(codec =>
        codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("hvc", StringComparison.OrdinalIgnoreCase) ||
        codec.StartsWith("hev", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("vp", StringComparison.OrdinalIgnoreCase) ||
        codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("dvh", StringComparison.OrdinalIgnoreCase));

    // MP3 and AAC-LC are what the built-in player decodes; HE-AAC (SBR) it does not, so it comes last.
    private static int CodecRank(string? codecs)
    {
        if (IsMp3Codec(codecs)) return 0;
        if (codecs is null) return 2;
        string[] list = codecs.Split(',').Select(codec => codec.Trim()).ToArray();
        if (list.Any(codec => codec.Equals("mp4a.40.2", StringComparison.OrdinalIgnoreCase))) return 1;
        if (list.Any(codec => codec.StartsWith("mp4a.40.5", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("mp4a.40.29", StringComparison.OrdinalIgnoreCase))) return 4;
        return 3;
    }

    private static bool IsMp3Codec(string? codecs) => codecs is not null && codecs.Split(',').Select(codec => codec.Trim()).Any(codec =>
        codec.Equals("mp3", StringComparison.OrdinalIgnoreCase) || codec.Equals("mp4a.40.34", StringComparison.OrdinalIgnoreCase) ||
        codec.Equals("mp4a.69", StringComparison.OrdinalIgnoreCase) || codec.Equals("mp4a.6B", StringComparison.OrdinalIgnoreCase));

    private static (string Tag, string Value) SplitTag(string line)
    {
        int colon = line.IndexOf(':');
        return colon < 0 ? (line, "") : (line[..colon], line[(colon + 1)..]);
    }

    private static Dictionary<string, string> ParseAttributes(string text)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        int i = 0;
        while (i < text.Length)
        {
            int equals = text.IndexOf('=', i);
            if (equals < 0) break;
            string key = text[i..equals].Trim();
            i = equals + 1;
            string value;
            if (i < text.Length && text[i] == '"')
            {
                int close = text.IndexOf('"', i + 1);
                if (close < 0) throw new InvalidDataException("An HLS attribute has an unterminated quoted value.");
                value = text[(i + 1)..close];
                i = close + 1;
                int comma = text.IndexOf(',', i);
                i = comma < 0 ? text.Length : comma + 1;
            }
            else
            {
                int comma = text.IndexOf(',', i);
                value = comma < 0 ? text[i..] : text[i..comma];
                i = comma < 0 ? text.Length : comma + 1;
            }
            result.TryAdd(key, value.Trim());
        }
        return result;
    }
}
