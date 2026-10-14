#!/usr/bin/env bash
# Phase 10's measurements, each a record under measurements/ (P10-01, decision 5): the host built
# in Release inside the CI-pinned SDK image, the configuration read by the tool, and the three
# values a process cannot see (commit, SDK, image) passed in from here. Usage:
#   scripts/bench.sh inputs [repetitions]   P10-02: the data volume's sync, the container's /tmp sync,
#                                           and the loopback round trip, each repeated
#   scripts/bench.sh floors [reps]          P12-06: the floors the decomposition's segments are read
#                                           against: the sync with 2 and 3 writers at once, a thread
#                                           hand-off to a blocked loop, a one-way loopback hop
#   scripts/bench.sh compose-hop [reps]     P13-06: Compose's hop floor, a frame from one container to a
#                                           reader asleep in another, on a bridge network as Compose's
#   scripts/bench.sh allowance              P13-08: the allowance's model (a computation, no load): the
#                                           service time from 50% low to 50% high at 3,125 a second, the
#                                           measured service, evenly spaced arrivals, and the rates the
#                                           measured service keeps the sustained-rate definition to
#   scripts/bench.sh load-local [reps]      P10-04: the offered-load curve against three hosts in one
#                                           process (the container's /tmp), each rate repeated
#   scripts/bench.sh load-compose [reps]    P10-04: the same curve against the Compose cluster, the
#                                           load from a client container on the clients network
#   scripts/bench.sh barrier [reps]         P10-05: the leader's persist barrier against its commit
#                                           latency, in process, at rates below the knee
#   scripts/bench.sh soak-ab [reps]         P10-06: the same 1,000 soak executions at this commit and at
#                                           phase 8's head (before the last-use set), interleaved
#   scripts/bench.sh compose-ab [reps]      P12-04: load generators built at RAFT_BENCH_AB_COMMITS (this
#                                           commit's nodes), interleaved in Compose at RAFT_BENCH_RATES
#   scripts/bench.sh trace-ab [reps]        P12-03: the hand-off trace's own cost, in process, traced and
#                                           untraced runs interleaved at RAFT_BENCH_RATES
#   scripts/bench.sh compose-node-ab [reps] P13-05: the nodes built at each of RAFT_BENCH_AB_COMMITS, the
#                                           load from this commit's generator, interleaved in Compose at
#                                           RAFT_BENCH_RATES
#   scripts/bench.sh load-ab [reps]         P11-05: the in-process curve at this commit and at phase
#                                           10's head (before the resend fix), interleaved, at
#                                           RAFT_BENCH_AB_RATES
# The task a record names, and its id's prefix, are phase 10's unless RAFT_BENCH_INPUTS_TASK or
# RAFT_BENCH_LOAD_TASK names another (P11-04, P11-05 measure again with the same tool).
# RAFT_BENCH_DECOMPOSE=1 makes load-local and load-compose trace each write's hand-offs and record
# where its latency went (P12-03); the Compose nodes then run with RAFT_HANDOFF_TRACE=1.
# RAFT_BENCH_ID_TAG adds a tag after a record id's task prefix, for a second set under one task
# (P12-04: the generator's second schedule, `paced`).
# RAFT_BENCH_SEPARATE=1 makes load-local and load-ab run the in-process cluster in a process of its own
# (`bench cluster`, a container of its own) and the load generator in another sharing its loopback
# (P15-01); without it the generator shares the hosts' process, as before. RAFT_BENCH_TRACE_SAMPLE=N makes
# a traced run keep one write in N, by entry index (P15-01's contingency: the full trace's own cost).
#   scripts/bench.sh instrument-ab [reps]   P15-01: the generator in the hosts' process and in its own,
#                                           each untraced and traced, interleaved at RAFT_BENCH_RATES
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
inputs_task="${RAFT_BENCH_INPUTS_TASK:-P10-02}"; load_task="${RAFT_BENCH_LOAD_TASK:-P10-04}"
lc() { echo "$1" | tr 'A-Z' 'a-z'; }
tag="${RAFT_BENCH_ID_TAG:+-$RAFT_BENCH_ID_TAG}"
decompose=(); [ "${RAFT_BENCH_DECOMPOSE:-0}" = 1 ] && decompose=(--decompose 1)
# P14-04: the tail criterion the decomposition's tail rule reads the writes over (the run's own 99th percentile when unset).
[ -n "${RAFT_BENCH_TAIL_OVER:-}" ] && decompose+=(--tail-over "$RAFT_BENCH_TAIL_OVER")
test -z "$(git status --porcelain -- src)" || { echo "bench: src has uncommitted changes; a record names the commit it measured"; exit 1; }
volume="raft-bench-$$"; trap 'docker volume rm -f "$volume" >/dev/null 2>&1 || true' EXIT
# Built once, in Release, through the same container as every other build (restore needs the
# network); each measurement then runs with no network at all, beside nothing else of ours.
scripts/in-sdk.sh dotnet build src/Raft.Host -c Release -nologo -v:q >/dev/null
sdk="$(scripts/in-sdk.sh dotnet --version | tail -1)"
run() { run_in "$PWD" "$@"; }
# The host built in <dir> (this tree, or another commit exported beside it), its records written here.
run_in() {
  local dir="$1"; shift
  docker run --rm --network none -v "$dir:/src" -v "$PWD/measurements:/out" -v "$volume:/data" -w /src -e RAFT_COMMIT="${measured:-$commit}" -e RAFT_IMAGE="$image" \
    -e RAFT_SDK="$sdk" -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 "$image" dotnet src/Raft.Host/bin/Release/net10.0/Raft.Host.dll "$@"
}
# P15-01: one in-process load run, the cluster built in <dir> either in the generator's process
# (`--local`) or in a process of its own (RAFT_BENCH_SEPARATE=1, or "separate" as the second argument):
# `bench cluster` in a container of its own, the load generator built here in a second container sharing
# its loopback; the record names the measured commit. The load's own arguments follow.
cluster="raft-cluster-$$"
load_in() {
  local dir="$1" mode="$2"; shift 2
  if [ "$mode" = separate ]; then
    # The cluster traces when the run decomposes, keeping the run's sample (its own --trace-sample, else RAFT_BENCH_TRACE_SAMPLE).
    local traced=() sample="${RAFT_BENCH_TRACE_SAMPLE:-}" prev=""
    for a in "$@"; do [ "$a" = --decompose ] && traced=(--decompose 1); [ "$prev" = --trace-sample ] && sample="$a"; prev="$a"; done
    [ ${#traced[@]} -gt 0 ] && [ -n "$sample" ] && traced+=(--trace-sample "$sample")
    docker rm -f "$cluster" >/dev/null 2>&1 || true
    docker run -d --name "$cluster" --network none -v "$dir:/src" -w /src -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 "$image" \
      dotnet src/Raft.Host/bin/Release/net10.0/Raft.Host.dll bench cluster --local /tmp/cluster --control-port 27299 "${traced[@]}" >/dev/null
    docker run --rm --network "container:$cluster" -v "$PWD:/src" -v "$PWD/measurements:/out" -w /src -e RAFT_COMMIT="${measured:-$commit}" -e RAFT_IMAGE="$image" \
      -e RAFT_SDK="$sdk" -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 "$image" dotnet src/Raft.Host/bin/Release/net10.0/Raft.Host.dll bench load --cluster 127.0.0.1:27299 "$@"
    docker rm -f "$cluster" >/dev/null 2>&1 || true
  else
    local sampled=(); for a in "$@"; do [ "$a" = --decompose ] && [ -n "${RAFT_BENCH_TRACE_SAMPLE:-}" ] && sampled=(--trace-sample "$RAFT_BENCH_TRACE_SAMPLE"); done
    run_in "$dir" bench load --local /tmp/cluster "$@" "${sampled[@]}"
  fi
}
mode="local"; [ "${RAFT_BENCH_SEPARATE:-0}" = 1 ] && mode=separate
trap 'docker rm -f "$cluster" >/dev/null 2>&1 || true; docker volume rm -f "$volume" >/dev/null 2>&1 || true' EXIT
case "$what" in
  inputs)
    for r in $(seq 1 "$reps"); do
      run bench sync --dir /data/bench --count 10000 --task "$inputs_task" --id "$(lc "$inputs_task")-sync-volume-$r" --repetition "$r" --out measurements
      run bench sync --dir /tmp/bench --count 10000 --task "$inputs_task" --id "$(lc "$inputs_task")-sync-tmp-$r" --repetition "$r" --out measurements
      run bench rtt --count 10000 --task "$inputs_task" --id "$(lc "$inputs_task")-rtt-$r" --repetition "$r" --out measurements
    done ;;
  floors)
    for r in $(seq 1 "$reps"); do
      for w in 2 3; do
        run bench sync --dir /data/bench --count 5000 --writers "$w" --task P12-06 --id "p12-06-sync$w-volume-$r" --repetition "$r" --out measurements
        run bench sync --dir /tmp/bench --count 5000 --writers "$w" --task P12-06 --id "p12-06-sync$w-tmp-$r" --repetition "$r" --out measurements
      done
      run bench handoff --count 10000 --task P12-06 --id "p12-06-handoff-$r" --repetition "$r" --out measurements
      run bench hop --count 10000 --task P12-06 --id "p12-06-hop-$r" --repetition "$r" --out measurements
    done ;;
  compose-hop)
    # Two containers of the node image on a bridge network of their own (Compose's driver), the reader
    # started first and writing the record, the writer refusing a reader at its own address.
    export SDK_IMAGE="$image" RAFT_OUT="$PWD/measurements" RAFT_UID="$(id -u)" RAFT_GID="$(id -g)"
    docker compose -f compose/compose.yaml build n1 >/dev/null
    net="raft-hop-$$"; docker network create "$net" >/dev/null
    trap 'docker rm -f raft-hop-reader >/dev/null 2>&1 || true; docker network rm "$net" >/dev/null 2>&1 || true; docker volume rm -f "$volume" >/dev/null 2>&1 || true' EXIT
    for r in $(seq 1 "$reps"); do
      docker run -d --name raft-hop-reader --hostname raft-hop-reader --network "$net" --user "$(id -u):$(id -g)" -v "$PWD/measurements:/out" \
        -e RAFT_COMMIT="$commit" -e RAFT_IMAGE="$image" -e RAFT_SDK="$sdk" raft-host:local \
        bench hop-reader --port 7300 --count 10000 --writer raft-hop-writer --task P13-06 --id "p13-06-hop-compose-$r" --repetition "$r" --out /out >/dev/null
      sleep 2
      docker run --rm --name raft-hop-writer --hostname raft-hop-writer --network "$net" raft-host:local bench hop-writer --to raft-hop-reader:7300 --count 10000 --bytes 96
      docker wait raft-hop-reader >/dev/null; docker logs raft-hop-reader | tail -1; docker rm raft-hop-reader >/dev/null
    done ;;
  allowance)
    # Each point a record: "rate service arrivals id-suffix". The service times at 3,125 and above are
    # P13-05's, the leader's execution of its effects per write at that rate (m:p13-05-ab-fix-*).
    while read -r rate service arrivals name; do
      run bench allowance --rate "$rate" --service-us "$service" --arrivals "$arrivals" --count 4000000 --seed 12 --task P13-08 --id "p13-08-allowance-$name" --out measurements
    done <<'POINTS'
3125 106.2 poisson 3125-d0.5
3125 159.3 poisson 3125-d0.75
3125 191.2 poisson 3125-d0.9
3125 212.4 poisson 3125-d1.0
3125 233.6 poisson 3125-d1.1
3125 265.5 poisson 3125-d1.25
3125 318.6 poisson 3125-d1.5
3125 304 poisson 3125-measured
3125 304 even 3125-measured-even
3500 280 poisson 3500-measured
4000 248 poisson 4000-measured
POINTS
    ;;
  load-local)
    for r in $(seq 1 "$reps"); do
      for rate in $rates; do
        load_in "$PWD" "$mode" --rate "$rate" --seconds 10 --warmup 3 --task "$load_task" --id "$(lc "$load_task")$tag-local-$rate-$r" --repetition "$r" --out /out "${decompose[@]}"
      done
    done ;;
  compose-ab)
    # Each commit's load generator in an image of its own, against nodes built from this commit, a
    # fresh cluster for every run, the commits interleaved within each repetition.
    export SDK_IMAGE="$image" RAFT_OUT="$PWD/measurements" RAFT_UID="$(id -u)" RAFT_GID="$(id -g)" RAFT_BENCH_ROLES=1 RAFT_HANDOFF_TRACE=0
    dc() { docker compose -f compose/compose.yaml "$@"; }
    trap 'dc --profile client down -v >/dev/null 2>&1 || true; docker volume rm -f "$volume" >/dev/null 2>&1 || true' EXIT
    dc build n1 >/dev/null
    commits="${RAFT_BENCH_AB_COMMITS:?compose-ab needs RAFT_BENCH_AB_COMMITS}"
    for c in $commits; do
      at="$(git rev-parse --short "$c")"; tree="$(mktemp -d)/$at"; mkdir -p "$tree"; git archive "$at" | tar -x -C "$tree"
      docker build -q -f "$tree/compose/Dockerfile" --build-arg SDK_IMAGE="$image" -t "raft-host:ab-$at" "$tree" >/dev/null
    done
    for r in $(seq 1 "$reps"); do
      for rate in $rates; do
        for c in $commits; do
          at="$(git rev-parse --short "$c")"
          dc --profile client down -v >/dev/null 2>&1 || true
          dc up -d n1 n2 n3 >/dev/null
          for _ in $(seq 60); do
            for i in 1 2 3; do
              case "$(dc run --rm --no-deps -T client request --node "n$i:7100" --line 'Status|' 2>/dev/null || true)" in ok\|Leader\|*) break 2 ;; esac
            done
            sleep 1
          done
          docker run --rm --network raft_clients --user "$(id -u):$(id -g)" -v "$PWD/measurements:/out" -e RAFT_COMMIT="$(git rev-parse "$c")" -e RAFT_IMAGE="$image" -e RAFT_SDK="$sdk" "raft-host:ab-$at" \
            bench load --nodes 1=n1:7100,2=n2:7100,3=n3:7100 --rate "$rate" --seconds 10 --warmup 3 --task "$load_task" --id "$(lc "$load_task")$tag-compose-ab-$at-$rate-$r" --repetition "$r" --out /out \
            --data-fs "each node's Docker volume (local driver, $(docker info -f '{{.Driver}}') storage on the host's disk); nodes at $commit, this load generator at $at"
        done
      done
    done ;;
  compose-node-ab)
    # The nodes at each commit, in an image of its own tagged as the cluster's for its runs; the load
    # from this commit's generator in every run; a fresh cluster each time, the commits interleaved.
    export SDK_IMAGE="$image" RAFT_OUT="$PWD/measurements" RAFT_UID="$(id -u)" RAFT_GID="$(id -g)" RAFT_BENCH_ROLES=1 RAFT_HANDOFF_TRACE=0
    dc() { docker compose -f compose/compose.yaml "$@"; }
    gen="raft-host:ab-gen-$(git rev-parse --short HEAD)"
    trap 'dc --profile client down -v >/dev/null 2>&1 || true; docker tag "$gen" raft-host:local >/dev/null 2>&1 || true; docker volume rm -f "$volume" >/dev/null 2>&1 || true' EXIT
    dc build n1 >/dev/null
    docker tag raft-host:local "$gen"
    commits="${RAFT_BENCH_AB_COMMITS:?compose-node-ab needs RAFT_BENCH_AB_COMMITS}"
    for c in $commits; do
      at="$(git rev-parse --short "$c")"; tree="$(mktemp -d)/$at"; mkdir -p "$tree"; git archive "$at" | tar -x -C "$tree"
      docker build -q -f "$tree/compose/Dockerfile" --build-arg SDK_IMAGE="$image" -t "raft-host:ab-$at" "$tree" >/dev/null
    done
    for r in $(seq 1 "$reps"); do
      for rate in $rates; do
        for c in $commits; do
          at="$(git rev-parse --short "$c")"
          dc --profile client down -v >/dev/null 2>&1 || true
          docker tag "raft-host:ab-$at" raft-host:local
          dc up -d --no-build n1 n2 n3 >/dev/null
          for _ in $(seq 60); do
            for i in 1 2 3; do
              case "$(docker run --rm --network raft_clients "$gen" request --node "n$i:7100" --line 'Status|' 2>/dev/null || true)" in ok\|Leader\|*) break 2 ;; esac
            done
            sleep 1
          done
          docker run --rm --network raft_clients --user "$(id -u):$(id -g)" -v "$PWD/measurements:/out" -e RAFT_COMMIT="$(git rev-parse "$c")" -e RAFT_IMAGE="$image" -e RAFT_SDK="$sdk" "$gen" \
            bench load --nodes 1=n1:7100,2=n2:7100,3=n3:7100 --rate "$rate" --seconds 10 --warmup 3 --task "$load_task" --id "$(lc "$load_task")$tag-compose-ab-$at-$rate-$r" --repetition "$r" --out /out \
            --data-fs "each node's Docker volume (local driver, $(docker info -f '{{.Driver}}') storage on the host's disk); nodes at $at, the load generator at $commit"
        done
      done
    done ;;
  trace-ab)
    for r in $(seq 1 "$reps"); do
      for rate in $rates; do
        run bench load --local /tmp/cluster --rate "$rate" --seconds 10 --warmup 3 --task P12-03 --id "p12-03-trace-ab-traced-$rate-$r" --repetition "$r" --out measurements --decompose 1
        run bench load --local /tmp/cluster --rate "$rate" --seconds 10 --warmup 3 --task P12-03 --id "p12-03-trace-ab-untraced-$rate-$r" --repetition "$r" --out measurements
      done
    done ;;
  barrier)
    # Below the knee P10-04 found (past it nothing completes, so a share of latency means nothing).
    for r in $(seq 1 "$reps"); do
      for rate in 300 625; do
        run bench load --local /tmp/cluster --rate "$rate" --seconds 10 --warmup 3 --task P10-05 --id "p10-05-local-$rate-$r" --repetition "$r" --out measurements
      done
    done ;;
  instrument-ab)
    # P15-01: the instrument against itself, interleaved: the generator in the hosts' process and in its
    # own, each untraced, traced, and traced keeping one write in RAFT_BENCH_SAMPLE_N (default 10), at
    # RAFT_BENCH_RATES.
    for r in $(seq 1 "$reps"); do
      for rate in $rates; do
        for m in local separate; do
          for t in untraced traced sampled; do
            tr=(); [ "$t" != untraced ] && tr=(--decompose 1 ${RAFT_BENCH_TAIL_OVER:+--tail-over "$RAFT_BENCH_TAIL_OVER"})
            [ "$t" = sampled ] && tr+=(--trace-sample "${RAFT_BENCH_SAMPLE_N:-10}")
            load_in "$PWD" "$m" --rate "$rate" --seconds 10 --warmup 3 --task P15-01 --id "p15-01-$m-$t-$rate-$r" --repetition "$r" --out /out "${tr[@]}"
          done
        done
      done
    done ;;
  load-ab)
    # Phase 10's head, exported (this tree stays as it is), both hosts built in Release.
    # The task is P11-05's unless RAFT_BENCH_AB_TASK names another (P12-04: the generator, against P12-03's head).
    ab_task="${RAFT_BENCH_AB_TASK:-P11-05}"
    base="${RAFT_BENCH_AB_BASE:-58bed2b}"; old="$(mktemp -d)/base"; mkdir -p "$old"; git archive "$base" | tar -x -C "$old"
    docker run --rm -v "$old:/src" -v "${RAFT_NUGET_VOLUME:-raft-sim-nuget}:/root/.nuget/packages" -w /src ${HTTPS_PROXY:+--network host -e HTTPS_PROXY -e HTTP_PROXY -e NO_PROXY} \
      ${SSL_CERT_FILE:+-v "$SSL_CERT_FILE:/etc/ssl/certs/proxy-ca.pem:ro" -e SSL_CERT_FILE=/etc/ssl/certs/proxy-ca.pem} "$image" \
      dotnet build src/Raft.Host -c Release -nologo -v:q >/dev/null
    for r in $(seq 1 "$reps"); do
      for rate in ${RAFT_BENCH_AB_RATES:-625 1250 3125}; do
        for side in fix base; do
          dir="$PWD"; at=$commit; [ "$side" = base ] && { dir="$old"; at=$base; }
          measured=$at load_in "$dir" "$mode" --rate "$rate" --seconds 10 --warmup 3 --task "$ab_task" \
            --id "$(lc "$ab_task")$tag-ab-$side-$rate-$r" --repetition "$r" --out /out "${decompose[@]}"
        done
      done
    done ;;
  soak-ab)
    # Phase 8's head, exported (not checked out: this tree stays as it is), both built in Release.
    p8="$(mktemp -d)/p8"; mkdir -p "$p8"; git archive 6f5001d | tar -x -C "$p8"
    for side in "$PWD" "$p8"; do
      docker run --rm -v "$side:/src" -v "${RAFT_NUGET_VOLUME:-raft-sim-nuget}:/root/.nuget/packages" -w /src ${HTTPS_PROXY:+--network host -e HTTPS_PROXY -e HTTP_PROXY -e NO_PROXY} \
        ${SSL_CERT_FILE:+-v "$SSL_CERT_FILE:/etc/ssl/certs/proxy-ca.pem:ro" -e SSL_CERT_FILE=/etc/ssl/certs/proxy-ca.pem} "$image" \
        dotnet build tests/Raft.Scale.Tests -c Release -nologo -v:q >/dev/null
    done
    for r in $(seq 1 "$reps"); do
      for side in cur p8; do
        dir="$PWD"; [ "$side" = p8 ] && dir="$p8"
        start=$(date +%s.%N)
        # The NuGet cache as for the build: without it, `dotnet test` finds no test projects (in-sdk.sh).
        docker run --rm --network none -v "$dir:/src" -v "${RAFT_NUGET_VOLUME:-raft-sim-nuget}:/root/.nuget/packages" -w /src -e RAFT_SOAK_COUNT=1000 "$image" \
          dotnet test --project tests/Raft.Scale.Tests -c Release --no-build --filter-method "*TheGeneratedSampleHoldsEveryInvariant" >/dev/null
        seconds=$(echo "$(date +%s.%N) - $start" | bc)
        at=$commit; [ "$side" = p8 ] && at=6f5001d
        measured=$at run bench record --task P10-06 --id "p10-06-soak-$side-$r" --repetition "$r" --results "wall_seconds=$seconds" \
          --measure "the wall time of 1,000 soak executions (dotnet test of TheGeneratedSampleHoldsEveryInvariant, test host start included) at $at" \
          --load "1,000 generated executions, Release, one at a time, no network; this side at $at, interleaved with the other"
      done
    done ;;
  load-compose)
    export SDK_IMAGE="$image" RAFT_OUT="$PWD/measurements" RAFT_UID="$(id -u)" RAFT_GID="$(id -g)" RAFT_BENCH_ROLES=1 RAFT_HANDOFF_TRACE="${RAFT_BENCH_DECOMPOSE:-0}"
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
          bench load --nodes 1=n1:7100,2=n2:7100,3=n3:7100 --rate "$rate" --seconds 10 --warmup 3 --task "$load_task" --id "$(lc "$load_task")$tag-compose-$rate-$r" --repetition "$r" --out /out \
          --data-fs "each node's Docker volume (local driver, $(docker info -f '{{.Driver}}') storage on the host's disk)" "${decompose[@]}"
      done
    done ;;
  *) echo "bench: unknown measurement $what"; exit 2 ;;
esac
