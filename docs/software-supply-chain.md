# SBOM and vulnerability checks

Every pull request and master build scans the actual self-contained Windows portable package and a freshly built live server container. Checks also run weekly on Mondays and can be started manually. Jobs have read-only repository permissions and do not use Azure credentials or deployment environments.

The production branch rules require both `Windows security` and `Container security` in addition to the existing website, application and infrastructure checks.

Trivy 0.75.0 is downloaded from its upstream release and verified against a checksum pinned in the repository. Changing the scanner requires reviewing both the version and checksum. GitHub Actions remain pinned to commit SHAs and Dependabot proposes updates.

Each scan produces `sbom.cdx.json` (CycloneDX), `cve.json` (all reported severities), `cve-gate.txt` and `provenance.txt` (source commit, scan target, scanner and database details). CI retains reports for 14 days; live deployment retains reports for 30 days. Reports remain available when the vulnerability gate fails. Empty inventories, database download failures and scanner errors fail the job. There is no vulnerability ignore list. High and critical findings, including those without fixes, block successful checks and publication.

Windows scans extract the portable package made by the installer job. This inventories detected runtime dependencies from `.deps.json`; NuGet restore additionally audits direct and transitive dependencies, including test tooling. The container scan also covers supported Linux OS packages. These inventories are not proof that every native DLL, OCR model, vendor driver or manually supplied SDK has advisory coverage. Review vendor advisories separately; ALGE drivers and SDK are not bundled. The static marketing website has no npm dependencies or visitor JavaScript.

Windows release publication waits for a successful package scan and attaches its SBOM, reports and provenance alongside the installer and ZIP. SHA256SUMS covers these additional files. Live deployment waits for a scan of the exact immutable GHCR image digest it will deploy. Image upload alone does not deploy an image. Neither process introduces manual approval or code signing.

Weekly checks rebuild the current master package and container with current dependency advisories. They do not modify production or guarantee that an older deployed image is free of newly discovered vulnerabilities. Investigate failed checks, update affected dependencies/base images, rerun the checks and publish the fixed version.
