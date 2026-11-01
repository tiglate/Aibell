# Builds the Aibell audio shim (a thin C layer over the vendored miniaudio) with the Visual C++ compiler.
# Usage: scripts/build-native-audio.ps1 -OutputDirectory <directory>
param([Parameter(Mandatory = $true)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'native/aibell-audio/aibell_audio.c'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

$cl = Get-Command cl.exe -ErrorAction SilentlyContinue
if (-not $cl) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'Visual Studio with the C++ build tools is required to build the Aibell audio library.' }
    $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $install) { throw 'Visual Studio with the C++ build tools is required to build the Aibell audio library.' }
    $architecture = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvarsall.bat'
    $environment = cmd /c "`"$vcvars`" $architecture >nul && set"
    foreach ($line in $environment) {
        if ($line -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($Matches[1])" -Value $Matches[2] }
    }
}

$objects = Join-Path $OutputDirectory 'obj'
New-Item -ItemType Directory -Force -Path $objects | Out-Null
& cl.exe /nologo /O2 /LD /utf-8 "/Fo$objects\" "/Fe$(Join-Path $OutputDirectory 'aibell_audio.dll')" $source
if ($LASTEXITCODE -ne 0) { throw 'Building the Aibell audio library failed.' }
Remove-Item -Recurse -Force $objects
Get-ChildItem $OutputDirectory -Include '*.exp', '*.lib' -Recurse | Remove-Item -Force
