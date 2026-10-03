#Requires -Version 7.0
[CmdletBinding()]
param([switch] $WebsiteOnly)
$ErrorActionPreference = 'Stop'
$repo = & gh api repos/tnakeli/OpenSkiTime --jq '{private: .private, default_branch: .default_branch}' | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $repo.private -or $repo.default_branch -ne 'master') { throw 'Expected the public upstream repository on master.' }
# Functional acceptance must precede enabling automated live deployment.
if (!$WebsiteOnly) {
    & node (Join-Path $PSScriptRoot '../../scripts/live-smoke.mjs') 'https://live.openskiti.me'
    if ($LASTEXITCODE -ne 0) { throw 'Live acceptance failed; automation remains disabled.' }
}
foreach ($origin in @('https://openskiti.me','https://www.openskiti.me')) {
    $response = Invoke-WebRequest -Uri $origin -TimeoutSec 60
    if ($response.StatusCode -ne 200 -or (!$WebsiteOnly -and $response.Content -notmatch 'OpenSkiTime')) { throw "Website acceptance failed: $origin" }
}
$names = if ($WebsiteOnly) { @('WEBSITE_DEPLOY_ENABLED') } else { @('WEBSITE_DEPLOY_ENABLED','LIVE_DEPLOY_ENABLED') }
foreach ($name in $names) {
    & gh variable set $name --repo tnakeli/OpenSkiTime --body true
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable deployment variable.' }
}
if ($WebsiteOnly) {
    & gh workflow run website.yml --repo tnakeli/OpenSkiTime --ref master
    if ($LASTEXITCODE -ne 0) { throw 'Website enabled, but initial deployment dispatch failed. Retry the workflow on master.' }
    Write-Host 'Initial website deployment dispatched. Wait for it, then rerun without -WebsiteOnly for full acceptance.'
} else { Write-Host 'Website and live deployments enabled after HTTPS/functional acceptance.' }
