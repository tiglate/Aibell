using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AibellTools;

/// <summary>
/// Scans a private snapshot of the tracked working-tree files with a pinned, checksum-verified Gitleaks and emits metadata only
/// (rule, path, line): matched content never leaves a private temporary folder. A canary secret proves the scanner still detects.
/// </summary>
internal static partial class SecretScan
{
    public const string Version = "8.30.1";
    private const string ArchiveSha256 = "551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb";
    private const string BinarySha256 = "88f91962aa2f93ac6ab281d553b9e125f5197bbbce38f9f2437f7299c32e5509";
    private const string Download = $"https://github.com/gitleaks/gitleaks/releases/download/v{Version}/gitleaks_{Version}_linux_x64.tar.gz";
    private const int ArchiveLimit = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions SummaryOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public sealed record Finding(string Rule, string Path, int Line);

    public delegate (int Code, string Output) Runner(IReadOnlyList<string> arguments, IDictionary<string, string?> environment);

    public static int Run(string[] args)
    {
        string root = RepositoryRoot.Find();
        Options options = new(args, new HashSet<string> { "--download" }, new HashSet<string> { "--scanner", "--output" });
        string scanner = Path.GetFullPath(options.Value("--scanner") ?? Path.Combine(root, "artifacts/tools/gitleaks", Version, "gitleaks"));
        string output = Path.GetFullPath(options.Value("--output") ?? Path.Combine(root, "artifacts/security"));
        return Run(root, scanner, output, options.Has("--download"), ProcessRunner);
    }

    internal static int Run(string root, string scanner, string output, bool download, Runner runner)
    {
        // Never keep a stale passing result after an interrupted retry.
        WriteSummary(output, "error", 0, [], "Scan did not complete.");
        try
        {
            EnsureScanner(scanner, download);
            ProveDetection(scanner, root, runner);
            string snapshot = Directory.CreateTempSubdirectory("aibell-tracked-secrets-").FullName;
            try
            {
                int count = SnapshotTracked(root, snapshot);
                List<Finding> findings = RunScan(scanner, snapshot, ConfigPath(root), runner);
                string status = findings.Count == 0 ? "pass" : "fail";
                WriteSummary(output, status, count, findings, null);
                Console.WriteLine($"Tracked-source secret gate: {status.ToUpperInvariant()} ({count} files, {findings.Count} findings).");
                return findings.Count == 0 ? 0 : 1;
            }
            finally
            {
                Directory.Delete(snapshot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is ToolException or IOException or InvalidDataException or JsonException or UnauthorizedAccessException or HttpRequestException)
        {
            string message = exception is ToolException ? exception.Message : $"Secret scan could not complete ({exception.GetType().Name}).";
            WriteSummary(output, "error", 0, [], message);
            Console.WriteLine(message);
            return 2;
        }
    }

    internal static string ConfigPath(string root) => Path.Combine(root, ".config", "gitleaks.toml");

    internal static void EnsureScanner(string path, bool download, Func<byte[]>? fetch = null)
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new ToolException("The pinned scanner gate currently supports Linux x64.");
        if (RepositoryRoot.IsLink(path)) throw new ToolException("The scanner must be an ordinary verified binary.");
        if (File.Exists(path))
        {
            if (RepositoryRoot.Sha256(path) != BinarySha256) throw new ToolException("Cached scanner checksum mismatch; refusing to execute it.");
            return;
        }
        if (!download) throw new ToolException("Scanner missing; use --download to restore the pinned tool.");
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        byte[] payload = fetch is null ? FetchArchive() : fetch();
        if (payload.Length > ArchiveLimit || Convert.ToHexStringLower(SHA256.HashData(payload)) != ArchiveSha256)
            throw new ToolException("Downloaded scanner archive checksum/size mismatch.");
        string temporary = Path.Combine(directory, ".scanner-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            using MemoryStream archive = new(payload);
            using GZipStream gzip = new(archive, CompressionMode.Decompress);
            using TarReader reader = new(gzip);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null && entry.Name.TrimStart('.', '/') != "gitleaks") { }
            if (entry is null || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null || entry.Length > 64 * 1024 * 1024)
                throw new ToolException("Invalid scanner archive entry.");
            using (FileStream file = File.Create(temporary)) entry.DataStream.CopyTo(file);
            if (RepositoryRoot.Sha256(temporary) != BinarySha256) throw new ToolException("Extracted scanner checksum mismatch.");
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            if (File.Exists(path) || RepositoryRoot.IsLink(path)) throw new ToolException("Scanner cache changed during restore.");
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] FetchArchive()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        using Stream stream = client.GetStreamAsync(Download).GetAwaiter().GetResult();
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > ArchiveLimit) throw new ToolException("Downloaded scanner archive checksum/size mismatch.");
        }
        return buffer.ToArray();
    }

    /// <summary>Copies the current bytes of every tracked file into <paramref name="target"/>; returns the count.</summary>
    internal static int SnapshotTracked(string root, string target)
    {
        (int code, string listing, _) = RepositoryRoot.Run("git", ["-C", root, "ls-files", "--cached", "-z"]);
        if (code != 0) throw new ToolException("git could not list the tracked files.");
        int count = 0;
        foreach (string name in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = name.Split('/');
            if (Path.IsPathRooted(name) || parts.Contains("..") || parts.Length == 0) throw new ToolException("Unsafe tracked source path.");
            string source = Path.Combine(root, name);
            for (int index = 1; index <= parts.Length; index++)
                if (RepositoryRoot.IsLink(Path.Combine([root, .. parts[..index]]))) throw new ToolException("Tracked source links are not allowed in the scan snapshot.");
            if (!File.Exists(source))
            {
                if (Directory.Exists(source)) throw new ToolException("Tracked source must be a regular file.");
                continue; // A working-tree deletion has no current bytes to scan.
            }
            string destination = Path.Combine(target, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            if (RepositoryRoot.IsLink(destination)) throw new ToolException("Source changed into a link while snapshotting.");
            count++;
        }
        if (count == 0) throw new ToolException("Refusing an empty tracked-source scan.");
        return count;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,100}$")]
    private static partial Regex RuleIdentity();

    /// <summary>Reduces a Gitleaks report to rule, snapshot-relative path and line; anything malformed fails closed.</summary>
    internal static List<Finding> SanitizeFindings(JsonElement report, string snapshot)
    {
        if (report.ValueKind != JsonValueKind.Array) throw new ToolException("Scanner report is not a finding list.");
        string snapshotFull = Path.GetFullPath(snapshot);
        List<Finding> safe = [];
        foreach (JsonElement finding in report.EnumerateArray())
        {
            if (finding.ValueKind != JsonValueKind.Object) throw new ToolException("Invalid scanner finding.");
            string? rule = Text(finding, "RuleID"), file = Text(finding, "File");
            if (rule is null || !RuleIdentity().IsMatch(rule)) throw new ToolException("Invalid scanner rule identity.");
            if (file is null || !finding.TryGetProperty("StartLine", out JsonElement lineValue) || lineValue.ValueKind != JsonValueKind.Number
                || !lineValue.TryGetInt32(out int line) || line < 1) throw new ToolException("Invalid scanner source location.");
            string relative = Path.GetRelativePath(snapshotFull, Path.GetFullPath(Path.Combine(snapshotFull, file)));
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new ToolException("Scanner reported a path outside its snapshot.");
            safe.Add(new Finding(rule, relative.Replace('\\', '/'), line));
        }
        return safe;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static List<Finding> RunScan(string scanner, string snapshot, string config, Runner runner)
    {
        string temporary = Directory.CreateTempSubdirectory("aibell-secret-report-").FullName;
        try
        {
            string report = Path.Combine(temporary, "private-findings.json");
            Dictionary<string, string?> environment = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(key => key.StartsWith("GITLEAKS_", StringComparison.Ordinal)).ToDictionary(key => key, _ => (string?)null);
            (int code, _) = runner([scanner, "dir", snapshot, "--config", config, "--gitleaks-ignore-path", Path.Combine(temporary, "no-ignore-file"),
                "--ignore-gitleaks-allow", "--redact=100", "--no-banner", "--no-color", "--report-format=json", "--report-path", report, "--timeout=180"], environment);
            // Captured logs and the raw report never leave this private temporary folder.
            if (code is not (0 or 1) || !File.Exists(report)) throw new ToolException("Scanner failed to produce a complete report.");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report, Encoding.UTF8));
            List<Finding> findings = SanitizeFindings(document.RootElement, snapshot);
            if ((code == 0) != (findings.Count == 0)) throw new ToolException("Scanner exit status disagrees with its report.");
            return findings;
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static void ProveDetection(string scanner, string root, Runner runner)
    {
        string snapshot = Directory.CreateTempSubdirectory("aibell-secret-canary-").FullName;
        try
        {
            // Synthetic, offline-only canary, assembled at run time so no token is ever checked into source.
            string canary = "ghp_" + RandomNumberGenerator.GetString("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ", 36);
            File.WriteAllText(Path.Combine(snapshot, "canary.txt"), "token = " + canary + "\n");
            List<Finding> findings = RunScan(scanner, snapshot, ConfigPath(root), runner);
            if (!findings.Any(item => item.Rule == "github-pat" && item.Path == "canary.txt"))
                throw new ToolException("The scanner failed its mandatory synthetic detection check.");
        }
        finally
        {
            Directory.Delete(snapshot, recursive: true);
        }
    }

    internal static void WriteSummary(string output, string status, int tracked, IReadOnlyList<Finding> findings, string? error)
    {
        Directory.CreateDirectory(output);
        var summary = new
        {
            schemaVersion = 1,
            scannerVersion = Version,
            status,
            detectionSelfTest = status is "pass" or "fail" ? "pass" : null,
            trackedFiles = tracked,
            findings = findings.Select(item => new { rule = item.Rule, path = item.Path, line = item.Line }),
            error
        };
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, SummaryOptions) + "\n");
        List<string> lines = ["Tracked-source secret gate", "", $"Gate: {status.ToUpperInvariant()}", $"Scanner: Gitleaks {Version}", $"Tracked files: {tracked}", ""];
        if (error is not null) lines.Add(error);
        lines.AddRange(findings.Select(item => $"- {item.Rule}: {item.Path}:{item.Line}"));
        lines.AddRange(["", "Current tracked working-tree bytes only; untracked files, deleted bytes and Git history are outside this gate.",
            "Reports retain rule/path/line metadata only. Findings never include matched content or credentials."]);
        File.WriteAllText(Path.Combine(output, "summary.md"), string.Join('\n', lines) + "\n");
    }

    private static (int Code, string Output) ProcessRunner(IReadOnlyList<string> arguments, IDictionary<string, string?> environment)
    {
        (int code, string output, string error) = RepositoryRoot.Run(arguments[0], arguments.Skip(1), environment: environment, timeout: TimeSpan.FromSeconds(200));
        return (code, output + error);
    }
}
