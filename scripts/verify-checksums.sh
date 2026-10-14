#!/usr/bin/env bash
# Checks that a release SHA256SUMS.txt uses the standard `sha256sum` format
# (lowercase "<hash>  <name>" lines, LF terminators, no BOM), lists every
# required file and matches the files beside it.
# Usage: verify-checksums.sh <release-directory> [required-file...]
set -euo pipefail
directory="${1:?Usage: verify-checksums.sh <release-directory> [required-file...]}"
shift
sums="$directory/SHA256SUMS.txt"
fail() { echo "$sums: $*" >&2; exit 1; }

test -s "$sums" || fail 'missing or empty'
test "$(tr -dc '\r' < "$sums" | wc -c)" -eq 0 || fail 'contains CR line terminators; use LF only'
test "$(head -c 3 "$sums" | od -An -tx1 | tr -d ' \n')" != efbbbf || fail 'starts with a UTF-8 BOM'
test "$(tail -c 1 "$sums" | od -An -tx1 | tr -d ' \n')" = 0a || fail 'last line has no LF terminator'
if LC_ALL=C grep -nvE '^[0-9a-f]{64}  [^ /\\][^/\\]*$' "$sums" >&2; then
  fail 'lines above are not "<lowercase sha256>  <file name>"'
fi
listed="$(cut -c 67- "$sums")"
test -z "$(sort <<<"$listed" | uniq -d)" || fail 'lists a file more than once'
for required in "$@"; do
  grep -Fxq -- "$required" <<<"$listed" || fail "does not list $required"
done
(cd "$directory" && sha256sum --check --strict SHA256SUMS.txt)
