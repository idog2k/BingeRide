# Builds dist\BingeRide-Setup.exe from a published BingeRide.exe. Runs on Windows.
# Inno Setup comes from its NuGet package, so nothing needs to be installed first.
# Usage: scripts/build-installer.ps1 -Version 0.1.0 [-Exe path\to\BingeRide.exe]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Exe
)
$ErrorActionPreference = 'Stop'
# Set here, not in param(): Windows PowerShell 5.1 leaves $PSScriptRoot empty there.
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\publish\win-x64\BingeRide.exe' }

$innoVersion = '7.1.0'
$tool = Join-Path $env:TEMP "innosetup-$innoVersion"
$iscc = Join-Path $tool 'tools\ISCC.exe'
if (-not (Test-Path $iscc)) {
    $zip = "$tool.zip"
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Tools.InnoSetup/$innoVersion" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $tool -Force
    Remove-Item $zip
}

$repo = Split-Path $PSScriptRoot -Parent
& $iscc "/DAppVersion=$Version" "/DSourceExe=$((Resolve-Path $Exe).ProviderPath)" "/O$repo\dist" "$repo\installer\BingeRide.iss"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
