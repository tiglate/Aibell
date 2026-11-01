using System.Text.Json;
using System.Xml.Linq;
using NUnit.Framework;

namespace AibellTools.Tests;

[TestFixture]
public sealed class CoverageGateTests
{
    private const string Module = "Aibell.Domain";
    private TempFolder folder = null!;
    private string path = "";

    [SetUp]
    public void Create()
    {
        folder = new TempFolder();
        path = folder.Combine("coverage.xml");
    }

    [TearDown]
    public void Remove() => folder.Dispose();

    private static XElement Report()
    {
        XElement lines = new("lines",
            new XElement("line", new XAttribute("number", "1"), new XAttribute("hits", "1"), new XAttribute("branch", "True"), new XAttribute("condition-coverage", "50% (1/2)")),
            new XElement("line", new XAttribute("number", "2"), new XAttribute("hits", "0"), new XAttribute("branch", "False")));
        // The method list duplicates canonical class lines and must not count twice.
        XElement methods = new("methods", new XElement("method", new XElement("lines", new XElement("line", new XAttribute("number", "1"), new XAttribute("hits", "100")))));
        return new XElement("coverage", new XAttribute("line-rate", "0.5"), new XAttribute("branch-rate", "0.5"), new XAttribute("lines-covered", "1"),
            new XAttribute("lines-valid", "2"), new XAttribute("branches-covered", "1"), new XAttribute("branches-valid", "2"),
            new XElement("packages", new XElement("package", new XAttribute("name", Module), new XAttribute("line-rate", "0.5"), new XAttribute("branch-rate", "0.5"),
                new XElement("classes", new XElement("class", new XAttribute("filename", "/_/src/Aibell.Domain/Rules.cs"), methods, lines)))));
    }

    private void Save(XElement root) => new XDocument(root).Save(path);

    private static CoverageGate.Baseline BaselineWith(string? lineFloor = null, string? branchFloor = null, string schema = "1")
    {
        string json = "{\"schemaVersion\":" + schema + ",\"modules\":{\"" + Module + "\":{\"linePercentMinimum\":" + (lineFloor ?? "50") + ",\"branchPercentMinimum\":" + (branchFloor ?? "50") + "}}}";
        using JsonDocument document = JsonDocument.Parse(json);
        return CoverageGate.Baseline.Parse(document.RootElement);
    }

    private static readonly HashSet<string> Expected = [Module];

    [Test]
    public void Measure_CanonicalClassLinesAndBranchOutcomes_MatchRootCounts()
    {
        Save(Report());

        Dictionary<string, CoverageGate.Module> measured = CoverageGate.Measure(path, Expected);

        Assert.That(measured[Module], Is.EqualTo(new CoverageGate.Module(1, 2, 1, 2)));
        Assert.That(measured[Module].LinePercent, Is.EqualTo(50));
        Assert.That(CoverageGate.Evaluate(measured, BaselineWith()), Is.Empty);
        Assert.That(CoverageGate.Markdown(measured, BaselineWith(), []), Does.Contain("Gate: PASS"));
    }

    [TestCase("51", "50", "linePercent")]
    [TestCase("50", "51", "branchPercent")]
    public void Evaluate_LineAndBranchFloors_FailIndependently(string lineFloor, string branchFloor, string metric)
    {
        Save(Report());
        Dictionary<string, CoverageGate.Module> measured = CoverageGate.Measure(path, Expected);
        CoverageGate.Baseline baseline = BaselineWith(lineFloor, branchFloor);

        List<string> failures = CoverageGate.Evaluate(measured, baseline);

        Assert.That(failures, Has.Count.EqualTo(1));
        Assert.That(failures[0], Does.Contain(Module).And.Contain(metric));
        Assert.That(CoverageGate.Markdown(measured, baseline, failures), Does.Contain("Gate: FAIL"));
    }

    [TestCase("empty")]
    [TestCase("extra")]
    [TestCase("duplicate")]
    [TestCase("generated")]
    [TestCase("tests")]
    [TestCase("legacy")]
    public void Measure_EmptyExtraDuplicateAndGeneratedSourceEvidence_IsRejected(string attack)
    {
        XElement root = Report();
        XElement packages = root.Element("packages")!;
        XElement package = packages.Element("package")!;
        switch (attack)
        {
            case "empty": package.Remove(); break;
            case "extra": package.SetAttributeValue("name", "Aibell.Unexpected"); break;
            case "duplicate": packages.Add(new XElement(package)); break;
            default:
                package.Element("classes")!.Element("class")!.SetAttributeValue("filename", attack switch
                {
                    "generated" => "/_/src/Aibell.Domain/obj/generated.cs",
                    "tests" => "/_/tests/Aibell.Domain.Tests/Tests.cs",
                    _ => "/_/src/Core/Legacy.cs"
                });
                break;
        }
        Save(root);

        Assert.Throws<InvalidDataException>(() => CoverageGate.Measure(path, Expected));
    }

    [TestCase("nan")]
    [TestCase("wrong-rate")]
    [TestCase("root-total")]
    [TestCase("negative-hits")]
    [TestCase("missing-hits")]
    [TestCase("missing-branch")]
    [TestCase("invalid-branch")]
    public void Measure_MalformedRatesCountsAndBranchDetails_AreRejected(string attack)
    {
        XElement root = Report();
        XElement package = root.Element("packages")!.Element("package")!;
        XElement line = package.Element("classes")!.Element("class")!.Element("lines")!.Elements("line").First();
        switch (attack)
        {
            case "nan": package.SetAttributeValue("line-rate", "nan"); break;
            case "wrong-rate": package.SetAttributeValue("line-rate", "1"); break;
            case "root-total": root.SetAttributeValue("lines-valid", "3"); break;
            case "negative-hits": line.SetAttributeValue("hits", "-1"); break;
            case "missing-hits": line.Attribute("hits")!.Remove(); break;
            case "missing-branch": line.Attribute("condition-coverage")!.Remove(); break;
            default: line.SetAttributeValue("condition-coverage", "150% (3/2)"); break;
        }
        Save(root);

        Assert.Throws<InvalidDataException>(() => CoverageGate.Measure(path, Expected));
    }

    [TestCase("-1")]
    [TestCase("101")]
    [TestCase("true")]
    [TestCase("\"50\"")]
    public void Baseline_NonNumericOrOutOfRangeFloors_AreRejected(string floor)
    {
        Assert.Throws<InvalidDataException>(() => BaselineWith(lineFloor: floor));
    }

    [TestCase("true")]
    [TestCase("2")]
    public void Baseline_UnknownSchema_IsRejected(string schema)
    {
        Assert.Throws<InvalidDataException>(() => BaselineWith(schema: schema));
    }

    [Test]
    public void Evaluate_ModuleScopeMismatch_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => CoverageGate.Evaluate(new Dictionary<string, CoverageGate.Module>(), BaselineWith()));
    }
}
