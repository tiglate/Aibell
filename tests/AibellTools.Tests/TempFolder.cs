namespace AibellTools.Tests;

internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Path = Directory.CreateTempSubdirectory("aibell-tools-test-").FullName;

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        // Git marks its object files read-only, which Windows refuses to delete.
        foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
