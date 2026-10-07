#!/usr/bin/env bash
# Phase 10's measurements, each a record under measurements/ (P10-01, decision 5): the host built
# in Release inside the CI-pinned SDK image, the configuration read by the tool, and the three
# values a process cannot see (commit, SDK, image) passed in from here. Usage:
#   scripts/bench.sh inputs [repetitions]   P10-02: the data volume's sync, the container's /tmp sync,
#                                           and the loopback round trip, each repeated
#   scripts/bench.sh load-local [reps]      P10-04: the offered-load curve against three hosts in one
#                                           process (the container's /tmp), each rate repeated
#   scripts/bench.sh load-compose [reps]    P10-04: the same curve against the Compose cluster, the
#                                           load from a client container on the clients network
set -euo pipefail
cd "$(dirname "$0")/.."
image="$(cat ci/image.digest)"
what="${1:?usage: bench.sh inputs|load-local|load-compose [repetitions]}"; reps="${2:-5}"
# P10-04's offered rates: from a tenth of the design's capacity (C_design = 6,250 writes a second,
# docs/design/performance-target.md) to its half, the latency criterion's load (3,125), with points
# around the knee a first sweep found between 1,000 and 1,250 (past it the cluster completes almost
# nothing within a run, so higher rates measure the same collapse).
rates="${RAFT_BENCH_RATES:-625 1000 1250 1500 2000 3125}"
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
  load-local)
    for r in $(seq 1 "$reps"); do
      for rate in $rates; do
        run bench load --local /tmp/cluster --rate "$rate" --seconds 10 --warmup 3 --id "p10-04-local-$rate-$r" --repetition "$r" --out measurements
      done
    done ;;
  load-compose)
    export SDK_IMAGE="$image" RAFT_OUT="$PWD/measurements" RAFT_UID="$(id -u)" RAFT_GID="$(id -g)"
    dc() { docker compose -f compose/compose.yaml "$@"; }
    trap 'dc --profile client down -v >/dev/null 2>&1 || true; docker volume rm -f "$volume" >/dev/null 2>&1 || true' EXIT
    dc build n1 >/dev/null
    for r in $(seq 1 "$reps"); do
      for rate in $rates; do
        # A fresh cluster for each run, so one run's backlog is not the next one's start.
        dc --profile client down -v >/dev/null 2>&1 || true
        dc up -d n1 n2 n3 >/dev/null
        for _ in $(seq 60); do
          for i in 1 2 3; do
            case "$(dc run --rm --no-deps -T client request --node "n$i:7100" --line 'Status|' 2>/dev/null || true)" in ok\|Leader\|*) break 2 ;; esac
          done
          sleep 1
        done
        dc --profile client run --rm --no-deps -T -e RAFT_COMMIT="$commit" -e RAFT_IMAGE="$image" -e RAFT_SDK="$sdk" client \
          bench load --nodes 1=n1:7100,2=n2:7100,3=n3:7100 --rate "$rate" --seconds 10 --warmup 3 --id "p10-04-compose-$rate-$r" --repetition "$r" --out /out \
          --data-fs "each node's Docker volume (local driver, $(docker info -f '{{.Driver}}') storage on the host's disk)"
      done
    done ;;
  *) echo "bench: unknown measurement $what"; exit 2 ;;
esac
