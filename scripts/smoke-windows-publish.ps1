param([string]$Binary = (Join-Path $PSScriptRoot "../artifacts/win-x64/Aibell.Desktop.exe"))
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class AibellNativeSmoke {
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
"@

function Get-SmokeSchema([string]$Database) {
    $stream = $null
    try {
        $stream = [IO.FileStream]::new($Database, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $header = [byte[]]::new(64)
        if ($stream.Read($header, 0, 64) -ne 64) { return -1 }
        $schema = 0
        foreach ($index in 60..63) { $schema = ($schema -shl 8) -bor $header[$index] }
        return $schema
    } catch [IO.IOException] { return -1 }
    finally { if ($stream) { $stream.Dispose() } }
}

$binaryPath = (Resolve-Path $Binary).Path
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ("aibell-windows-publish-" + [Guid]::NewGuid().ToString("N"))
$smokeProfile = Join-Path $smokeRoot "isolated-profile"
New-Item -ItemType Directory -Path $smokeRoot | Out-Null
$info = [Diagnostics.ProcessStartInfo]::new($binaryPath)
$info.WorkingDirectory = $smokeRoot
$info.UseShellExecute = $false
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.Environment["AIBELL_DATA_DIRECTORY"] = $smokeProfile
$info.Environment["DOTNET_ROOT"] = Join-Path $smokeRoot "no-system-dotnet"
$info.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"
$application = $null
try {
    $application = [Diagnostics.Process]::Start($info)
    $outputTask = $application.StandardOutput.ReadToEndAsync()
    $errorTask = $application.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 100
        $application.Refresh()
        if ($application.HasExited) { throw "Published application exited before opening its window." }
    } while ($application.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($application.MainWindowHandle -eq [IntPtr]::Zero) { throw "Published application did not open a native window." }
    $database = Join-Path $smokeProfile "library.db"
    while ((Get-SmokeSchema $database) -lt 13 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if ((Get-SmokeSchema $database) -lt 13) { throw "The isolated library did not initialize." }
    if (-not [AibellNativeSmoke]::PostMessage($application.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
        throw "Could not deliver the native close request."
    }
    if (-not $application.WaitForExit(30000)) { throw "Published application did not shut down gracefully." }
    if ($application.ExitCode -ne 0) { throw "Published application exited $($application.ExitCode)." }
    $bytes = [IO.File]::ReadAllBytes($database)
    if ($bytes.Length -lt 100 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 15) -ne "SQLite format 3") {
        throw "The isolated library has an invalid SQLite header."
    }
    $schema = 0
    foreach ($index in 60..63) { $schema = ($schema -shl 8) -bor $bytes[$index] }
    if ($schema -lt 13) { throw "The isolated library schema is $schema, expected at least 13." }
    Write-Output "Self-contained Windows publish: native startup, graceful close and isolated library schema $schema passed."
} catch {
    if ($application -and -not $application.HasExited) { $application.Kill($true); $application.WaitForExit() }
    if ($application) {
        Write-Output $outputTask.GetAwaiter().GetResult()
        Write-Output $errorTask.GetAwaiter().GetResult()
    }
    throw
} finally {
    if ($application) { $application.Dispose() }
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force
}
