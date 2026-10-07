#!/usr/bin/env bash
# Phase 10's measurements, each a record under measurements/ (P10-01, decision 5): the host built
# in Release inside the CI-pinned SDK image, the configuration read by the tool, and the three
# values a process cannot see (commit, SDK, image) passed in from here. Usage:
#   scripts/bench.sh inputs [repetitions]   P10-02: the data volume's sync, the container's /tmp sync,
#                                           and the loopback round trip, each repeated
set -euo pipefail
cd "$(dirname "$0")/.."
image="$(cat ci/image.digest)"
what="${1:?usage: bench.sh inputs [repetitions]}"; reps="${2:-5}"
commit="$(git rev-parse HEAD)"
test -z "$(git status --porcelain -- src)" || { echo "bench: src has uncommitted changes; a record names the commit it measured"; exit 1; }
volume="raft-bench-$$"; trap 'docker volume rm -f "$volume" >/dev/null 2>&1 || true' EXIT
# Built once, in Release, through the same container as every other build (restore needs the
# network); each measurement then runs with no network at all, beside nothing else of ours.
scripts/in-sdk.sh dotnet build src/Raft.Host -c Release -nologo -v:q >/dev/null
sdk="$(scripts/in-sdk.sh dotnet --version | tail -1)"
run() {
  docker run --rm --network none -v "$PWD:/src" -v "$volume:/data" -w /src -e RAFT_COMMIT="$commit" -e RAFT_IMAGE="$image" \
    -e RAFT_SDK="$sdk" -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 "$image" dotnet src/Raft.Host/bin/Release/net10.0/Raft.Host.dll "$@"
}
case "$what" in
  inputs)
    for r in $(seq 1 "$reps"); do
      run bench sync --dir /data/bench --count 10000 --id "p10-02-sync-volume-$r" --repetition "$r" --out measurements
      run bench sync --dir /tmp/bench --count 10000 --id "p10-02-sync-tmp-$r" --repetition "$r" --out measurements
      run bench rtt --count 10000 --id "p10-02-rtt-$r" --repetition "$r" --out measurements
    done ;;
  *) echo "bench: unknown measurement $what"; exit 2 ;;
esac
