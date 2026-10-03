#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = 'tnakeli/OpenSkiTime'
function GitHub([string] $Route, $Body = $null, [string] $Method = 'GET') {
    if ($null -eq $Body) { $result = & gh api $Route --method $Method }
    else { $result = ($Body | ConvertTo-Json -Depth 20 -Compress) | & gh api $Route --method $Method --input - }
    if ($LASTEXITCODE -ne 0) { throw "GitHub configuration failed: $Route" }
    if ($result) { return $result | ConvertFrom-Json }
}
$repo = GitHub "repos/$repository"
if ($repo.private) { throw 'Public-repository safeguards are required for this free OSS setup. Repository visibility is never changed by this script.' }
if ($repo.default_branch -ne 'master' -or !$repo.permissions.admin) { throw 'Expected master and repository administrator access.' }
$oidc = GitHub "repos/$repository/actions/oidc/customization/sub"
if (!$oidc.use_default -or [DateTimeOffset]$repo.created_at -ge [DateTimeOffset]'2026-07-15T00:00:00Z') {
    throw 'This bootstrap expects the existing repository default OIDC subject. Review its actual claims before creating a federation.'
}
# A bootstrap is deliberately disabled until DNS/TLS and functional acceptance pass.
foreach ($name in @('LIVE_DEPLOY_ENABLED','WEBSITE_DEPLOY_ENABLED')) {
    & gh variable set $name --repo $repository --body false
    if ($LASTEXITCODE -ne 0) { throw 'Could not disable deployment while configuring safeguards.' }
}
foreach ($name in @('production-live','production-website','production-release')) {
    $null = GitHub "repos/$repository/environments/$name" @{
        deployment_branch_policy=@{protected_branches=$false;custom_branch_policies=$true}
    } 'PUT'
    $desired = switch ($name) {
        'production-live' { @(@{name='master';type='branch'},@{name='live-v*';type='tag'}) }
        'production-website' { @(@{name='master';type='branch'},@{name='v*';type='tag'}) }
        'production-release' { @(@{name='v*';type='tag'}) }
    }
    $current = (GitHub "repos/$repository/environments/$name/deployment-branch-policies").branch_policies
    foreach ($policy in $current) {
        if (!($desired | Where-Object { $_.name -eq $policy.name -and $_.type -eq $policy.type })) {
            $null = GitHub "repos/$repository/environments/$name/deployment-branch-policies/$($policy.id)" $null 'DELETE'
        }
    }
    foreach ($policy in $desired) {
        if (!($current | Where-Object { $_.name -eq $policy.name -and $_.type -eq $policy.type })) {
            $null = GitHub "repos/$repository/environments/$name/deployment-branch-policies" $policy 'POST'
        }
    }
}
# Keep unrelated rulesets. Repository admins retain the platform's inherent emergency authority.
$rulesets = GitHub "repos/$repository/rulesets"
$branch = @{
    name='OpenSkiTime production branch';target='branch';enforcement='active';
    bypass_actors=@(@{actor_id=5;actor_type='RepositoryRole';bypass_mode='pull_request'});
    conditions=@{ref_name=@{include=@('refs/heads/master');exclude=@()}};
    rules=@(
        @{type='deletion'},@{type='non_fast_forward'},
        @{type='pull_request';parameters=@{required_approving_review_count=1;dismiss_stale_reviews_on_push=$true;require_code_owner_review=$true;require_last_push_approval=$false;required_review_thread_resolution=$true}},
        @{type='required_status_checks';parameters=@{strict_required_status_checks_policy=$true;required_status_checks=@(@{context='Website'},@{context='Windows tests'},@{context='Infrastructure'},@{context='Windows security'},@{context='Container security'})}}
    )
}
$tags = @{
    name='OpenSkiTime production tags';target='tag';enforcement='active';
    bypass_actors=@(@{actor_id=5;actor_type='RepositoryRole';bypass_mode='always'});
    conditions=@{ref_name=@{include=@('refs/tags/v*','refs/tags/live-v*');exclude=@()}};
    rules=@(@{type='creation'},@{type='update'},@{type='deletion'})
}
foreach ($ruleset in @($branch,$tags)) {
    $existing = @($rulesets | Where-Object name -eq $ruleset.name)
    if ($existing.Count -gt 1) { throw 'Ambiguous production ruleset; review GitHub settings.' }
    if ($existing.Count) { $null = GitHub "repos/$repository/rulesets/$($existing[0].id)" $ruleset 'PUT' }
    else { $null = GitHub "repos/$repository/rulesets" $ruleset 'POST' }
}
Write-Host 'Repository environments and rules configured; deployments remain disabled.'
