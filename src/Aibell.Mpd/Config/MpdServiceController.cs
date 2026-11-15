using System.Diagnostics;

namespace Aibell.Mpd.Config;

public sealed record MpdProcessResult(int ExitCode, string Output);

/// <summary>
/// Restarts MPD so it reads a changed configuration, when it runs as a systemd user service (<c>mpd.service</c>). It never starts,
/// stops or signals anything on its own; the caller runs <see cref="RestartAsync"/> only after the user asks.
/// </summary>
public sealed class MpdServiceController
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<MpdProcessResult>> run;

    public MpdServiceController(Func<IReadOnlyList<string>, CancellationToken, Task<MpdProcessResult>>? runner = null) =>
        run = runner ?? RunSystemctlAsync;

    /// <summary>True when a user unit named <c>mpd.service</c> exists.</summary>
    public async Task<bool> IsUserServiceAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            return (await run(["--user", "cat", "mpd.service"], cancellationToken).ConfigureAwait(false)).ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>Restarts the user service.</summary>
    /// <returns>An empty string on success, otherwise what <c>systemctl</c> printed.</returns>
    public async Task<string> RestartAsync(CancellationToken cancellationToken = default)
    {
        MpdProcessResult result = await run(["--user", "restart", "mpd.service"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? string.Empty : result.Output.Trim().Length > 0 ? result.Output.Trim() : $"systemctl exited with code {result.ExitCode}.";
    }

    private static async Task<MpdProcessResult> RunSystemctlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using Process process = new() { StartInfo = new ProcessStartInfo("systemctl", arguments) { RedirectStandardOutput = true, RedirectStandardError = true } };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new MpdProcessResult(process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }
}
