#!/usr/bin/env bash
# Scan the full git history of a repository (default: this one) with the digest-pinned gitleaks
# image in ci/gitleaks.image. Runs on the runner host (it needs docker), not in the SDK container.
# Known limit: gitleaks skips binary files, so e.g. the committed PDF is not scanned.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
repo="$(cd "${1:-$here}" && pwd)"
image="$(cat "$here/ci/gitleaks.image")"
test "$(git -C "$repo" rev-parse --is-shallow-repository)" = false || { echo "secret-scan: $repo is shallow; history would not be scanned"; exit 1; }
# Three outcomes, never two. gitleaks exits 3 on leaks (--exit-code 3), 0 when clean; anything else
# means the scan did not happen: the daemon unreachable exits 1, as a gitleaks error does, a bad flag
# 126, a missing docker 127 (measured). Exit 1 is therefore never read as a finding: a connection
# failure once printed "leaks found" (docs/findings.md). No verdict exits 2 and says so.
leaks=3
rc=0
docker run --rm -v "$repo:/repo" "$image" git /repo --redact --no-banner --log-level warn --exit-code "$leaks" || rc=$?
case "$rc" in
  0) echo "secret-scan: no leaks found in $(git -C "$repo" rev-list --count HEAD) commits" ;;
  "$leaks") echo "secret-scan: leaks found"; exit 1 ;;
  *) echo "secret-scan: NO VERDICT: the scan did not run (docker/gitleaks exit $rc); this is not a finding and not a pass"; exit 2 ;;
esac
