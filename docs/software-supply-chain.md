# SBOM, vulnerability and secret checks

Every pull request and master build scans the actual self-contained Windows portable package and a freshly built live server container. Checks also run weekly on Mondays and can be started manually. Jobs have read-only repository permissions and do not use Azure credentials or deployment environments.

The production branch rules require `Secrets`, `Windows security` and `Container security` in addition to the existing website, application and infrastructure checks.

## Secret scanning

The `Secrets` check runs Gitleaks over every reachable commit on every pull request, master build, weekly and manual run, including documentation-only changes. A secret removed in a later commit still fails the check, because it remains in history: rotate the credential, do not just delete it. Findings are redacted in the job log. Reviewed false positives (synthetic test values only) are listed by exact fingerprint in `.gitleaksignore`; never add a real credential there. GitHub secret scanning with push protection and Dependabot alerts/security updates are enabled on the repository as an additional layer.

Gitleaks 8.30.1 is installed like Trivy: a fixed upstream release verified against a checksum pinned in `.github/actions/setup-gitleaks`.

## Vulnerability scanning

Trivy 0.75.0 is downloaded from its upstream release and verified against a checksum pinned in the repository. Changing the scanner requires reviewing both the version and checksum. GitHub Actions remain pinned to commit SHAs and Dependabot proposes updates.

Each scan produces `sbom.cdx.json` (CycloneDX), `cve.json` (all reported severities), `cve-gate.txt` and `provenance.txt` (source commit, scan target, scanner and database details). CI retains reports for 14 days; live deployment retains reports for 30 days. Reports remain available when the vulnerability gate fails. Empty inventories, database download failures and scanner errors fail the job. There is no vulnerability ignore list. High and critical findings, including those without fixes, block successful checks and publication.

Windows scans extract the portable package made by the packaging job (in releases, the signed package). This inventories detected runtime dependencies from `.deps.json`; NuGet restore additionally audits direct and transitive dependencies, including test tooling. The container scan also covers supported Linux OS packages. These inventories are not proof that every native DLL, OCR model, vendor driver or manually supplied SDK has advisory coverage. Review vendor advisories separately; ALGE drivers and SDK are not bundled. The static marketing website has no npm dependencies or visitor JavaScript.

Windows release publication waits for a successful package scan and attaches its SBOM, reports and provenance alongside the installer and ZIP. SHA256SUMS covers these additional files. Live deployment waits for a scan of the exact immutable GHCR image digest it will deploy. Image upload alone does not deploy an image. Windows releases are signed before the scan, so the scanned ZIP and the published files are the signed ones; signing needs the maintainer's one-time SimplySign code (see [release process](release-process.md#code-signing-certum-simplysign)). Live deployment has no manual approval or code signing.

Weekly checks rebuild the current master package and container with current dependency advisories. They do not modify production or guarantee that an older deployed image is free of newly discovered vulnerabilities. Investigate failed checks, update affected dependencies/base images, rerun the checks and publish the fixed version.
