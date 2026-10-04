#Requires -Version 7.0
<#
.SYNOPSIS
Adds, rotates, removes or lists live timing publisher keys on the Azure Container App.

.DESCRIPTION
The Container App secret 'live-publisher-keys' holds only "name:sha256" entries. A new or rotated key is
printed once (or stored as a GitHub environment secret with -GitHubSecret) and is never recoverable from Azure.
Changes take effect in a new revision; running sessions keep their own tokens. Run away from a race window,
because a new revision resets RAM state until publishers resynchronize.
#>
[CmdletBinding(DefaultParameterSetName = 'List')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Add')][ValidatePattern('^[A-Za-z0-9 ._-]{1,64}$')][string] $Name,
    [Parameter(ParameterSetName = 'Add')][switch] $GitHubSecret,
    [Parameter(Mandatory, ParameterSetName = 'Remove')][ValidatePattern('^[A-Za-z0-9 ._-]{1,64}$')][string] $Remove,
    [Parameter(ParameterSetName = 'List')][switch] $List,
    [string] $ResourceGroup = 'openskitime-production',
    [string] $AppName = 'openskitime-live'
)
$ErrorActionPreference = 'Stop'
function Invoke-Azure([string[]] $Arguments) {
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Azure command failed. Publisher keys were not changed.' }
    return $result
}
$secretName = 'live-publisher-keys'
$names = @(Invoke-Azure @('containerapp','secret','list','--name',$AppName,'--resource-group',$ResourceGroup,'--query','[].name','--output','json') | ConvertFrom-Json)
$current = if ($names -contains $secretName) {
    Invoke-Azure @('containerapp','secret','show','--name',$AppName,'--resource-group',$ResourceGroup,'--secret-name',$secretName,'--query','value','--output','tsv')
} else { '' }
$entries = [ordered]@{}
foreach ($entry in ($current -split ';') | Where-Object { $_.Trim() }) {
    $separator = $entry.LastIndexOf(':')
    $entries[$entry.Substring(0, $separator).Trim()] = $entry.Substring($separator + 1).Trim()
}
if ($PSCmdlet.ParameterSetName -eq 'List') {
    if (!$entries.Count) { Write-Host 'No publisher keys are configured; the live server refuses to start without one.' }
    $entries.Keys | ForEach-Object { [pscustomobject]@{ Publisher = $_ } }
    return
}
$key = $null
if ($PSCmdlet.ParameterSetName -eq 'Add') {
    $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $key = 'ost_pk_' + [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key))).ToLowerInvariant()
    $existing = @($entries.Keys | Where-Object { $_ -ieq $Name })
    foreach ($old in $existing) { $entries.Remove($old) } # Same name rotates the key; the old key stops working.
    $entries[$Name] = $hash
} else {
    $existing = @($entries.Keys | Where-Object { $_ -ieq $Remove })
    if (!$existing.Count) { throw "Publisher '$Remove' is not configured." }
    if ($entries.Count -eq 1) { throw 'Refusing to remove the last publisher key; the server cannot start without one. Add a replacement first.' }
    foreach ($old in $existing) { $entries.Remove($old) }
}
$value = ($entries.GetEnumerator() | ForEach-Object { "$($_.Key):$($_.Value)" }) -join ';'
try {
    # Only names and hashes reach Azure. A new revision reloads the secret-backed environment.
    Invoke-Azure @('containerapp','secret','set','--name',$AppName,'--resource-group',$ResourceGroup,'--secrets',"$secretName=$value",'--output','none')
    Invoke-Azure @('containerapp','update','--name',$AppName,'--resource-group',$ResourceGroup,'--revision-suffix',('keys' + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()),
        '--set-env-vars',"LiveTiming__PublisherKeys=secretref:$secretName",'LiveTiming__TrustForwardedFor=true','--output','none')
    if ($key -and $GitHubSecret) {
        $key | & gh secret set LIVE_PUBLISHER_KEY --repo tnakeli/OpenSkiTime --env production-live
        if ($LASTEXITCODE -ne 0) { throw 'Azure was updated, but the key could not be stored in GitHub. Rotate this publisher again.' }
        Write-Host "Publisher '$Name' key stored as the production-live LIVE_PUBLISHER_KEY secret for deployment checks."
    } elseif ($key) {
        Write-Host "Publisher '$Name' key (shown once; send it to the organizer over a private channel and do not store it in Git or e-mail threads):"
        Write-Output $key
    } else { Write-Host "Publisher '$Remove' removed. New sessions with that key are refused; existing session tokens expire on their own." }
} finally { $key = $null; $value = $null }
