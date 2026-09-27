#!/usr/bin/env bash
# Run a command inside the pinned SDK image (ci/image.digest) with this repository at /src.
# The only host prerequisite is Docker. Example: scripts/in-sdk.sh scripts/ci-build.sh
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
image="$(cat "$here/ci/image.digest")"
# Behind an HTTPS proxy (with its own CA), pass both through so restore can reach NuGet.
net=()
if [ -n "${HTTPS_PROXY:-}" ]; then
  net+=(--network host -e HTTPS_PROXY -e HTTP_PROXY -e NO_PROXY)
  if [ -n "${SSL_CERT_FILE:-}" ]; then net+=(-v "$SSL_CERT_FILE:/etc/ssl/certs/proxy-ca.pem:ro" -e SSL_CERT_FILE=/etc/ssl/certs/proxy-ca.pem); fi
fi
# Each call is a fresh container, so the NuGet cache lives in a named volume; without it, a test
# step after a build step finds no packages, and `dotnet test` reports "No test projects were
# found" and exits 0 (found by the README walk).
cache="${RAFT_NUGET_VOLUME:-raft-sim-nuget}"
docker run --rm "${net[@]}" -v "$cache:/root/.nuget/packages" -v "$here:/src" -w /src -e CI=true -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  "$image" bash -c 'git config --global --add safe.directory /src && "$@"' in-sdk "$@"
