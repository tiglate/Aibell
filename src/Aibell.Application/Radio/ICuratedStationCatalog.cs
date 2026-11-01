using System.Security.Cryptography;
using System.Text;
using Aibell.Domain.Radio;

namespace Aibell.Application.Radio;

/// <summary>
/// One entry of the curated station list that ships with the application. Browsing the list never contacts a station:
/// only <see cref="Logo"/> is ever fetched, and only after the user has turned the catalog on. <see cref="UnsupportedFormat"/> names the audio
/// format (HE-AAC, Opus, fragmented MP4...) that the built-in player cannot play, so the UI can say the station needs VLC; null when it can.
/// </summary>
public sealed record CuratedStation(string Id, string Name, string CountryCode, string Country, string Genre, string Description,
    Uri Stream, Uri? Logo = null, Uri? Homepage = null, string? UnsupportedFormat = null)
{
    // A fixed namespace for name-based (version 5) identifiers: the same catalog id always yields the same station id.
    private static readonly Guid Namespace = new("6f1d4c52-0b0e-5c37-9a41-3d8e2b7f5a10");

    /// <summary>The identifier a copy of this station gets in the user's library, stable across runs and machines.</summary>
    public Guid StationId => IdFor(Id);

    /// <summary>Derives a RFC 4122 version 5 (SHA-1, name-based) identifier from a catalog id.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 4122 defines name-based version 5 identifiers with SHA-1; this is an identifier, not a security boundary.")]
    public static Guid IdFor(string catalogId)
    {
        ArgumentException.ThrowIfNullOrEmpty(catalogId);
        byte[] name = Encoding.UTF8.GetBytes(catalogId);
        byte[] input = new byte[16 + name.Length];
        Namespace.TryWriteBytes(input, bigEndian: true, out _);
        name.CopyTo(input, 16);
        byte[] hash = SHA1.HashData(input);
        Span<byte> bytes = hash.AsSpan(0, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>The station as it is saved to "My stations". The creator line shows the station's own website host.</summary>
    public RadioStation ToRadioStation() => new(StationId, Name, Stream, Genre, Homepage?.Host ?? "", Description,
        Country: Country, CountryCode: CountryCode, LogoUri: Logo, CatalogId: Id);
}

/// <summary>The built-in list of curated stations, in popularity order. Implementations never throw for bad data.</summary>
public interface ICuratedStationCatalog
{
    IReadOnlyList<CuratedStation> Stations { get; }
}
