#!/usr/bin/env bash
# Scan every reachable commit for secrets with Gitleaks. Never suppress scanner failures.
# --diff-merges=remerge also scans what a merge commit changed relative to the automatic merge,
# so a secret added while resolving a conflict is found; Gitleaks' default log omits merge patches.
set -euo pipefail
target="${1:?Expected repository path}"
gitleaks git --redact --no-banner --exit-code 1 \
  --log-opts="--all --full-history --diff-merges=remerge" "$target"
