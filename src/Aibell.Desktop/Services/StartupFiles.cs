namespace Aibell.Desktop.Services;

/// <summary>Turns the command line (what a file manager passes for "Open with") into the audio files to play.</summary>
public static class StartupFiles
{
    public static IReadOnlyList<Uri> Resolve(IEnumerable<string> arguments, string workingDirectory)
    {
        List<Uri> files = [];
        foreach (string argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument) || argument.StartsWith("--", StringComparison.Ordinal)) continue;
            string path;
            if (Uri.TryCreate(argument, UriKind.Absolute, out Uri? uri) && uri.IsFile && argument.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                path = uri.LocalPath;
            else
                path = Path.GetFullPath(argument, workingDirectory);
            if (File.Exists(path)) files.Add(new Uri(path));
        }
        return files;
    }
}
