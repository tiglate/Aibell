using System.Security.Cryptography;
using System.Text;
using Aibell.Application.Library;

namespace Aibell.Desktop.Services;

/// <summary>
/// Cover art for the player's now-playing panel: for <c>mpd://</c> tracks it asks the server (a <c>cover.*</c> file next to the song, or a
/// picture inside it) and keeps the result in a cache folder; everything else goes to the ordinary provider.
/// </summary>
public sealed class MpdArtworkProvider(IArtworkProvider inner, Func<IMpdTransport?> transport, string cacheDirectory) : IArtworkProvider
{
    public async Task<Uri?> GetAsync(Uri mediaLocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaLocation);
        if (!MpdTracks.TryGetSongUri(mediaLocation, out string songUri)) return await inner.GetAsync(mediaLocation, cancellationToken);
        if (transport() is not { IsConnected: true } active) return null;
        string path = Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(songUri)))[..32] + ".img");
        if (File.Exists(path)) return new Uri(path);
        byte[]? data;
        try
        {
            data = await active.GetCoverAsync(songUri, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        if (data is not { Length: > 0 }) return null;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, data, cancellationToken);
            File.Move(temporary, path, overwrite: true);
            return new Uri(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
