using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AibellTools;

/// <summary>
/// Builds a per-machine Windows Installer package (<c>.msi</c>) from a self-contained win-x64 publish folder with the WiX toolset
/// (a repository-local dotnet tool, see <c>.config/dotnet-tools.json</c>). The WiX source is generated here so it can be tested without WiX.
/// </summary>
internal static partial class MsiPackager
{
    /// <summary>Fixed for the life of the product: it is how a newer package finds and replaces an older install.</summary>
    internal const string UpgradeCode = "6F2B8D1E-4C3A-4E57-9B0D-7A1C5E92D3F4";

    internal const string WixVersion = "5.0.2";
    private const string WixNamespace = "http://wixtoolset.org/schemas/v4/wxs";
    private const string WixUiNamespace = "http://wixtoolset.org/schemas/v4/wxs/ui";
    private const string IconId = "AibellIcon.ico";
    // Same ID as WindowsAppIdentity.Register("Aibell.Desktop", ...) in Aibell.Desktop/Program.cs.
    internal const string AppUserModelId = "Aibell.Desktop";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Publish files that only serve crash dumps and debugging; the installer leaves them out.</summary>
    private static readonly HashSet<string> Unneeded = new(StringComparer.OrdinalIgnoreCase) { "createdump.exe" };

    public static int Run(string[] args)
    {
        string root = RepositoryRoot.Find();
        Options options = new(args, new HashSet<string>(), new HashSet<string> { "--publish-dir", "--output" });
        try
        {
            Packager.Require(OperatingSystem.IsWindows(), "MSI installers are built on Windows.");
            string path = Build(Path.GetFullPath(options.Value("--publish-dir") ?? Path.Combine(root, "artifacts", "msi-publish")),
                Path.GetFullPath(options.Value("--output") ?? Path.Combine(root, "artifacts/packages")), root,
                Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages"),
                BuildWithWix);
            Console.WriteLine(path);
            return 0;
        }
        catch (Exception exception) when (exception is PackagingException or IOException or KeyNotFoundException or JsonException or FormatException)
        {
            throw new ToolException(exception.Message, exception);
        }
    }

    /// <summary>Compiles a WiX source file into an installer; the first argument is the source, the second the output path.</summary>
    internal delegate void WixCompiler(string source, string output, string repository);

    internal static string Build(string source, string output, string repository, string packageCache, WixCompiler compile)
    {
        Packager.Require(!RepositoryRoot.IsLink(source), "Publish input must not be a symbolic link.");
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        Packager.Require(output != source && !output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Package output must be outside the publish input.");

        List<string> files = Packager.PayloadFiles(source, "win-x64");
        string appVersion = ReadAppVersion(source);
        string version = ProductVersion(appVersion);
        string msi = Path.Combine(output, $"Aibell-{appVersion}-x64.msi");
        if (File.Exists(msi)) throw new IOException("The .msi already exists; choose a fresh output directory.");

        string work = Path.Combine(Path.GetTempPath(), "aibell-msi-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            SortedDictionary<string, string> payload = new(StringComparer.Ordinal);
            foreach (string path in files)
            {
                string relative = Path.GetRelativePath(source, path).Replace('\\', '/');
                if (Unneeded.Contains(relative) || relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                payload[relative] = path;
            }

            string licenses = Path.Combine(work, "licenses");
            Directory.CreateDirectory(licenses);
            StageLicenses(source, repository, packageCache, licenses, payload);

            string rtf = Path.Combine(work, "license.rtf");
            File.WriteAllText(rtf, Rtf(File.ReadAllText(Path.Combine(repository, "LICENSE"))), Encoding.ASCII);
            string wxs = Path.Combine(work, "Aibell.wxs");
            File.WriteAllText(wxs, Source(payload, version, appVersion, Path.Combine(repository, "data", "icons", "aibell.ico"), rtf), new UTF8Encoding(false));

            Directory.CreateDirectory(output);
            compile(wxs, msi, repository);
            if (!File.Exists(msi)) throw new PackagingException("WiX did not produce an installer.");
            return msi;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    internal static string ReadAppVersion(string source)
    {
        using JsonDocument deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "Aibell.Desktop.deps.json")));
        return deps.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(item => item.Name.StartsWith("Aibell.Desktop/", StringComparison.Ordinal)).Select(item => item.Name["Aibell.Desktop/".Length..].Split('+')[0]).SingleOrDefault()
            ?? throw new PackagingException("Cannot determine the compiled application version.");
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$")]
    private static partial Regex SemanticVersion();

    /// <summary>The numeric <c>major.minor.build</c> Windows Installer compares for upgrades; a pre-release suffix is dropped.</summary>
    internal static string ProductVersion(string appVersion)
    {
        Match match = SemanticVersion().Match(appVersion);
        Packager.Require(match.Success, $"Not a version Windows Installer can use: {appVersion}");
        bool fits = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) <= 255 && int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) <= 255
            && match.Groups[3].Value.Length <= 5 && int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) <= 65535;
        Packager.Require(fits, $"Windows Installer allows at most 255.255.65535: {appVersion}");
        return $"{int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)}.{int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)}.{int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)}";
    }

    private static void StageLicenses(string source, string repository, string packageCache, string licenses, SortedDictionary<string, string> payload)
    {
        void Add(string name, string path)
        {
            if (File.Exists(path)) payload["licenses/" + name] = path;
        }
        Add("LICENSE", Path.Combine(repository, "LICENSE"));
        Add("COPYING.Opus", Path.Combine(repository, "src", "Aibell.Audio.Opus", "COPYING.Opus"));
        Add("COPYING.Rar", Path.Combine(repository, "src", "Aibell.Archive.Rar", "COPYING.Rar"));
        string alac = Path.Combine(repository, "src", "Aibell.Audio.Alac");
        Add("LICENSE.Alac", Path.Combine(alac, "LICENSE.Alac"));
        Add("NOTICE.Alac", Path.Combine(alac, "NOTICE"));

        using JsonDocument deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "Aibell.Desktop.deps.json")));
        // DependencyNotices writes its own dependency-notices folder below the directory it is given.
        List<Dictionary<string, object?>> inventory = Packager.DependencyNotices(deps.RootElement.GetProperty("libraries"), packageCache, licenses);
        foreach (string path in Directory.EnumerateFiles(licenses, "*", SearchOption.AllDirectories))
            payload["licenses/" + Path.GetRelativePath(licenses, path).Replace('\\', '/')] = path;
        string inventoryPath = Path.Combine(licenses, "dependency-inventory.json");
        File.WriteAllText(inventoryPath, JsonSerializer.Serialize(inventory, Indented) + "\n", new UTF8Encoding(false));
        payload["licenses/dependency-inventory.json"] = inventoryPath;
    }

    /// <summary>The WiX source for a per-machine install of <paramref name="payload"/> (relative path to full source path) under Program Files.</summary>
    internal static string Source(IReadOnlyDictionary<string, string> payload, string version, string appVersion, string iconPath, string licenseRtf)
    {
        Packager.Require(payload.ContainsKey("Aibell.Desktop.exe"), "The application host is missing from the installer payload.");
        XNamespace wix = WixNamespace, ui = WixUiNamespace;

        // Directories, one per distinct folder, with an id that depends only on the path so rebuilds stay stable.
        SortedSet<string> folders = new(StringComparer.Ordinal);
        foreach (string relative in payload.Keys)
            for (string? folder = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar)); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
                folders.Add(folder.Replace('\\', '/'));

        XElement install = new(wix + "Directory", new XAttribute("Id", "INSTALLFOLDER"), new XAttribute("Name", "Aibell"));
        Dictionary<string, XElement> directories = new(StringComparer.Ordinal) { [""] = install };
        foreach (string folder in folders)
        {
            string parent = folder.Contains('/') ? folder[..folder.LastIndexOf('/')] : "";
            XElement element = new(wix + "Directory", new XAttribute("Id", Id("d", folder)), new XAttribute("Name", folder[(folder.LastIndexOf('/') + 1)..]));
            directories[parent].Add(element);
            directories[folder] = element;
        }

        XElement components = new(wix + "ComponentGroup", new XAttribute("Id", "AppFiles"));
        foreach ((string relative, string full) in payload)
        {
            string folder = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            bool host = relative == "Aibell.Desktop.exe";
            XElement file = new(wix + "File", new XAttribute("Id", host ? "AibellExe" : Id("f", relative)), new XAttribute("Name", relative[(relative.LastIndexOf('/') + 1)..]),
                new XAttribute("Source", full), new XAttribute("KeyPath", "yes"));
            if (host)
                // The shell labels the media card, taskbar and notifications from the shortcut that carries the process's
                // AppUserModelID; the value must match WindowsAppIdentity.Register in Program.Main.
                file.Add(new XElement(wix + "Shortcut", new XAttribute("Id", "StartMenuShortcut"), new XAttribute("Directory", "ProgramMenuFolder"), new XAttribute("Name", "Aibell"),
                    new XAttribute("Description", "Music library and player"), new XAttribute("WorkingDirectory", "INSTALLFOLDER"), new XAttribute("Icon", IconId),
                    new XElement(wix + "ShortcutProperty", new XAttribute("Id", "AppUserModelId"), new XAttribute("Key", "System.AppUserModel.ID"), new XAttribute("Value", AppUserModelId))));
            components.Add(new XElement(wix + "Component", new XAttribute("Id", Id("c", relative)),
                new XAttribute("Directory", folder.Length == 0 ? "INSTALLFOLDER" : Id("d", folder)), file));
        }

        XElement package = new(wix + "Package",
            new XAttribute("Name", "Aibell"), new XAttribute("Manufacturer", "Tiglate Pileser III"), new XAttribute("Version", version),
            new XAttribute("UpgradeCode", UpgradeCode), new XAttribute("Scope", "perMachine"),
            new XElement(wix + "SummaryInformation", new XAttribute("Description", "Aibell music library and player")),
            new XElement(wix + "MajorUpgrade", new XAttribute("DowngradeErrorMessage", "A newer version of Aibell is already installed.")),
            new XElement(wix + "MediaTemplate", new XAttribute("EmbedCab", "yes")),
            new XElement(wix + "Icon", new XAttribute("Id", IconId), new XAttribute("SourceFile", iconPath)),
            Property("ARPPRODUCTICON", IconId), Property("ARPCOMMENTS", "Aibell " + appVersion),
            Property("ARPURLINFOABOUT", "https://github.com/tiglate/Aibell"),
            new XElement(wix + "WixVariable", new XAttribute("Id", "WixUILicenseRtf"), new XAttribute("Value", licenseRtf)),
            new XElement(ui + "WixUI", new XAttribute("Id", "WixUI_Minimal")),
            new XElement(wix + "StandardDirectory", new XAttribute("Id", "ProgramFiles64Folder"), install),
            new XElement(wix + "StandardDirectory", new XAttribute("Id", "ProgramMenuFolder")),
            components,
            new XElement(wix + "Feature", new XAttribute("Id", "Main"), new XAttribute("Title", "Aibell"), new XAttribute("Level", "1"),
                new XElement(wix + "ComponentGroupRef", new XAttribute("Id", "AppFiles"))));
        XDocument document = new(new XDeclaration("1.0", "utf-8", null), new XElement(wix + "Wix", new XAttribute(XNamespace.Xmlns + "ui", WixUiNamespace), package));
        return document.Declaration + Environment.NewLine + document.Root;

        XElement Property(string id, string value) => new(wix + "Property", new XAttribute("Id", id), new XAttribute("Value", value));
    }

    // Identifiers must be unique, start with a letter or underscore and stay under 72 characters; a hash of the path does that.
    internal static string Id(string prefix, string relative) =>
        prefix + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative.ToLowerInvariant())))[..24];

    /// <summary>The license text as the RTF the installer's license page shows.</summary>
    internal static string Rtf(string text)
    {
        StringBuilder rtf = new(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs20 ");
        foreach (char character in text.Replace("\r\n", "\n"))
        {
            switch (character)
            {
                case '\\': rtf.Append(@"\\"); break;
                case '{': rtf.Append(@"\{"); break;
                case '}': rtf.Append(@"\}"); break;
                case '\n': rtf.Append(@"\par "); break;
                case '\t': rtf.Append(@"\tab "); break;
                case < ' ': break;
                case > '~': rtf.Append(CultureInfo.InvariantCulture, $"\\u{(short)character}?"); break;
                default: rtf.Append(character); break;
            }
        }
        return rtf.Append('}').ToString();
    }

    private static void BuildWithWix(string source, string output, string repository)
    {
        Packager.Require(OperatingSystem.IsWindows(), "MSI installers are built on Windows.");
        (int listed, string extensions, _) = RepositoryRoot.Run("dotnet", ["tool", "run", "wix", "extension", "list"], repository);
        if (listed != 0 || !extensions.Contains("WixToolset.UI.wixext " + WixVersion, StringComparison.Ordinal))
        {
            (int added, _, string addError) = RepositoryRoot.Run("dotnet", ["tool", "run", "wix", "extension", "add", "WixToolset.UI.wixext/" + WixVersion], repository);
            Packager.Require(added == 0, "Could not add the WiX UI extension (run `dotnet tool restore` first): " + addError.Trim());
        }
        (int code, string stdout, string stderr) = RepositoryRoot.Run("dotnet",
            ["tool", "run", "wix", "build", "-arch", "x64", "-ext", "WixToolset.UI.wixext/" + WixVersion, "-o", output, source], repository, timeout: TimeSpan.FromMinutes(15));
        Packager.Require(code == 0, "WiX failed:\n" + stdout.Trim() + "\n" + stderr.Trim());
    }
}
