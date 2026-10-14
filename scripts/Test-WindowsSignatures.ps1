#Requires -Version 7.0
# Verifies a signed release: every OpenSkiTime binary in the app directory, the installer, and the copies inside
# the portable ZIP carry a valid, timestamped signature from the pinned certificate.
[CmdletBinding()]
param([Parameter(Mandatory)][string] $PackageDirectory, [Parameter(Mandatory)][string] $Version)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsSigning.psm1') -Force
$package = [IO.Path]::GetFullPath($PackageDirectory)
$installer = Join-Path $package "OpenSkiTime-$Version-win-x64-setup.exe"
$zip = Join-Path $package "OpenSkiTime-$Version-win-x64.zip"
Assert-OpenSkiTimeSignature (@(Get-OpenSkiTimeBinaries (Join-Path $package 'app') | ForEach-Object FullName) + $installer)
$extracted = Join-Path ([IO.Path]::GetTempPath()) ('OpenSkiTime-zip-' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $zip -DestinationPath $extracted
    Assert-OpenSkiTimeSignature @(Get-OpenSkiTimeBinaries $extracted | ForEach-Object FullName)
} finally {
    Remove-Item -LiteralPath $extracted -Recurse -Force -ErrorAction SilentlyContinue
}
