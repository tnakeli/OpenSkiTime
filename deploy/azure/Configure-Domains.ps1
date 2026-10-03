#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string] $CloudflareZoneId,
    [string] $ResourceGroup = 'openskitime-production',
    [string] $WebsiteName = 'openskitime-website',
    [string] $AppName = 'openskitime-live',
    [switch] $WebsiteOnly
)
$ErrorActionPreference = 'Stop'
if (!$env:CLOUDFLARE_API_TOKEN) { throw 'Supply CLOUDFLARE_API_TOKEN privately in the local shell: Zone Read and DNS Edit for openskiti.me only.' }
function Azure([string[]] $Arguments) {
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Azure domain configuration failed. DNS and certificate propagation may require a later retry.' }
    return $result
}
function Cloudflare([string] $Route, [string] $Method = 'GET', $Body = $null) {
    $arguments = @{Uri="https://api.cloudflare.com/client/v4/$Route";Method=$Method;Headers=@{Authorization="Bearer $env:CLOUDFLARE_API_TOKEN"};TimeoutSec=60}
    if ($null -ne $Body) { $arguments.ContentType='application/json'; $arguments.Body=$Body | ConvertTo-Json -Depth 8 -Compress }
    try { $response = Invoke-RestMethod @arguments }
    catch { throw 'Cloudflare request failed. Check the scoped token locally; it is intentionally not printed.' }
    if (!$response.success) { throw 'Cloudflare did not accept the DNS change.' }
    return $response.result
}
$zone = Cloudflare "zones/$CloudflareZoneId"
if ($zone.name -ne 'openskiti.me') { throw 'Refusing to modify a zone other than openskiti.me.' }
$website = Azure @('staticwebapp','show','--name',$WebsiteName,'--resource-group',$ResourceGroup,'--output','json') | ConvertFrom-Json
$websiteHost = $website.defaultHostname
if (!$websiteHost) { throw 'Azure website hostname is missing.' }
$records = @(
    @{type='CNAME';name='openskiti.me';content=$websiteHost;ttl=1;proxied=$false},
    @{type='CNAME';name='www.openskiti.me';content=$websiteHost;ttl=1;proxied=$false}
)
if (!$WebsiteOnly) {
    $app = Azure @('containerapp','show','--name',$AppName,'--resource-group',$ResourceGroup,'--output','json') | ConvertFrom-Json
    if ($app.location.Replace(' ','').ToLowerInvariant() -ne 'swedencentral') { throw 'The live app must be in Sweden Central.' }
    $liveHost = $app.properties.configuration.ingress.fqdn
    $verification = $app.properties.customDomainVerificationId
    if (!$liveHost -or !$verification) { throw 'Azure live hostname or verification ID is missing.' }
    $records += @{type='CNAME';name='live.openskiti.me';content=$liveHost;ttl=1;proxied=$false}
    $records += @{type='TXT';name='asuid.live.openskiti.me';content=$verification;ttl=1}
}
function Inspect-Record($Record) {
    $name = [Uri]::EscapeDataString($Record.name)
    $current = @(Cloudflare "zones/$CloudflareZoneId/dns_records?name=$name&type=$($Record.type)&per_page=100")
    if ($Record.type -eq 'CNAME') {
        $addresses = @(Cloudflare "zones/$CloudflareZoneId/dns_records?name=$name&type=A&per_page=100")
        $addresses += @(Cloudflare "zones/$CloudflareZoneId/dns_records?name=$name&type=AAAA&per_page=100")
        if ($addresses.Count) { throw "Existing address records at $($Record.name) conflict with the proposed CNAME. Review them explicitly." }
    }
    if ($current.Count -gt 1 -or ($current.Count -and ($current[0].type -ne $Record.type -or $current[0].content.Trim('"').TrimEnd('.') -ne $Record.content.TrimEnd('.')))) {
        throw "Existing DNS at $($Record.name) conflicts with the proposed record. Review it explicitly; this script never replaces a different target."
    }
    if ($current.Count -and $current[0].proxied) { throw "$($Record.name) is proxied. Review and change it to DNS-only before certificate provisioning." }
    return $current
}
function Ensure-Record($Record) {
    $current = @(Inspect-Record $Record)
    if (!$current.Count) { $null = Cloudflare "zones/$CloudflareZoneId/dns_records" 'POST' $Record }
}
# Validate every initial record before the first DNS mutation. Other records, including mail, stay untouched.
foreach ($record in $records) { $null = Inspect-Record $record }
foreach ($record in $records) { Ensure-Record $record }
foreach ($domain in @('openskiti.me','www.openskiti.me')) {
    $existing = @(Azure @('staticwebapp','hostname','list','--name',$WebsiteName,'--resource-group',$ResourceGroup,'--output','json') | ConvertFrom-Json | Where-Object domainName -eq $domain)
    if ($existing.Count -and $existing[0].status -eq 'Ready') { continue }
    if (!$existing.Count) {
        $method = if ($domain -eq 'openskiti.me') { 'dns-txt-token' } else { 'cname-delegation' }
        Azure @('staticwebapp','hostname','set','--name',$WebsiteName,'--resource-group',$ResourceGroup,'--hostname',$domain,'--validation-method',$method,'--no-wait','--output','none')
    }
    # The Free plan validates www directly through its publicly resolvable CNAME.
    if ($domain -eq 'www.openskiti.me') { continue }
    $token = $null
    for ($attempt=0; $attempt -lt 12 -and !$token; $attempt++) {
        $details = Azure @('staticwebapp','hostname','show','--name',$WebsiteName,'--resource-group',$ResourceGroup,'--hostname',$domain,'--output','json') | ConvertFrom-Json
        if ($details.status -eq 'Ready') { break }
        $token = $details.validationToken
        if (!$token) { Start-Sleep -Seconds 5 }
    }
    if ($details.status -eq 'Ready') { continue }
    if (!$token) { throw 'Azure TXT validation token is not ready. Retry this script later.' }
    Ensure-Record @{type='TXT';name=$domain;content=$token;ttl=1}
}
if ($WebsiteOnly) {
    Write-Host 'Website DNS records and domain validation submitted. Retry after propagation if certificates are pending.'
    return
}
$domains = @($app.properties.configuration.ingress.customDomains | Where-Object name -eq 'live.openskiti.me')
if (!$domains.Count) { Azure @('containerapp','hostname','add','--name',$AppName,'--resource-group',$ResourceGroup,'--hostname','live.openskiti.me','--output','none') }
if (!$domains.Count -or $domains[0].bindingType -ne 'SniEnabled') {
    $environment = $app.properties.managedEnvironmentId ?? $app.properties.environmentId
    if (!$environment) { throw 'Container Apps environment ID is missing.' }
    Azure @('containerapp','hostname','bind','--name',$AppName,'--resource-group',$ResourceGroup,'--hostname','live.openskiti.me','--environment',$environment,'--validation-method','CNAME','--output','none')
}
Write-Host 'DNS records submitted and live certificate binding requested. Retry after propagation if Azure validation is pending.'
Write-Host 'Do not enable deployment until both website names have Ready certificates and all three HTTPS endpoints pass acceptance.'
