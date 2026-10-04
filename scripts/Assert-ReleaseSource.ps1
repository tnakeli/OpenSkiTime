#Requires -Version 7.0
[CmdletBinding()]
param([switch] $Live)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_REPOSITORY -ne 'tnakeli/OpenSkiTime') { throw 'Only the upstream repository may publish official artifacts.' }
if ($env:GITHUB_EVENT_NAME -eq 'workflow_dispatch') {
    if ($env:GITHUB_REF -ne 'refs/heads/master') { throw 'Manual publication must use master.' }
} elseif ($env:GITHUB_EVENT_NAME -eq 'push') {
    $pattern = if ($Live) { '^refs/tags/live-v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$' } else { '^refs/tags/v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$' }
    if ($env:GITHUB_REF -notmatch $pattern) { throw 'Invalid release tag.' }
    if (!$Live) {
        # The tag must release the version declared in source, so About, reports and XML agree with the package.
        [xml] $props = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw
        $declared = ($props.Project.PropertyGroup | Where-Object { $_.VersionPrefix } | Select-Object -First 1).VersionPrefix
        $tagged = ($env:GITHUB_REF -replace '^refs/tags/v', '') -split '-' | Select-Object -First 1
        if ($tagged -ne $declared) { throw "Release tag version $tagged does not match VersionPrefix $declared in Directory.Build.props." }
    }
} else { throw 'This event may not publish artifacts.' }
& git merge-base --is-ancestor HEAD origin/master
if ($LASTEXITCODE -ne 0) { throw 'Publication commit must be on the protected master history.' }
