#!/usr/bin/env bash
# P12-08 (phase 12 decision 7): both sides of every host bench control, over RUNS runs alone and RUNS
# beside three busy processes (the sabotage harness runs four workers on four processors, and two
# of this phase's control failures happened only there), as rows for `gates margins`. Unpatched: the
# whole BenchControlTests class; patched: each control's sabotage applied to the tree, built, its
# target run, the patch reverted. Each test writes its report before it asserts, so a failing run
# still gives its values. The bounds are the tests' own, copied here: a test whose bound changes
# changes this file in the same commit.
# Usage: scripts/in-sdk.sh scripts/margins.sh [runs] [out]
set -euo pipefail
cd "$(dirname "$0")/.."
runs="${1:-5}"; out="${2:-docs/phases/P12/margins-bench.tsv}"
test -z "$(git status --porcelain -- src tests)" || { echo "margins: src or tests has changes"; exit 1; }
bin=tests/Raft.Host.Slow.Tests/bin/Debug/net10.0
declare -A vals
add() { vals["$1|$2"]="${vals["$1|$2"]:+${vals["$1|$2"]},}$3"; }
busy() { for _ in 1 2 3; do (timeout 900 sh -c 'while :; do :; done' &); done; }
idle() { pkill -f 'while :; do :; done' || true; sleep 1; }
build() { dotnet build tests/Raft.Host.Slow.Tests -nologo -v:q >/dev/null; }
test_run() { dotnet test --project tests/Raft.Host.Slow.Tests --no-build "$@" >/dev/null 2>&1 || true; }
pick() { grep -oP "$1" "$bin/$2.txt" | head -1; }
ratio() { awk -v a="$1" -v b="$2" 'BEGIN { printf "%.4f", (b == 0 ? 0 : a / b) }'; }

# One report's values, for the rows its control has on this side.
read_stall() {
  add stall-open-p97 "$1" "$(pick 'p97 \K[0-9.]+' bench-stall)"
  [ "$1" = u ] && add stall-closed-p95 u "$(pick 'closed: .*p95 \K[0-9.]+' bench-stall)"
  return 0
}
read_slowdown() {
  local r; r="$(pick 'us, \K[-0-9.E]+(?= times the)' bench-slowdown)"
  add slowdown-low "$1" "$r"; [ "$1" = u ] && add slowdown-high u "$r"
  return 0
}
read_barrier() {
  local lists writes per slept
  lists="$(pick '^\K[0-9]+(?= barrier lists)' bench-barrier)"; writes="$(pick 'lists for \K[0-9]+' bench-barrier)"
  per="$(pick 'writes, \K[0-9.]+(?= us each)' bench-barrier)"; slept="$(pick 'slept \K[0-9.]+' bench-barrier)"
  add barrier-lists "$1" "$(ratio "$lists" "$writes")"
  [ "$1" = u ] && { add barrier-per-list-low u "$(ratio "$per" "$slept")"; add barrier-per-list-high u "$(ratio "$per" "$slept")"; }
  return 0
}
read_lateness() {
  add lateness-early "$1" "$(pick '[0-9]+(?= early)' bench-lateness)"
  [ "$1" = u ] && add lateness-median u "$(pick 'p50 \K[0-9.]+' bench-lateness)"
  return 0
}
read_handoff() {
  local spun queue others
  spun="$(pick '\K[0-9.]+(?= us spun)' bench-handoff)"
  queue="$(pick 'follower-queue \K[-0-9.]+' bench-handoff)"
  # The largest rise among the host segments other than follower-queue (the generator's two are before any host).
  others="$(grep -oP 'median rise: \K.*' "$bin/bench-handoff.txt" | tr ',' '\n' | awk '$1 != "follower-queue" && $1 != "generator-late" && $1 != "connection-wait" { if ($2 > m) m = $2 } END { print m + 0 }')"
  add handoff-low "$1" "$(ratio "$queue" "$spun")"; add handoff-others "$1" "$(ratio "$others" "$spun")"
  [ "$1" = u ] && add handoff-high u "$(ratio "$queue" "$spun")"
  return 0
}

# Unpatched: the whole class, alone and beside three busy processes.
build
for mode in alone busy; do
  [ "$mode" = busy ] && busy
  for r in $(seq 1 "$runs"); do
    test_run --filter-class '*BenchControlTests'
    read_stall u; read_slowdown u; read_barrier u; read_lateness u; read_handoff u
  done
  idle
done

# Patched: each control's sabotage, its target alone and beside three busy processes.
for pair in S-bench-1:APlantedStallShowsOpenLoopAndHidesClosedLoop:stall S-bench-2:APlantedSlowdownMovesTheMedianByTheModelsAmount:slowdown \
            S-bench-3:TheBarrierMeasuredIsTheLeadersOwnSync:barrier S-bench-4:TheGeneratorDispatchesEachWriteAtItsTime:lateness \
            S-lat-1:APlantedHandOffDelayAppearsInItsSegmentOnly:handoff; do
  IFS=: read -r id method control <<< "$pair"
  git apply "sabotage/$id/patch.diff"
  build
  for mode in alone busy; do
    [ "$mode" = busy ] && busy
    for r in $(seq 1 "$runs"); do
      test_run --filter-method "*$method"
      "read_$control" p
    done
    idle
  done
  git apply -R "sabotage/$id/patch.diff"
done
build

# The replication cost check: seeded, so one run is every run; each side read from the report its test
# writes. Patched: S-repl-11 (the resends return) for the bound, S-cost-1 (the reading counts the
# followers' sends) for the committed count.
sbin=tests/Raft.Scale.Tests/bin/Debug/net10.0
cost_run() {
  dotnet build tests/Raft.Scale.Tests -nologo -v:q >/dev/null
  dotnet test --project tests/Raft.Scale.Tests --no-build --filter-method '*TheLeaderSendsEachFollowerABoundedNumberOfEntriesPerCommittedEntry' >/dev/null 2>&1 || true
}
cost_pick() { grep -oP "$1" "$sbin/replication-cost-$2.txt" | head -1; }
cost_run
for n in 8 32; do
  add "cost-ratio-$n" u "$(cost_pick 'worst \K[0-9.]+' "$n")"; add "cost-backlog-$n" u "$(cost_pick 'backlog \K[0-9]+' "$n")"; add "cost-committed-$n" u "$(cost_pick 'committed \K[0-9]+' "$n")"
done
for pair in S-repl-11:ratio S-cost-1:committed; do
  IFS=: read -r id what <<< "$pair"
  git apply "sabotage/$id/patch.diff"; rm -f "$sbin"/replication-cost-*.txt; cost_run
  for n in 8 32; do
    case "$what" in
      ratio) add "cost-ratio-$n" p "$(cost_pick 'worst \K[0-9.]+' "$n")" ;;
      committed) add "cost-committed-$n" p "$(cost_pick 'committed \K[0-9]+' "$n")" ;;
    esac
  done
  git apply -R "sabotage/$id/patch.diff"
done
cost_run

row() { # check bound above|below timing|ratio note
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "${vals["$1|u"]:--}" "${vals["$1|p"]:--}" "$5"
}
{
  echo "# P12-08: the host bench controls, $runs runs alone and $runs beside three busy processes on each side; the replication cost check, seeded, one run a side; at $(git rev-parse --short HEAD)."
  echo "# check	bound	pass	kind	unpatched	patched	note"
  row stall-open-p97 150000 above timing "open loop's 97th percentile, us; S-bench-1"
  row stall-closed-p95 75000 below timing "closed loop's 95th percentile, us"
  row slowdown-low 1.5 above ratio "median rise over the sleep actually slept; S-bench-2"
  row slowdown-high 5.0 below ratio "the same, its upper bound"
  row barrier-lists 1.0 above ratio "barrier effect lists per write answered; S-bench-3"
  row barrier-per-list-low 0.9 above ratio "barrier per list over the sleep slept"
  row barrier-per-list-high 2.5 below ratio "the same, its upper bound"
  row lateness-early 0 below ratio "writes dispatched before their time; S-bench-4"
  row lateness-median 5000 below timing "the generator's median lateness, us"
  row handoff-low 0.5 above ratio "follower-queue's median rise over the delay spun; S-lat-1"
  row handoff-high 2.0 below ratio "the same, its upper bound"
  row handoff-others 0.25 below ratio "the largest other host segment's rise over the delay spun; S-lat-1"
  row cost-ratio-8 2 below ratio "entries sent a follower per committed entry, 8 in flight; S-repl-11"
  row cost-ratio-32 2 below ratio "the same, 32 in flight; S-repl-11"
  row cost-committed-8 100 above ratio "entries committed, the reading's guard, 8 in flight; S-cost-1"
  row cost-committed-32 100 above ratio "the same, 32 in flight; S-cost-1"
  row cost-backlog-8 4 above ratio "the largest backlog, the vacuity guard, 8 in flight (S-cost-2 is caught on a run built idle)"
  row cost-backlog-32 16 above ratio "the same, 32 in flight"
} > "$out"
cat "$out"
