using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aibell.Application.Discs;

namespace Aibell.Metadata.MusicBrainz;

/// <summary>MusicBrainz Disc ID: https://musicbrainz.org/doc/Disc_ID_Calculation</summary>
internal static class DiscId
{
    public static string Compute(DiscToc toc)
    {
        ArgumentNullException.ThrowIfNull(toc);
        StringBuilder text = new();
        text.Append(toc.FirstTrackNumber.ToString("X2", CultureInfo.InvariantCulture));
        text.Append(toc.LastTrackNumber.ToString("X2", CultureInfo.InvariantCulture));
        // Index 0 is the lead-out, then tracks 1 to 99; slots for tracks the disc does not have stay zero.
        for (int slot = 0; slot < 100; slot++)
        {
            int offset = slot == 0 ? toc.LeadOutOffset
                : slot >= toc.FirstTrackNumber && slot <= toc.LastTrackNumber ? toc.TrackOffsets[slot - toc.FirstTrackNumber] : 0;
            text.Append(offset.ToString("X8", CultureInfo.InvariantCulture));
        }
        // SHA-1 is mandated by the Disc ID format; it is an identifier here, not a security measure.
#pragma warning disable CA5350
        return Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(text.ToString())))
            .Replace('+', '.').Replace('/', '_').Replace('=', '-');
#pragma warning restore CA5350
    }

    /// <summary>The "toc" query value used to find fuzzy matches for discs whose exact ID is unknown.</summary>
    public static string TocQuery(DiscToc toc) =>
        string.Join('+', new[] { toc.FirstTrackNumber, toc.LastTrackNumber, toc.LeadOutOffset }.Concat(toc.TrackOffsets)
            .Select(value => value.ToString(CultureInfo.InvariantCulture)));
}
