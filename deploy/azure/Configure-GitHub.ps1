#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SubscriptionId,
    [string] $ResourceGroup = 'openskitime-production',
    [string] $AppName = 'openskitime-live',
    [string] $EnvironmentName = 'openskitime-environment',
    [string] $WebsiteName = 'openskitime-website'
)
$ErrorActionPreference = 'Stop'
$repository = 'tnakeli/OpenSkiTime'
function GitHub([string] $Route, $Body = $null, [string] $Method = 'GET') {
    if ($null -eq $Body) { $result = & gh api $Route --method $Method }
    else { $result = ($Body | ConvertTo-Json -Depth 20 -Compress) | & gh api $Route --method $Method --input - }
    if ($LASTEXITCODE -ne 0) { throw "GitHub configuration failed: $Route" }
    if ($result) { return $result | ConvertFrom-Json }
}
function Azure([string[]] $Arguments) {
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Azure configuration failed.' }
    return $result
}
Azure @('account','set','--subscription',$SubscriptionId)
$account = Azure @('account','show','--output','json') | ConvertFrom-Json
$appId = Azure @('containerapp','show','--name',$AppName,'--resource-group',$ResourceGroup,'--query','id','--output','tsv')
$environmentId = Azure @('containerapp','env','show','--name',$EnvironmentName,'--resource-group',$ResourceGroup,'--query','id','--output','tsv')
if (!$appId -or !$environmentId) { throw 'Provision the live app and environment first.' }
& (Join-Path $PSScriptRoot 'Configure-Repository.ps1')
$identityName = 'OpenSkiTime GitHub live deployment'
$identities = @(Azure @('ad','app','list','--display-name',$identityName,'--output','json') | ConvertFrom-Json | Where-Object displayName -eq $identityName)
if ($identities.Count -gt 1) { throw 'Ambiguous Entra application name; resolve before granting permissions.' }
if (!$identities.Count) { $identity = Azure @('ad','app','create','--display-name',$identityName,'--output','json') | ConvertFrom-Json }
else { $identity = $identities[0] }
$principals = @(Azure @('ad','sp','list','--filter',"appId eq '$($identity.appId)'",'--output','json') | ConvertFrom-Json)
if (!$principals.Count) { $principal = Azure @('ad','sp','create','--id',$identity.appId,'--output','json') | ConvertFrom-Json }
else { $principal = $principals[0] }
$federation = @{name='github-production-live';issuer='https://token.actions.githubusercontent.com';subject="repo:${repository}:environment:production-live";audiences=@('api://AzureADTokenExchange')}
$existingFederation = @(Azure @('ad','app','federated-credential','list','--id',$identity.id,'--output','json') | ConvertFrom-Json | Where-Object name -eq $federation.name)
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('openskitime-federation-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    $federation | ConvertTo-Json | Set-Content -LiteralPath $temporary
    if ($existingFederation.Count) {
        Azure @('ad','app','federated-credential','update','--id',$identity.id,'--federated-credential-id',$existingFederation[0].id,'--parameters',"@$temporary",'--output','none')
    } else { Azure @('ad','app','federated-credential','create','--id',$identity.id,'--parameters',"@$temporary",'--output','none') }
} finally { Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue }
# No subscription-wide or resource-group-wide deployment role. No client password exists.
foreach ($assignment in @(@{role='Contributor';scope=$appId},@{role='Reader';scope=$environmentId})) {
    Azure @('role','assignment','create','--assignee-object-id',$principal.id,'--assignee-principal-type','ServicePrincipal','--role',$assignment.role,'--scope',$assignment.scope,'--output','none')
}
$variables = @{AZURE_LIVE_CLIENT_ID=$identity.appId;AZURE_TENANT_ID=$account.tenantId;AZURE_SUBSCRIPTION_ID=$account.id;AZURE_RESOURCE_GROUP=$ResourceGroup;AZURE_LIVE_APP=$AppName}
foreach ($entry in $variables.GetEnumerator()) {
    & gh variable set $entry.Key --repo $repository --env production-live --body $entry.Value
    if ($LASTEXITCODE -ne 0) { throw 'Could not store non-secret Azure environment variables.' }
}
$websiteToken = Azure @('staticwebapp','secrets','list','--name',$WebsiteName,'--resource-group',$ResourceGroup,'--query','properties.apiKey','--output','tsv')
if (!$websiteToken) { throw 'Website deployment token missing.' }
try {
    $websiteToken | & gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --repo $repository --env production-website
    if ($LASTEXITCODE -ne 0) { throw 'Could not store website token in its protected environment.' }
} finally { $websiteToken = $null }
Write-Host 'Production safeguards configured. Deployment remains disabled until DNS/TLS and acceptance checks pass.'
