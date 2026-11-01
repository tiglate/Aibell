using System.Text.Json;
using NUnit.Framework;

namespace AibellTools.Tests;

[TestFixture]
public sealed class SecretScanTests
{
    private TempFolder folder = null!;
    private string root = "";
    private string snapshot = "";

    [SetUp]
    public void Create()
    {
        folder = new TempFolder();
        root = folder.Combine("repository");
        snapshot = folder.Combine("snapshot");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(snapshot);
    }

    [TearDown]
    public void Remove() => folder.Dispose();

    private void Git(params string[] arguments)
    {
        (int code, _, string error) = RepositoryRoot.Run("git", ["-C", root, .. arguments]);
        Assert.That(code, Is.Zero, error);
    }

    private static JsonElement Report(params object[] findings)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(findings));
        return document.RootElement.Clone();
    }

    private Dictionary<string, object?> Finding(string rule = "generic-api-key", string? file = null, object? line = null) => new()
    {
        ["RuleID"] = rule,
        ["File"] = file ?? Path.Combine(snapshot, "source.cs"),
        ["StartLine"] = line ?? 7,
        ["Secret"] = "private fixture value",
        ["Match"] = "private fixture value",
        ["Description"] = "private fixture value"
    };

    [Test]
    public void SnapshotTracked_UsesCurrentTrackedBytes_AndExcludesUntrackedAndDeletedFiles()
    {
        Git("init", "--quiet");
        string source = Path.Combine(root, "tracked ü file.txt"), deleted = Path.Combine(root, "deleted.txt");
        File.WriteAllText(source, "indexed version");
        File.WriteAllText(deleted, "old bytes");
        Git("add", Path.GetFileName(source), Path.GetFileName(deleted));
        File.WriteAllText(source, "current working bytes");
        File.Delete(deleted);
        File.WriteAllText(Path.Combine(root, "untracked.txt"), "not part of this gate");

        int count = SecretScan.SnapshotTracked(root, snapshot);

        Assert.That(count, Is.EqualTo(1));
        Assert.That(File.ReadAllText(Path.Combine(snapshot, Path.GetFileName(source))), Is.EqualTo("current working bytes"));
        Assert.That(File.Exists(Path.Combine(snapshot, "deleted.txt")), Is.False);
        Assert.That(File.Exists(Path.Combine(snapshot, "untracked.txt")), Is.False);
    }

    [Test]
    public void SnapshotTracked_EmptyRepository_FailsClosed()
    {
        Git("init", "--quiet");

        Assert.Throws<ToolException>(() => SecretScan.SnapshotTracked(root, snapshot));
    }

    [Test]
    public void SnapshotTracked_TrackedLink_CannotCopyOutsideTheSource()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Symbolic-link fixtures need POSIX permissions.");
        Git("init", "--quiet");
        string outside = folder.Combine("outside.txt");
        File.WriteAllText(outside, "outside bytes");
        File.CreateSymbolicLink(Path.Combine(root, "linked.txt"), outside);
        Git("add", "linked.txt");

        Assert.Throws<ToolException>(() => SecretScan.SnapshotTracked(root, snapshot));
        Assert.That(File.Exists(Path.Combine(snapshot, "linked.txt")), Is.False);
    }

    [Test]
    public void SanitizeFindings_KeepsOnlyRuleRelativePathAndLine()
    {
        List<SecretScan.Finding> safe = SecretScan.SanitizeFindings(Report(Finding()), snapshot);

        Assert.That(safe, Is.EqualTo(new[] { new SecretScan.Finding("generic-api-key", "source.cs", 7) }));
        Assert.That(JsonSerializer.Serialize(safe), Does.Not.Contain("fixture"));
        Assert.That(SecretScan.SanitizeFindings(Report(Finding(file: "source.cs")), snapshot), Is.EqualTo(safe));
    }

    [Test]
    public void SanitizeFindings_MalformedOrOutsideLocations_FailClosed()
    {
        JsonElement[] cases =
        [
            JsonDocument.Parse("{}").RootElement, Report(JsonDocument.Parse("null").RootElement), Report(Finding(rule: "unsafe rule")), Report(Finding(line: true)),
            Report(Finding(line: 0)), Report(Finding(file: Path.Combine(folder.Path, "outside.cs"))), Report(Finding(file: "../outside.cs"))
        ];
        foreach (JsonElement malformed in cases)
            Assert.Throws<ToolException>(() => SecretScan.SanitizeFindings(malformed, snapshot), malformed.ToString());
    }

    private static SecretScan.Runner FakeRunner(int code, object[] findings, bool createReport = true, Action<string>? capture = null) => (arguments, environment) =>
    {
        // Any ambient GITLEAKS_* override is passed on as a removal.
        if (Environment.GetEnvironmentVariable("GITLEAKS_CONFIG") is not null)
        {
            Assert.That(environment, Does.ContainKey("GITLEAKS_CONFIG"));
            Assert.That(environment["GITLEAKS_CONFIG"], Is.Null);
        }
        Assert.That(arguments, Does.Contain("--ignore-gitleaks-allow"));
        Assert.That(arguments, Does.Contain("--redact=100"));
        string report = arguments[arguments.ToList().IndexOf("--report-path") + 1];
        if (createReport) File.WriteAllText(report, JsonSerializer.Serialize(findings));
        capture?.Invoke(report);
        return (code, "captured private output");
    };

    [Test]
    public void RunScan_ObservesFindings_WithoutPersistingTheRawReportOrAmbientOverrides()
    {
        string? report = null;
        Environment.SetEnvironmentVariable("GITLEAKS_CONFIG", "untrusted override");
        try
        {
            List<SecretScan.Finding> safe = SecretScan.RunScan("unused-scanner", snapshot, "config.toml", FakeRunner(1, [Finding()], capture: path => report = path));

            Assert.That(safe[0].Line, Is.EqualTo(7));
            Assert.That(File.Exists(report), Is.False);
            Assert.That(JsonSerializer.Serialize(safe), Does.Not.Contain("fixture"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITLEAKS_CONFIG", null);
        }
    }

    [Test]
    public void RunScan_ScannerErrorMissingReportAndInconsistentStatus_FailClosed()
    {
        (int Code, object[] Findings, bool Create)[] cases = [(2, [], true), (0, [], false), (0, [Finding()], true), (1, [], true)];
        foreach ((int code, object[] findings, bool create) in cases)
            Assert.Throws<ToolException>(() => SecretScan.RunScan("unused-scanner", snapshot, "config.toml", FakeRunner(code, findings, create)), $"{code} {create}");
    }

    [Test]
    public void EnsureScanner_AlteredCachedTool_IsNeverExecutedOrOverwritten()
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            Assert.Ignore("The pinned scanner gate supports Linux x64.");
        string binary = folder.Combine("gitleaks");
        File.WriteAllText(binary, "altered scanner");

        Assert.Throws<ToolException>(() => SecretScan.EnsureScanner(binary, download: true, fetch: () => throw new InvalidOperationException("must not download")));

        Assert.That(File.ReadAllText(binary), Is.EqualTo("altered scanner"));
    }

    [Test]
    public void EnsureScanner_DownloadWithWrongChecksum_IsRefusedAndNothingIsInstalled()
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            Assert.Ignore("The pinned scanner gate supports Linux x64.");
        string binary = folder.Combine("tools", "gitleaks");

        Assert.Throws<ToolException>(() => SecretScan.EnsureScanner(binary, download: true, fetch: () => [1, 2, 3]));

        Assert.That(File.Exists(binary), Is.False);
    }

    [Test]
    public void Run_FailedRetry_ReplacesAStalePassingSummary()
    {
        string output = folder.Combine("reports");
        SecretScan.WriteSummary(output, "pass", 5, [], null);
        string missingScanner = folder.Combine("no-scanner", "gitleaks");

        int code = SecretScan.Run(root, missingScanner, output, download: false, (_, _) => throw new InvalidOperationException("never runs"));

        Assert.That(code, Is.EqualTo(2));
        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
        Assert.That(result.RootElement.GetProperty("status").GetString(), Is.EqualTo("error"));
        Assert.That(File.ReadAllText(Path.Combine(output, "summary.md")), Does.Not.Contain("Gate: PASS"));
    }
}
