#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SubscriptionId,
    [Parameter(Mandatory)][ValidatePattern('^ghcr\.io/tnakeli/openskitime-live@sha256:[a-f0-9]{64}$')][string] $Image,
    [string] $ResourceGroup = 'openskitime-production',
    [string] $Location = 'swedencentral',
    [string] $WebsiteLocation = 'westeurope',
    [string] $WebsiteName = 'openskitime-website',
    [string] $AppName = 'openskitime-live',
    [string] $EnvironmentName = 'openskitime-environment'
)
$ErrorActionPreference = 'Stop'
function Invoke-Azure([string[]] $Arguments) {
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Azure command failed. No subsequent deployment step was performed.' }
    return $result
}
Invoke-Azure @('account', 'set', '--subscription', $SubscriptionId)
try {
    $pullToken = (Invoke-RestMethod -Uri 'https://ghcr.io/token?service=ghcr.io&scope=repository%3Atnakeli%2Fopenskitime-live%3Apull' -TimeoutSec 30).token
    $digest = ($Image -split '@')[1]
    $null = Invoke-WebRequest -Method Head -Uri "https://ghcr.io/v2/tnakeli/openskitime-live/manifests/$digest" -TimeoutSec 30 -Headers @{
        Authorization="Bearer $pullToken";Accept='application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.manifest.v1+json'
    }
} catch { throw 'The immutable image is not anonymously readable. Build it and make the GHCR package public before provisioning.' }
finally { $pullToken = $null }
foreach ($provider in @('Microsoft.App','Microsoft.Web','Microsoft.OperationalInsights')) {
    Invoke-Azure @('provider','register','--namespace',$provider,'--wait','--output','none')
}
# Check supported locations before creating resources; do not silently choose another region.
$available = Invoke-Azure @('provider','show','--namespace','Microsoft.Web','--query',"resourceTypes[?resourceType=='staticSites'].locations | [0]",'--output','json') | ConvertFrom-Json
if (($available | ForEach-Object { $_.Replace(' ','').ToLowerInvariant() }) -notcontains $WebsiteLocation) {
    throw "Static Web Apps does not support $WebsiteLocation in this subscription. Choose a supported website control-plane region."
}
Invoke-Azure @('group','create','--name',$ResourceGroup,'--location',$Location,'--tags','project=OpenSkiTime','--output','none')
$output = Invoke-Azure @('deployment','group','create','--name','openskitime-platform','--resource-group',$ResourceGroup,
    '--template-file',(Join-Path $PSScriptRoot 'main.bicep'),'--parameters',"location=$Location", "websiteLocation=$WebsiteLocation",
    "websiteName=$WebsiteName", "environmentName=$EnvironmentName", '--query','properties.outputs','--output','json') | ConvertFrom-Json

# Preserve the key on repeated bootstrap runs. Never rotate it during an ordinary deployment.
$apps = Invoke-Azure @('containerapp','list','--resource-group',$ResourceGroup,'--query','[].name','--output','json') | ConvertFrom-Json
if ($apps -contains $AppName) {
    $key = Invoke-Azure @('containerapp','secret','list','--name',$AppName,'--resource-group',$ResourceGroup,
        '--show-values','--query',"[?name=='live-signing-key'].value | [0]",'--output','tsv')
    if ([string]::IsNullOrWhiteSpace($key)) { throw 'Existing live signing key could not be recovered. Refusing to replace it.' }
    $publisherKeys = Invoke-Azure @('containerapp','secret','list','--name',$AppName,'--resource-group',$ResourceGroup,
        '--show-values','--query',"[?name=='live-publisher-keys'].value | [0]",'--output','tsv')
} else {
    $key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
}
if ([string]::IsNullOrWhiteSpace($publisherKeys)) {
    # Fail closed: an entry whose key nobody holds lets the server start without accepting any publisher.
    # Add real keys afterwards with Set-LivePublisherKey.ps1 (including -Name deployment-check -GitHubSecret).
    $unheld = [Security.Cryptography.SHA256]::HashData([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $publisherKeys = 'bootstrap-closed:' + [Convert]::ToHexString($unheld).ToLowerInvariant()
    Write-Warning 'No publisher keys exist yet. Run Set-LivePublisherKey.ps1 to issue keys before publishing.'
}
$parameters = @{ '$schema'='https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'; contentVersion='1.0.0.0'; parameters=@{
    environmentId=@{value=$output.environmentId.value}; location=@{value=$Location}; appName=@{value=$AppName}; image=@{value=$Image};
    publicBaseUrl=@{value='https://live.openskiti.me'}; signingKey=@{value=$key}; publisherKeys=@{value=$publisherKeys}; minReplicas=@{value=0}
} }
# A secured temporary file keeps the secret out of command-line arguments, Git and console output.
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('openskitime-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
if ($IsWindows) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $temporary -AclObject $acl
} else { & chmod 700 $temporary; if ($LASTEXITCODE -ne 0) { throw 'Could not protect temporary parameter directory.' } }
$parameterFile = Join-Path $temporary 'live.parameters.json'
try {
    $parameters | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $parameterFile
    Invoke-Azure @('deployment','group','create','--name','openskitime-live','--resource-group',$ResourceGroup,
        '--template-file',(Join-Path $PSScriptRoot '../live-timing/containerapp.bicep'),'--parameters',"@$parameterFile",'--output','none')
} finally {
    $key = $null; $publisherKeys = $null; $parameters = $null
    if (Test-Path -LiteralPath $parameterFile) { Remove-Item -LiteralPath $parameterFile }
    Remove-Item -LiteralPath $temporary
}
$liveHost = Invoke-Azure @('containerapp','show','--name',$AppName,'--resource-group',$ResourceGroup,'--query','properties.configuration.ingress.fqdn','--output','tsv')
[pscustomobject]@{ ResourceGroup=$ResourceGroup; LiveRegion=$Location; WebsiteRegion=$WebsiteLocation; WebsiteHostname=$output.websiteHostname.value; LiveHostname=$liveHost }
