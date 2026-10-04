# Website, live hosting and Windows releases

## Scope and deployment status

This repository contains the website, infrastructure, deployment workflows and unsigned Windows packaging. Keep them beside the application: source changes, release metadata and deployment reviews then refer to the same commit. No separate private deployment repository is required. Secrets are in Azure and GitHub environment secrets, never source.

The target domain is **openskiti.me**, not openskitime.me. The live service runs in **Sweden Central** (`swedencentral`). The website's template default control-plane region is West Europe, subject to subscription eligibility and capacity. On 2026-10-03 Azure rejected West Europe for this subscription; East US 2 passed live ARM validation and was selected explicitly for bootstrap. Use `-WebsiteLocation eastus2` when repeating this deployment. Static Web Apps distributes static content globally; this website has no Functions backend. It does not promise Sweden-only delivery of website assets. See [Static Web Apps FAQ](https://learn.microsoft.com/en-us/azure/static-web-apps/faq).

Infrastructure scripts are prepared for authenticated bootstrap. A successful local build does not establish an Azure deployment, DNS change or production acceptance. Record actual acceptance below after bootstrap.

| Address | Host | Configuration |
|---|---|---|
| `https://openskiti.me` | Azure Static Web Apps | Free plan, managed TLS |
| `https://www.openskiti.me` | Same Static Web App | Second custom domain, managed TLS; pages declare the apex canonical URL |
| `https://live.openskiti.me` | Azure Container Apps | Consumption, Sweden Central, 0.5 CPU / 1 GiB, zero to one replica, single active revision, managed TLS |

There is no ACR, cloud database, Redis, Azure SignalR Service, paid workload profile or Log Analytics workspace in these templates. Environment application-log destination is `none`. Log Analytics primarily incurs ingestion/retention charges, rather than adding application-process memory. Live state itself uses server RAM.

## Costs and scale to zero

The Azure consumption grants and Free website limits apply monthly, across the relevant billing scope. With `minReplicas=0`, no traffic between races and usage within the grants, live compute can cost effectively zero. This is not a guaranteed zero bill: ongoing publishers/viewers, WebSockets, bandwidth, other subscription usage and changed pricing can incur charges. Keep `maxReplicas=1`; this RAM-only service cannot safely run multiple replicas. Refer to [Container Apps billing](https://learn.microsoft.com/en-us/azure/container-apps/billing) and [scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app).

Scaling to zero, container replacement or restart loses published state. Stable signing keys allow an active desktop publisher to restore the same session by sending a full snapshot. A stopped publisher does not restore a view automatically. First requests can experience a cold start. An active publisher performs health checks, so it may keep the service running throughout a race even without viewers. Do not configure a frequent external uptime poll if the intent is to sleep between races. Cloud outages do not affect local timing capture.

Azure budgets are **monthly alerts in the subscription billing currency**, not spending caps. The default budget template amount is 20; change it deliberately. The domain registration/renewal is a separate registrar charge, normally annual. Free Cloudflare DNS does not make domain registration free. No numeric estimate here is a Sweden-specific price quotation.

Optional resource-group budget:

```powershell
# Dates and email are account configuration, not credentials. Use the first day of the current billing month.
az deployment sub create --location swedencentral --template-file deploy/azure/budget.bicep `
  --parameters resourceGroupName=openskitime-production amount=20 `
    'contactEmails=["YOUR-ALERT-EMAIL"]' startDate=YYYY-MM-01 endDate=YYYY-MM-01
```

Use an end date later than the start date. Azure may require time before cost data and budget alerts become available. Trial credit is temporary; inspect its expiry and billing/spending-limit settings separately.

## Website development

The dependency-free Node builder generates a responsive English website: landing page, download page, first-race guide, privacy page, code signing policy and 404 page, plus a Finnish overview at `/fi/`. The screenshots are synthetic desktop captures. There are no visitor scripts, forms or analytics cookies.

Search and answer engines read the same content visitors see. Page titles and descriptions use the terms people search for (alpine ski race timing software, ajanotto-ohjelma). The English and Finnish landing pages declare `hreflang` alternates and carry one JSON-LD `application/ld+json` data block (WebSite, SoftwareApplication, SoftwareSourceCode, FAQPage). The FAQ is defined once in `website/build.mjs` and renders both the visible questions and the structured data, so keep its answers factual and in step with the application. `website/llms.txt` gives AI tools a short project summary with links; `website/check.mjs` verifies that its site links resolve. JSON-LD blocks are not executed, so the CSP needs no `script-src`. The 404 page is `noindex`. Security headers are in `website/staticwebapp.config.json`. Download links are generated only from a published stable `vX.Y.Z` release with the expected upstream installer; before that, the site honestly offers source instructions.

```powershell
node website/build.mjs
node website/check.mjs
node --test scripts/tests/*.test.mjs
node website/preview.mjs
# Open http://127.0.0.1:4173
```

Before publication, review the website on a desktop and narrow/mobile viewport. Local HTML/link checks are not a substitute for browser review. Stable releases trigger a rebuild, so installer links cannot get ahead of release publication. Prereleases remain available on GitHub Releases but do not replace the stable website download.

## Security boundaries

Use a public source repository for the free OSS setup. Private repositories require a GitHub plan that supports the intended protection features and a separate public distribution plan. Bootstrap refuses a private repository rather than assuming its environments/rules are enforced. Changing visibility also publishes history; scan it for secrets and personal data first. The scripts never change visibility themselves.

`Configure-Repository.ps1` restricts production environments to approved branches/tags, requires master PRs with code-owner review and successful Website / Windows tests / Infrastructure / Windows security / Container security checks, blocks branch deletion/force pushes, and reserves `v*` / `live-v*` tag creation, replacement and deletion for repository administrators. The sole maintainer can bypass branch rules through a PR to avoid requiring self-approval. Administrators remain inherently trusted and can change repository settings. Keep administrator/collaborator membership small and use MFA. See [software supply-chain checks](software-supply-chain.md) for SBOM generation, CVE gates, weekly checks and coverage limits.

PR CI has read-only permissions and no deployment environments. There is no `pull_request_target` pipeline. Actions are pinned to commit SHAs, with Dependabot updates requiring review. Fork workflows cannot publish official artifacts because of the upstream repository guard. Production images and packages must originate from protected master history. Version tags must never be moved after publication. Do not give write access to untrusted contributors.

Live deployment uses GitHub OIDC, with the exact federation subject `repo:tnakeli/OpenSkiTime:environment:production-live` for this existing repository. There is no Azure client password. Its Entra service principal gets Contributor on the **one Container App**, and Reader on its environment, not Contributor on the resource group/subscription. It can still alter that app and access its secrets; app deployment authority necessarily trusts the reviewed code. Re-review the federation when renaming/transferring the repository or changing OIDC subject settings. New GitHub repositories may have different default subject claims.

Static Web Apps deployment uses its app-scoped deployment token in the **production-website** environment. The official upload action uses this token; this website pipeline does not pretend to be secret-free OIDC. Rotate the token if compromised. Its scope does not grant Azure subscription administration. Release publishing has `contents: write` only in a separate production-release job; package builds have read-only repository access. No manual SignPath or code-signing step is configured.

Cloudflare DNS credentials are needed only in the local bootstrap shell: Zone Read + DNS Edit limited to the `openskiti.me` zone. They are not stored in GitHub. Keep records DNS-only for Azure validation and managed-certificate renewal. Never enable shell tracing or Azure CLI `--debug` around credentials; never paste tokens into chat.

## One-time bootstrap

Prerequisites: PowerShell 7, .NET 10 SDK for local packaging, Node 22+, Azure CLI with the Container Apps extension, authenticated GitHub CLI, Azure subscription with resource/role-assignment permissions and Entra application creation permissions. GitHub authentication needs repository administration, environments/rules and secrets access. Routine workflows use their own short-lived tokens.

1. Review and merge the changes to master. Review history and explicitly choose public visibility. Authenticate locally:

   ```powershell
   gh auth login
   az login
   az account list --query '[].{name:name,id:id}' --output table
   az extension add --name containerapp --upgrade
   ./deploy/azure/Configure-Repository.ps1
   ```

   Setup creates no manual deployment approval gates. Review occurs at PR/tag authority instead. Deployment variables initially remain false.

2. Build the first image using the **Live deployment** workflow on master, with `deploy=false`:

   ```powershell
   gh workflow run live.yml --repo tnakeli/OpenSkiTime --ref master -f deploy=false
   ```

   Wait for its checks and image build. Read the immutable `ghcr.io/tnakeli/openskitime-live@sha256:...` reference from the run summary. Make that GHCR package publicly readable in its GitHub package settings; public source does not automatically mean public packages. Azure pulls anonymously, avoiding registry credentials and ACR charges. This package-visibility choice is a one-time registry setup, not a release signing approval. Verify anonymous pull access before provisioning. There is currently no local Docker-engine verification.

3. Provision in the selected subscription, using the exact image digest:

   ```powershell
   ./deploy/azure/Initialize-Azure.ps1 -SubscriptionId YOUR-SUBSCRIPTION-ID -WebsiteLocation eastus2 `
     -Image ghcr.io/tnakeli/openskitime-live@sha256:YOUR-64-HEX-DIGEST
   ./deploy/azure/Configure-GitHub.ps1 -SubscriptionId YOUR-SUBSCRIPTION-ID
   ```

   Resource group defaults to `openskitime-production`. The bootstrap preserves the existing HMAC signing key on repeated runs. A secured temporary parameter file avoids printing or passing the key in command-line arguments. Do not manually replace a signing key during an image update. Creation needs bootstrap administrator authority; normal image deployment does not have it.

4. Supply the Cloudflare token privately in the shell, then configure DNS/domain validation:

   ```powershell
   # Populate CLOUDFLARE_API_TOKEN with your local secret manager; never put its value in a script or Git.
   ./deploy/azure/Configure-Domains.ps1 -CloudflareZoneId YOUR-ZONE-ID
   Remove-Item Env:CLOUDFLARE_API_TOKEN
   ```

   If the website is ready while the live environment is still provisioning, run `Configure-Domains.ps1 -WebsiteOnly -CloudflareZoneId YOUR-ZONE-ID` first. This validates and configures only apex/www records and website certificates. Run the normal command once the live app exists. Region checks accept both Azure's display name (`Sweden Central`) and its location code (`swedencentral`).

   The script verifies the exact zone and validates all initial DNS changes before mutation. It refuses conflicting existing records or proxied records; it never deletes another destination or mail records. Cloudflare flattens the apex CNAME. It adds `asuid.live` and the generated apex TXT token at `@`, validates www through its CNAME on the Free plan, attaches both SWA names and binds live managed TLS. TXT ownership records can coexist with Cloudflare's flattened apex CNAME. DNS/certificate propagation can require a later rerun; this is not rolled back by deleting validation records. Keep them for renewal. See [Azure SWA apex validation](https://learn.microsoft.com/en-us/azure/static-web-apps/apex-domain-external), [www CNAME validation](https://learn.microsoft.com/en-us/azure/static-web-apps/custom-domain-external) and [Container Apps managed certificates](https://learn.microsoft.com/en-us/azure/container-apps/custom-domains-managed-certificates).

5. Wait for SWA custom domains to report Ready and the live certificate to bind. Enable and dispatch the first website deployment, then verify both website names and live behavior:

   ```powershell
   ./deploy/azure/Enable-Deployments.ps1 -WebsiteOnly
   # Wait for Website deployment to complete in GitHub Actions.
   ./deploy/azure/Enable-Deployments.ps1
   ```

   WebsiteOnly verifies HTTPS reachability and enables only website deployment, permitting the first content upload. The final check verifies actual OpenSkiTime content on both names and live REST/SignalR behavior before enabling live automation. The live smoke test creates and deletes only its synthetic session, checks invalid credentials, full state, WebSocket initial state, a result event and pause. It does not prove race-day load capacity or desktop/phone visual behavior.

6. Rehearse desktop Cloud publishing against `https://live.openskiti.me`, restart/resync, Internet loss, paused/deleted view and real-time browser updates. Confirm Local capture remains usable offline. Delete synthetic sessions after testing. Record date, deployed commit/digest, certificate status and any remaining acceptance gaps.

## Routine deployment and recovery

Website changes merged into master automatically deploy when enabled. Live release tags use `live-vX.Y.Z` (optional prerelease suffix), or dispatch live.yml on master with `deploy=true`. These are separate from Windows version tags. A tag requires its `CHANGELOG-LIVE.md` section and, after successful deployment, creates a GitHub release created with *Latest* disabled. GitHub still reports it as latest while no stable application release exists, so `scripts/read-release.mjs` selects the newest stable `vX.Y.Z` release itself instead of using `releases/latest`. Dispatched deployments create no release. The image reference is a digest. `Deploy-Live.ps1` verifies region and single-replica configuration, updates only the existing app, waits until the new image is the only active revision (checking earlier would test the previous revision still serving traffic), performs the synthetic acceptance check and verifies the custom HTTPS health endpoint. If verification fails, it restores the previous image and tests it again. A failed rollback requires operator action. Both update and rollback may reset RAM state; avoid race windows and expect the publisher to restore its snapshot.

Bootstrap Bicep is not the routine image-deployment mechanism. Reapplying full bootstrap to an existing service can affect domains/configuration; review `az deployment group what-if` before infrastructure updates. Do not rotate the signing key as an error-recovery shortcut. Live-session deletion revocation is RAM-only and is lost on restart; see [live timing credentials and deletion](live-timing.md#credentials-expiry-and-deletion).

To stop automatic deployments without deleting resources:

```powershell
gh variable set LIVE_DEPLOY_ENABLED --repo tnakeli/OpenSkiTime --body false
gh variable set WEBSITE_DEPLOY_ENABLED --repo tnakeli/OpenSkiTime --body false
```

These flags stop deployment jobs, not an already running service or all spending. Adjust/disable public ingress or remove resources deliberately if service operation must stop. Do not stop a race's local capture.

## Versioning

OpenSkiTime uses [Semantic Versioning](https://semver.org/) `MAJOR.MINOR.PATCH`, with optional `-preview.N` pre-release labels. `VersionPrefix` in `Directory.Build.props` is the single source (currently `0.1.0`); `ProductInfo` in `OpenSkiTime.Application` reads it from the informational version, dropping build metadata. While the version is `0.x`, the `.ost` format and APIs are not yet stable. Bump `VersionPrefix` in the release commit, then tag `vX.Y.Z`; `Assert-ReleaseSource.ps1` refuses a tag whose version differs from `VersionPrefix`. Version rules, changelogs and the release checklist are in [release process](release-process.md). The version appears in the window title, Settings → About, general PDF footers, the timing report form and the timing report XML `Software` element. The implemented FIS rule season (`ProductInfo.FisRulesSeason`, currently 2026-27) is shown beside it and changes only with a reviewed rules update. Live server tags (`live-vX.Y.Z`) version the hosted service independently; the live protocol version defines compatibility between the two.

## Unsigned Windows release

`vX.Y.Z` creates a stable release marked *Latest*; `vX.Y.Z-preview.N` creates a prerelease. Build/test occurs before publishing. Release notes come from the tagged `CHANGELOG.md` section; a tag without one fails before building. The Windows runner provides Inno Setup 6; compilation fails rather than silently falling back if it is missing. The package contains the desktop UI, separate control panel, worker, local server, OCR assets, MIT license and notices. Each .NET component is self-contained for Windows x64; the legacy USB host still requires Windows .NET Framework 4.8. Vendor Timy driver/SDK are separate.

```powershell
./scripts/Package-Windows.ps1 -Version 0.1.0-preview.1 -Iscc 'PATH-TO-INNO-SETUP-6/ISCC.exe'
./scripts/Test-WindowsPackage.ps1 -PackageDirectory artifacts/windows/app
```

Use a new output directory for each local build (`-OutputDirectory artifacts/windows-ANOTHER-VERSION`). The script refuses stale existing output, publishes each process independently, checks runtime/assets, builds an Inno per-user installer and portable ZIP, and produces `SHA256SUMS.txt`. The package test removes access to installed .NET from child processes, publishes a synthetic snapshot, kills only its own packaged server, verifies restart/resync, tests REST/SignalR and closes the worker. It does not exercise desktop GUI or physical hardware. CI also runs `Test-WindowsInstaller.ps1` on an ephemeral Windows runner: silent installation, packaged process checks, reinstall and uninstall preservation of a synthetic user file. This script deliberately refuses to install on a normal operator machine.

The installer needs no administrator privileges, has stable AppId/shortcuts, and contains no event-data deletion rules. CI requires installation, reinstall and uninstall-preservation acceptance before publishing. Review the desktop GUI and hardware on a clean Windows machine before the first release. Neither executable nor installer is signed. Checksums demonstrate integrity, not publisher identity; Windows can display an unknown-publisher warning. There is no SignPath integration.

No production release/tag is created automatically by merging this setup. Rehearse and back up before updating. Development database files with incompatible schemas are rejected unchanged; automatic upgrades/migrations are absent. Establish the release/schema compatibility policy before declaring a stable release. Do not overwrite an existing release's assets.

## Verification record

On 2026-10-03 local website build/link checks, Node failure-cleanup tests, Bicep compilation and self-contained ZIP/live process checks passed. GitHub Windows CI verified installer compilation, installation, packaged processes, reinstall and uninstall preservation. Azure provisioning completed with live Consumption in Sweden Central and Static Web Apps Free in East US 2. Cloudflare DNS and Azure managed TLS were configured for all three public names; live REST/SignalR acceptance passed on both the Azure hostname and `live.openskiti.me`. The website was published through its GitHub workflow. A monthly 20 EUR resource-group budget with email alerts was configured; this is not a spending cap. Scoped GitHub OIDC and the website environment secret were configured. Desktop and 390px mobile website previews were reviewed. Race-day load, real hardware and an operator's desktop Cloud publishing rehearsal remain unverified.

Core boundary test scenarios run sequentially to avoid unrelated OCR and durable SQLite workloads competing for CI resources. Each scenario retains its own capture/concurrency assertions and production drain deadlines. Live coalescing tests wait for the worker to acknowledge the complete snapshot batch: an event timestamp alone can also identify the first event of that batch.

Live ARM validation requires `appLogsConfiguration.destination: null` to disable persistent logs; the string `'none'` is rejected even though Bicep compilation accepts it. No Log Analytics workspace is provisioned.
