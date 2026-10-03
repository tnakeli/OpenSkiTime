#!/usr/bin/env bash
# Scan the exact published files or immutable image. Never suppress scanner failures.
set -euo pipefail
mode="${1:?Expected fs or image}"
target="${2:?Expected target}"
output="${3:?Expected output directory}"
case "$mode" in fs|image) ;; *) echo 'Unsupported scan mode' >&2; exit 2 ;; esac
mkdir -p "$output"
trivy "$mode" --scanners vuln --format cyclonedx --output "$output/sbom.cdx.json" "$target"
# Reject an empty inventory: a successful scan must actually contain packages.
node -e 'const fs=require("node:fs"); const b=JSON.parse(fs.readFileSync(process.argv[1])); if(!b.components?.length) throw Error("Empty SBOM: dependency discovery failed"); console.log(`SBOM contains ${b.components.length} components`)' "$output/sbom.cdx.json"
trivy sbom --format json --output "$output/cve.json" "$output/sbom.cdx.json"
printf 'Commit: %s\nTarget: %s\nScanner: ' "${GITHUB_SHA:-local}" "$target" > "$output/provenance.txt"
trivy --version >> "$output/provenance.txt"
# Include unfixed vulnerabilities. Database/network errors also fail the job.
status=0
trivy sbom --severity HIGH,CRITICAL --exit-code 1 --format table --output "$output/cve-gate.txt" "$output/sbom.cdx.json" || status=$?
cat "$output/cve-gate.txt"
exit "$status"
