#Requires -Version 7.0
# Shared rules for Windows release signing: which files are OpenSkiTime's own, and what a valid signature is.
$ErrorActionPreference = 'Stop'

# Only binaries built from this repository are signed. The .NET runtime keeps Microsoft's signatures, and
# third-party libraries keep whatever their publishers shipped; they are never re-signed as OpenSkiTime.
function Get-OpenSkiTimeBinaries([Parameter(Mandatory)][string] $AppDirectory) {
    $files = @(Get-ChildItem -LiteralPath $AppDirectory -Recurse -File -Include '*.exe','*.dll' |
        Where-Object { $_.Name -like 'OpenSkiTime.*' -or $_.Name -eq 'TimyUsbHost.exe' } | Sort-Object FullName)
    foreach ($required in 'OpenSkiTime.Desktop.exe','OpenSkiTime.LiveTiming.ControlPanel.exe','OpenSkiTime.LiveTiming.Worker.exe',
        'OpenSkiTime.LiveTiming.Server.exe','TimyUsbHost.exe') {
        if (!($files | Where-Object Name -eq $required)) { throw "Missing release binary: $required" }
    }
    $files
}

# The expected certificate is pinned in the repository, so a signature by any other certificate fails the release.
function Get-SigningThumbprint {
    $file = Join-Path $PSScriptRoot '../deploy/windows/signing-certificate.txt'
    $thumbprint = [string](Get-Content -LiteralPath $file | Where-Object { $_ -and !$_.StartsWith('#') } | Select-Object -First 1)
    if ($thumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'deploy/windows/signing-certificate.txt has no SHA-1 certificate thumbprint.' }
    $thumbprint.ToUpperInvariant()
}

function Assert-OpenSkiTimeSignature([Parameter(Mandatory)][string[]] $Path) {
    $expected = Get-SigningThumbprint
    foreach ($file in $Path) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        $name = [IO.Path]::GetFileName($file)
        if ($signature.Status -ne 'Valid') { throw "$name signature is $($signature.Status): $($signature.StatusMessage)" }
        if ($signature.SignerCertificate.Thumbprint -ne $expected) { throw "$name is signed by an unexpected certificate $($signature.SignerCertificate.Thumbprint)." }
        if (!$signature.TimeStamperCertificate) { throw "$name signature has no timestamp." }
    }
    Write-Host "Verified OpenSkiTime signatures with timestamps on $($Path.Count) file(s)."
}

Export-ModuleMember -Function Get-OpenSkiTimeBinaries, Get-SigningThumbprint, Assert-OpenSkiTimeSignature
