using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AibellTools;

/// <summary>Validates a Cobertura report from the modern production modules and enforces the floors in tests/coverage-baseline.json.</summary>
internal static partial class CoverageGate
{
    public sealed record Module(long LinesCovered, long LinesValid, long BranchesCovered, long BranchesValid)
    {
        public double LinePercent => 100.0 * LinesCovered / LinesValid;

        public double BranchPercent => BranchesValid == 0 ? 100 : 100.0 * BranchesCovered / BranchesValid;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record Floors(double Line, double Branch);

    public sealed record Baseline(string Profile, Dictionary<string, Floors> Modules)
    {
        public static Baseline Parse(JsonElement root)
        {
            if (!root.TryGetProperty("schemaVersion", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1
                || !root.TryGetProperty("modules", out JsonElement modules) || modules.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Coverage baseline schema or module scope is incompatible.");
            Dictionary<string, Floors> floors = [];
            foreach (JsonProperty module in modules.EnumerateObject())
                floors[module.Name] = new Floors(Percent(module.Value, "linePercentMinimum"), Percent(module.Value, "branchPercentMinimum"));
            return new Baseline(root.TryGetProperty("profile", out JsonElement profile) ? profile.GetString() ?? "" : "", floors);
        }

        private static double Percent(JsonElement module, string name)
        {
            if (!module.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double percent)
                || !double.IsFinite(percent) || percent is < 0 or > 100)
                throw new InvalidDataException("Coverage floors must be finite percentages between zero and one hundred.");
            return percent;
        }
    }

    public static int Run(string[] args)
    {
        string root = RepositoryRoot.Find();
        Options options = new(args, new HashSet<string>(), new HashSet<string> { "--baseline", "--json", "--markdown" });
        if (options.Positional.Count != 1) throw new ToolException("Give the Cobertura report to check.");
        string jsonPath = options.Value("--json") ?? Path.Combine(root, "artifacts/coverage/summary.json");
        string markdownPath = options.Value("--markdown") ?? Path.Combine(root, "artifacts/coverage/summary.md");
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(options.Value("--baseline") ?? Path.Combine(root, "tests/coverage-baseline.json")));
            Baseline baseline = Baseline.Parse(document.RootElement);
            HashSet<string> projects = Directory.EnumerateDirectories(Path.Combine(root, "src"), "Aibell.*")
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj")).Select(Path.GetFileNameWithoutExtension).Cast<string>().ToHashSet();
            if (!projects.SetEquals(baseline.Modules.Keys))
                throw new InvalidDataException("Modern project scope changed. Add its coverage policy before accepting a new baseline.");
            Dictionary<string, Module> measured = Measure(options.Positional[0], projects);
            List<string> failures = Evaluate(measured, baseline);
            (int code, string revision, _) = RepositoryRoot.Run("git", ["rev-parse", "HEAD"], root);
            var summary = new
            {
                schemaVersion = 1,
                profile = baseline.Profile,
                sourceRevision = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? (code == 0 ? revision.Trim() : "unknown"),
                generatedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                modules = measured.ToDictionary(item => item.Key, item => new
                {
                    linesCovered = item.Value.LinesCovered,
                    linesValid = item.Value.LinesValid,
                    linePercent = item.Value.LinePercent,
                    branchesCovered = item.Value.BranchesCovered,
                    branchesValid = item.Value.BranchesValid,
                    branchPercent = item.Value.BranchPercent
                }),
                failures
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(summary, Indented) + "\n");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(markdownPath))!);
            string text = Markdown(measured, baseline, failures);
            File.WriteAllText(markdownPath, text);
            Console.WriteLine(text);
            return failures.Count == 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or System.Xml.XmlException or KeyNotFoundException)
        {
            throw new ToolException(exception.Message, exception);
        }
    }

    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+)?% \((\d+)/(\d+)\)$")]
    private static partial Regex ConditionCoverage();

    private static long Integer(string? value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result)) throw new InvalidDataException("Coverage counts cannot be negative or missing.");
        return result;
    }

    private static double Rate(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) || !double.IsFinite(result) || result is < 0 or > 1)
            throw new InvalidDataException("Coverage rates must be finite values between zero and one.");
        return result;
    }

    private static void CheckRate(string? actual, long covered, long total)
    {
        double expected = total == 0 ? 1 : (double)covered / total;
        if (Math.Abs(Rate(actual) - expected) > 1e-8) throw new InvalidDataException("Reported coverage rate disagrees with its line/branch counts.");
    }

    internal static Dictionary<string, Module> Measure(string path, IReadOnlySet<string> expectedModules)
    {
        if (new FileInfo(path).Length > 50 * 1024 * 1024) throw new InvalidDataException("Coverage report exceeds the inspection bound.");
        XElement root = XDocument.Load(path).Root ?? throw new InvalidDataException("Expected a Cobertura coverage report.");
        if (root.Name != "coverage") throw new InvalidDataException("Expected a Cobertura coverage report.");
        Dictionary<string, Module> measured = [];
        foreach (XElement package in root.Elements("packages").Elements("package"))
        {
            string name = (string?)package.Attribute("name") ?? "";
            if (!expectedModules.Contains(name) || measured.ContainsKey(name)) throw new InvalidDataException("Unexpected or duplicate coverage module: " + name);
            long covered = 0, valid = 0, branchesCovered = 0, branchesValid = 0;
            foreach (XElement source in package.Elements("classes").Elements("class"))
            {
                string filename = ((string?)source.Attribute("filename") ?? "").Replace('\\', '/');
                if (!Regex.IsMatch(filename, "(?:^|/)src/" + Regex.Escape(name) + "/") || filename.Contains("/obj/", StringComparison.Ordinal)
                    || filename.EndsWith(".g.cs", StringComparison.Ordinal) || filename.EndsWith(".g.i.cs", StringComparison.Ordinal))
                    throw new InvalidDataException("Coverage includes a generated, test or out-of-module source: " + filename);
                // Class lines are the collector's canonical sequence-point records; method line lists repeat them and are never counted.
                foreach (XElement line in source.Elements("lines").Elements("line"))
                {
                    if (Integer((string?)line.Attribute("number")) == 0) throw new InvalidDataException("Coverage contains an invalid source line.");
                    valid++;
                    if (Integer((string?)line.Attribute("hits")) > 0) covered++;
                    string branch = ((string?)line.Attribute("branch") ?? "false").ToLowerInvariant();
                    if (branch is not ("true" or "false")) throw new InvalidDataException("Coverage has an invalid branch marker.");
                    if (branch == "false") continue;
                    Match fraction = ConditionCoverage().Match((string?)line.Attribute("condition-coverage") ?? "");
                    if (!fraction.Success) throw new InvalidDataException("Coverage branch counts are missing.");
                    long hit = Integer(fraction.Groups[1].Value), count = Integer(fraction.Groups[2].Value);
                    if (count == 0 || hit > count) throw new InvalidDataException("Coverage branch counts are invalid.");
                    branchesCovered += hit;
                    branchesValid += count;
                }
            }
            if (valid == 0) throw new InvalidDataException("A production module has no measurable lines: " + name);
            CheckRate((string?)package.Attribute("line-rate"), covered, valid);
            CheckRate((string?)package.Attribute("branch-rate"), branchesCovered, branchesValid);
            measured[name] = new Module(covered, valid, branchesCovered, branchesValid);
        }
        if (!expectedModules.SetEquals(measured.Keys))
            throw new InvalidDataException("Missing production coverage modules: " + string.Join(", ", expectedModules.Except(measured.Keys).Order(StringComparer.Ordinal)));
        (string Attribute, Func<Module, long> Value)[] totals =
            [("lines-covered", m => m.LinesCovered), ("lines-valid", m => m.LinesValid), ("branches-covered", m => m.BranchesCovered), ("branches-valid", m => m.BranchesValid)];
        foreach ((string attribute, Func<Module, long> value) in totals)
            if (measured.Values.Sum(value) != Integer((string?)root.Attribute(attribute))) throw new InvalidDataException("Coverage root totals disagree with the measured production modules.");
        CheckRate((string?)root.Attribute("line-rate"), Integer((string?)root.Attribute("lines-covered")), Integer((string?)root.Attribute("lines-valid")));
        CheckRate((string?)root.Attribute("branch-rate"), Integer((string?)root.Attribute("branches-covered")), Integer((string?)root.Attribute("branches-valid")));
        return measured;
    }

    internal static List<string> Evaluate(IReadOnlyDictionary<string, Module> measured, Baseline baseline)
    {
        if (!measured.Keys.ToHashSet().SetEquals(baseline.Modules.Keys)) throw new InvalidDataException("Coverage baseline schema or module scope is incompatible.");
        List<string> failures = [];
        foreach ((string name, Module module) in measured.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Floors floors = baseline.Modules[name];
            if (module.LinePercent + 1e-8 < floors.Line) failures.Add($"{name}: linePercent {module.LinePercent.ToString("F2", CultureInfo.InvariantCulture)}% is below {floors.Line.ToString("F2", CultureInfo.InvariantCulture)}%");
            if (module.BranchPercent + 1e-8 < floors.Branch) failures.Add($"{name}: branchPercent {module.BranchPercent.ToString("F2", CultureInfo.InvariantCulture)}% is below {floors.Branch.ToString("F2", CultureInfo.InvariantCulture)}%");
        }
        return failures;
    }

    internal static string Markdown(IReadOnlyDictionary<string, Module> measured, Baseline baseline, IReadOnlyList<string> failures)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder text = new();
        text.Append("Modern production coverage (Linux x64, Debug, full native profile)\n\n| Module | Lines | Floor | Branches | Floor |\n| --- | ---: | ---: | ---: | ---: |\n");
        foreach ((string name, Module module) in measured.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Floors floor = baseline.Modules[name];
            text.Append(c, $"| {name} | {module.LinePercent:F2}% ({module.LinesCovered}/{module.LinesValid}) | {floor.Line:F2}% | {module.BranchPercent:F2}% ({module.BranchesCovered}/{module.BranchesValid}) | {floor.Branch:F2}% |\n");
        }
        text.Append("\nGate: ").Append(failures.Count == 0 ? "PASS" : "FAIL").Append('\n');
        foreach (string failure in failures) text.Append("- ").Append(failure).Append('\n');
        text.Append(c, $"\nIncludes the {measured.Count} modern production modules only. Test, legacy, third-party and generated sources are excluded.\n");
        text.Append("Coverage measures executed source sequence points and branch outcomes; it does not certify audio-device, UI or release acceptance.\n");
        return text.ToString();
    }
}
