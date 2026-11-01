namespace AibellTools;

internal static class Program
{
    private const string Usage = """
        Usage: dotnet run --project tools/AibellTools -- <command> [arguments]

          secrets [--download] [--scanner <path>] [--output <dir>]
              Scan the tracked source files with the pinned Gitleaks.
          radio-check [--only <country code or id>] [--timeout <seconds>] [--concurrency <n>] [--logos] [--catalog <json>]
              Fetch every curated radio station and report the ones that no longer deliver audio (needs the network).
          coverage-gate <cobertura.xml> [--baseline <json>] [--json <path>] [--markdown <path>]
              Check per-module coverage against tests/coverage-baseline.json.
          deb [--publish-dir <dir>] [--output <dir>] [--deb-version <version>]
              Build a Debian/Ubuntu .deb (amd64) from an optimized linux-x64 publish folder.
          appimage [--download-runtime | --runtime <file>] [--publish-dir <dir>] [--output <dir>]
              Build an AppImage from the same optimized publish folder, using the pinned type-2 AppImage runtime.
          flatpak [--stage-only] [--runtime-version <24.08>] [--publish-dir <dir>] [--output <dir>]
              Build a Flatpak bundle with flatpak-builder (needs the freedesktop Platform and Sdk runtimes).
          snap [--stage-only] [--publish-dir <dir>] [--output <dir>]
              Build a snap with snapcraft (core24, gnome extension); --stage-only writes just the snapcraft project.
          msi [--publish-dir <dir>] [--output <dir>]
              Build a per-machine Windows Installer package (.msi, x64) from a self-contained win-x64 publish folder with WiX.
          package --rid <linux-x64|win-x64> [--publish-dir <dir>] [--output <dir>] [--bundle-libvlc <dir>]
              Build an auditable candidate archive from a self-contained publish folder.
          smoke-linux [binary] [--profile-override] [--media-session] [--keyboard-close] [--sigterm]
              Start a Linux publish under Xvfb (run it with xvfb-run -a) and exercise it.
        """;

    public static int Main(string[] args)
    {
        string[] rest = args.Length > 1 ? args[1..] : [];
        try
        {
            return args.FirstOrDefault() switch
            {
                "secrets" => SecretScan.Run(rest),
                "radio-check" => RadioCheck.Run(rest),
                "coverage-gate" => CoverageGate.Run(rest),
                "package" => Packager.Run(rest),
                "msi" => MsiPackager.Run(rest),
                "deb" => DebPackager.Run(rest),
                "appimage" => AppImagePackager.Run(rest),
                "flatpak" => FlatpakPackager.Run(rest),
                "snap" => SnapPackager.Run(rest),
                "smoke-linux" => LinuxSmoke.Run(rest),
                _ => Fail(Usage, 2)
            };
        }
        catch (ToolException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static int Fail(string message, int code)
    {
        Console.Error.WriteLine(message);
        return code;
    }
}

/// <summary>A controlled failure whose message is safe to show: it never contains matched secrets or file contents.</summary>
internal class ToolException(string message, Exception? inner = null) : Exception(message, inner);
