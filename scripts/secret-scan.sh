#!/usr/bin/env bash
# Scan the full git history of a repository (default: this one) with the digest-pinned gitleaks
# image in ci/gitleaks.image. Runs on the runner host (it needs docker), not in the SDK container.
# Known limit: gitleaks skips binary files, so e.g. the committed PDF is not scanned.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
repo="$(cd "${1:-$here}" && pwd)"
image="$(cat "$here/ci/gitleaks.image")"
test "$(git -C "$repo" rev-parse --is-shallow-repository)" = false || { echo "secret-scan: $repo is shallow; history would not be scanned"; exit 1; }
docker run --rm -v "$repo:/repo" "$image" git /repo --redact --no-banner --log-level warn --exit-code 1 \
  && echo "secret-scan: no leaks found in $(git -C "$repo" rev-list --count HEAD) commits" \
  || { rc=$?; echo "secret-scan: leaks found (exit $rc)"; exit "$rc"; }
