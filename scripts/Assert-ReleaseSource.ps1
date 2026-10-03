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
} else { throw 'This event may not publish artifacts.' }
& git merge-base --is-ancestor HEAD origin/master
if ($LASTEXITCODE -ne 0) { throw 'Publication commit must be on the protected master history.' }
