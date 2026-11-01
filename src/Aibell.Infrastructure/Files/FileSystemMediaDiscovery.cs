using System.Runtime.CompilerServices;
using Aibell.Application.Library;
using Aibell.Audio.Spc;

namespace Aibell.Infrastructure.Files;

public sealed class FileSystemMediaDiscovery : IMediaFileDiscovery
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".aiff", ".alac", ".flac", ".m4a", ".mid", ".midi", ".mp3", ".ogg", ".opus", ".rmi", ".rsn", ".spc", ".wav", ".wma"
    };

    private static IReadOnlyList<string> ListRsnTracks(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return RsnArchive.ListTracks(stream);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public async IAsyncEnumerable<Uri> DiscoverAsync(
        IReadOnlyCollection<string> roots,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(root, "*", EnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!SupportedExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                // An RSN archive is a set of tracks: one location per SPC inside. One that cannot be listed is returned as it is, so that
                // the import reports it as a damaged file.
                if (RsnArchive.IsRsnPath(path))
                {
                    IReadOnlyList<string> tracks = ListRsnTracks(path);
                    if (tracks.Count > 0)
                    {
                        foreach (string track in tracks) yield return RsnArchive.CreateLocation(path, track);
                        await Task.Yield();
                        continue;
                    }
                }

                yield return new Uri(Path.GetFullPath(path));
                await Task.Yield();
            }
        }
    }
}
