#!/usr/bin/env bash
# Self-test for scan-secrets.sh: a synthetic key added only in a conflict resolution must fail the scan.
set -euo pipefail
repo="$(mktemp -d)"
trap 'rm -rf "$repo"' EXIT
script="$(cd "$(dirname "$0")" && pwd)/scan-secrets.sh"
cd "$repo"
git init -q -b main
git config user.email ci@example.invalid
git config user.name ci
echo base > f.txt; git add f.txt; git commit -qm base
git checkout -qb side; echo side > f.txt; git commit -qam side
git checkout -q main; echo main > f.txt; git commit -qam main
git merge -q side >/dev/null 2>&1 || true
# Built at runtime so this repository never contains the synthetic key itself.
printf 'resolved\nkey=%s%s\n' AKIA Z7Q4XN2LPW6RBV3T > f.txt
git add f.txt; git commit -qm merge
# Require an actual finding: a missing scanner or a scanner error must not pass as detection.
status=0
output="$(bash "$script" "$repo" 2>&1)" || status=$?
if [ "$status" -ne 1 ] || ! grep -q 'leaks found: 1' <<<"$output"; then
  printf '%s
' "$output" >&2
  echo 'Secret scan did not report the key introduced in a merge resolution' >&2
  exit 1
fi
echo 'Secret scan detects keys introduced in merge resolutions'
