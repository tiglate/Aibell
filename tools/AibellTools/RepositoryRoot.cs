using System.Diagnostics;
using System.Security.Cryptography;

namespace AibellTools;

internal static class RepositoryRoot
{
    /// <summary>The repository folder: the nearest parent of the working directory (or of the tool) holding Aibell.slnx.</summary>
    public static string Find()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Aibell.slnx"))) return directory.FullName;
        }
        throw new ToolException("Run this from inside the Aibell repository.");
    }

    public static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>Runs a program, returning its exit code and captured output.</summary>
    public static (int Code, string Output, string Error) Run(string program, IEnumerable<string> arguments, string? workingDirectory = null,
        IDictionary<string, string?>? environment = null, TimeSpan? timeout = null)
    {
        // git writes UTF-8 paths; the default would be the system code page on Windows and garble non-ASCII file names.
        ProcessStartInfo start = new(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? "",
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach ((string key, string? value) in environment)
            {
                if (value is null) start.Environment.Remove(key);
                else start.Environment[key] = value;
            }
        using Process process = Process.Start(start) ?? throw new ToolException($"Could not start {program}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new ToolException($"{program} did not finish in time.");
        }
        return (process.ExitCode, output.Result, error.Result);
    }
}
