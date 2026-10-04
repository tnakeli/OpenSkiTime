#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^ghcr\.io/tnakeli/openskitime-live@sha256:[a-f0-9]{64}$')][string] $Image,
    [Parameter(Mandatory)][string] $ResourceGroup,
    [string] $AppName = 'openskitime-live'
)
$ErrorActionPreference = 'Stop'
function Invoke-Azure([string[]] $Arguments) {
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Azure deployment command failed.' }
    return $result
}
$app = Invoke-Azure @('containerapp','show','--name',$AppName,'--resource-group',$ResourceGroup,'--output','json') | ConvertFrom-Json
if ($app.location.Replace(' ','').ToLowerInvariant() -ne 'swedencentral') { throw 'Live deployment must target Sweden Central.' }
if ($app.properties.template.scale.maxReplicas -ne 1 -or $app.properties.configuration.activeRevisionsMode -ne 'Single') {
    throw 'Live service requires a single replica and single active revision.'
}
$previous = $app.properties.template.containers[0].image
$origin = 'https://' + $app.properties.configuration.ingress.fqdn
try {
    Invoke-Azure @('containerapp','update','--name',$AppName,'--resource-group',$ResourceGroup,'--image',$Image,'--output','none')
    # LIVE_PUBLISHER_KEY is the dedicated deployment-check key; anonymous creation must be refused.
    & node (Join-Path $PSScriptRoot '../../scripts/live-smoke.mjs') $origin '--require-publisher-key'
    if ($LASTEXITCODE -ne 0) { throw 'New live revision failed the functional check.' }
    # Also verifies DNS and TLS on the public domain, without creating another race session.
    $health = Invoke-WebRequest -Uri 'https://live.openskiti.me/health' -TimeoutSec 60 -MaximumRedirection 0
    if ($health.StatusCode -ne 200) { throw 'Custom-domain health check failed.' }
} catch {
    Write-Warning 'Deployment failed. Restoring the previous image; RAM race state must be republished.'
    Invoke-Azure @('containerapp','update','--name',$AppName,'--resource-group',$ResourceGroup,'--image',$previous,'--output','none')
    & node (Join-Path $PSScriptRoot '../../scripts/live-smoke.mjs') $origin
    if ($LASTEXITCODE -ne 0) { throw 'Rollback also failed verification. Operator action is required.' }
    throw 'Deployment verification failed; the previous image has been restored and verified.'
}
Write-Host "Live deployment verified: $Image"
