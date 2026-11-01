#!/usr/bin/env pwsh
# Keep in sync with make.sh: every action, environment variable and argument rule must match.
# Only platform-specific details (shell syntax, interpreter name) may differ.

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Action = '',

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$solution = Join-Path $repoRoot 'Aibell.slnx'
$desktopProject = Join-Path $repoRoot 'src/Aibell.Desktop/Aibell.Desktop.csproj'
$configuration = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Debug' }

function Show-Usage {
    Write-Host 'Usage: ./make.ps1 {build|run|test|coverage|secrets|radio-check|format|clean|publish|deb|appimage|flatpak|snap|msi} [arguments]'
    Write-Host 'Set CONFIGURATION=Release to override the default Debug configuration.'
    Write-Host 'Set RID (for example win-x64) to override publish runtime detection.'
    Write-Host 'deb, appimage, flatpak and snap build Linux x64 packages into artifacts/packages (extra arguments go to the packaging tool command).'
    Write-Host 'msi builds a Windows x64 installer (WiX) into artifacts/packages and runs only on Windows x64.'
}

function Get-RuntimeId {
    # Windows PowerShell 5.1 (.NET Framework) has no RuntimeInformation.OSArchitecture and always runs on Windows.
    if ($PSVersionTable.PSVersion.Major -lt 6) {
        $native = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
        switch ($native) {
            'AMD64' { return 'win-x64' }
            'ARM64' { return 'win-arm64' }
            default { throw "Unsupported architecture: $native" }
        }
    }

    $architecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported architecture: $_" }
    }

    $platform = [System.Runtime.InteropServices.OSPlatform]
    $info = [System.Runtime.InteropServices.RuntimeInformation]
    if ($info::IsOSPlatform($platform::Linux)) { return "linux-$architecture" }
    if ($info::IsOSPlatform($platform::Windows)) { return "win-$architecture" }
    throw 'Unsupported operating system.'
}

# Runs a native command and stops the script with its exit code on failure (the equivalent of set -e).
function Invoke-Native {
    param([string]$Command, [string[]]$CommandArguments)
    & $Command @CommandArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

switch ($Action) {
    'build' {
        Invoke-Native dotnet (@('build', $solution, '--configuration', $configuration, '--maxcpucount:1') + $Arguments)
    }
    'run' {
        Invoke-Native dotnet @('build', $desktopProject, '--configuration', $configuration, '--maxcpucount:1')
        Invoke-Native dotnet (@('run', '--project', $desktopProject, '--configuration', $configuration, '--no-build', '--') + $Arguments)
    }
    'test' {
        Invoke-Native dotnet (@('test', $solution, '--configuration', $configuration, '--maxcpucount:1') + $Arguments)
    }
    'coverage' {
        if ((Get-RuntimeId) -ne 'linux-x64' -or $configuration -ne 'Debug') {
            [Console]::Error.WriteLine('The coverage baseline uses Linux x64 and CONFIGURATION=Debug.')
            exit 2
        }
        Set-Location $repoRoot
        Invoke-Native dotnet @('tool', 'restore')
        Invoke-Native dotnet (@('build', $solution, '--configuration', 'Debug', '--maxcpucount:1') + $Arguments)
        $env:DOTNET_COVERAGE_TELEMETRY_OPTOUT = '1'
        $env:AIBELL_NATIVE_PLAYBACK_TESTS = '1'
        $env:AIBELL_METADATA_WRITE_TESTS = '1'
        $env:AIBELL_LINUX_INTEGRATION_TESTS = '1'
        Invoke-Native dotnet @('tool', 'run', 'dotnet-coverage', '--', 'collect', '--settings', 'tests/coverage.settings.xml',
            '--output-format', 'cobertura', '--output', 'artifacts/coverage/modern.cobertura.xml', '--nologo',
            (Join-Path $repoRoot 'make.ps1'), 'test', '--no-build', '--no-restore')
        Invoke-Native dotnet @('run', '--project', 'tools/AibellTools', '--', 'coverage-gate', 'artifacts/coverage/modern.cobertura.xml')
    }
    'secrets' {
        Set-Location $repoRoot
        Invoke-Native dotnet (@('run', '--project', 'tools/AibellTools', '--', 'secrets', '--download') + $Arguments)
    }
    'radio-check' {
        Set-Location $repoRoot
        Invoke-Native dotnet (@('run', '--project', 'tools/AibellTools', '--', 'radio-check') + $Arguments)
    }
    'format' {
        Invoke-Native dotnet (@('format', 'whitespace', $solution) + $Arguments)
    }
    'clean' {
        Invoke-Native dotnet (@('clean', $solution, '--configuration', $configuration, '--maxcpucount:1') + $Arguments)
        $artifactsDir = Join-Path $repoRoot 'artifacts'
        if ($artifactsDir -ne (Join-Path $repoRoot 'artifacts')) {
            [Console]::Error.WriteLine("Refusing to clean unexpected path: $artifactsDir")
            exit 1
        }
        if (Test-Path -LiteralPath $artifactsDir) { Remove-Item -LiteralPath $artifactsDir -Recurse -Force }
    }
    'publish' {
        $runtimeId = if ($env:RID) { $env:RID } else { Get-RuntimeId }
        Invoke-Native dotnet (@('publish', $desktopProject,
                '--configuration', 'Release',
                '--runtime', $runtimeId,
                '--self-contained', 'true',
                "-p:NuGetLockFilePath=packages.$runtimeId.lock.json",
                '--maxcpucount:1',
                '--output', (Join-Path $repoRoot "artifacts/$runtimeId")) + $Arguments)
    }
    { $_ -in 'deb', 'appimage', 'flatpak', 'snap' } {
        if ((Get-RuntimeId) -ne 'linux-x64') {
            [Console]::Error.WriteLine('Linux packages are built for Linux x64 on a Linux x64 machine.')
            exit 2
        }
        $publishDir = Join-Path $repoRoot 'artifacts/deb-publish'
        if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
        Invoke-Native dotnet @('publish', $desktopProject,
            '--configuration', 'Release',
            '--runtime', 'linux-x64',
            '--self-contained', 'true',
            '-p:PublishReadyToRun=true',
            '-p:DebugType=none',
            '-p:DebugSymbols=false',
            '-p:NuGetLockFilePath=packages.linux-x64.lock.json',
            '--maxcpucount:1',
            '--output', $publishDir)
        Set-Location $repoRoot
        Invoke-Native dotnet (@('run', '--project', 'tools/AibellTools', '--', $Action, '--publish-dir', $publishDir) + $Arguments)
    }
    'msi' {
        if ((Get-RuntimeId) -ne 'win-x64') {
            [Console]::Error.WriteLine('The MSI installer is built for Windows x64 on a Windows x64 machine.')
            exit 2
        }
        $publishDir = Join-Path $repoRoot 'artifacts/msi-publish'
        if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
        Invoke-Native dotnet @('publish', $desktopProject,
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '-p:PublishReadyToRun=true',
            '-p:DebugType=none',
            '-p:DebugSymbols=false',
            '-p:NuGetLockFilePath=packages.win-x64.lock.json',
            '--maxcpucount:1',
            '--output', $publishDir)
        Set-Location $repoRoot
        Invoke-Native dotnet @('tool', 'restore')
        Invoke-Native dotnet (@('run', '--project', 'tools/AibellTools', '--', 'msi', '--publish-dir', $publishDir) + $Arguments)
    }
    default {
        Show-Usage
        exit 2
    }
}
