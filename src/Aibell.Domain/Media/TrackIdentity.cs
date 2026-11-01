using System.Security.Cryptography;
using System.Text;

namespace Aibell.Domain.Media;

public static class TrackIdentity
{
    public static Guid FromLocation(Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);

        string normalizedLocation = location.GetComponents(
            UriComponents.AbsoluteUri,
            UriFormat.SafeUnescaped);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedLocation));
        return new Guid(digest.AsSpan(0, 16));
    }
}
