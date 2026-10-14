#Requires -Version 7.0
# Signs OpenSkiTime's own binaries in a published app directory with ssign (Certum SimplySign cloud certificate).
# CERTUM_EMAIL and CERTUM_TOKEN must be set. The first ssign call logs in with the one-time code; later calls,
# including Inno Setup's installer signing, reuse ssign's cached session.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $AppDirectory,
    [Parameter(Mandatory)][string] $SignTool
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsSigning.psm1') -Force
if (!$env:CERTUM_EMAIL -or !$env:CERTUM_TOKEN) { throw 'CERTUM_EMAIL and CERTUM_TOKEN must be set.' }
$files = @(Get-OpenSkiTimeBinaries ([IO.Path]::GetFullPath($AppDirectory)) | ForEach-Object FullName)
foreach ($file in $files) {
    if ((Get-AuthenticodeSignature -LiteralPath $file).Status -ne 'NotSigned') { throw "$file is already signed; sign only freshly built files." }
}
# Batches keep the command line short; the session is shared across them.
for ($i = 0; $i -lt $files.Count; $i += 40) {
    & $SignTool -n OpenSkiTime -u https://openskiti.me @($files[$i..([Math]::Min($i + 39, $files.Count - 1))])
    if ($LASTEXITCODE -ne 0) { throw 'ssign failed.' }
}
Assert-OpenSkiTimeSignature $files
