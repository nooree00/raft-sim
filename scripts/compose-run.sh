#!/usr/bin/env bash
# P9-08, spec §11 phase 9's done criterion: three nodes in Compose, the real client's load, the
# leader killed with SIGKILL and restarted from its volume, and the checker on the history.
#   up -> wait for a leader -> the client's load -> kill the leader -> wait for another -> restart
#   the killed node -> the load ends -> collect the history and the events -> tools/Raft.Check
# The check's guards say the run tested a leader's death (phase 9's P9-05 guards). What it does not
# test: durability (a process kill keeps the page cache; phase 9 decision 7).
# Needs Docker on the host; the check runs in the pinned SDK image (scripts/in-sdk.sh).
set -euo pipefail
cd "$(dirname "$0")/.."
export SDK_IMAGE="$(cat ci/image.digest)"
export RAFT_OUT="${RAFT_OUT:-$PWD/compose-out}"
seconds="${RAFT_COMPOSE_SECONDS:-40}"
rm -rf "$RAFT_OUT" && mkdir -p "$RAFT_OUT"
dc() { docker compose -f compose/compose.yaml "$@"; }
cleanup() {
  dc logs --no-log-prefix n1 n2 n3 > "$RAFT_OUT/events.jsonl" 2>/dev/null || true
  docker rm -f raft-client >/dev/null 2>&1 || true
  dc --profile client down -v >/dev/null 2>&1 || true
}
trap cleanup EXIT

status() { dc run --rm --no-deps -T client request --node "n$1:7100" --line 'Status|' 2>/dev/null || true; }
# "<id> <status>" of the node reporting itself leader, other than $1 if given.
leader() {
  for i in 1 2 3; do
    [ "$i" = "${1:-}" ] && continue
    s="$(status "$i")"
    case "$s" in ok\|Leader\|*) echo "$i $s"; return 0 ;; esac
  done
  return 1
}
wait_leader() {
  for _ in $(seq 60); do
    if l="$(leader "${1:-}")"; then echo "$l"; return 0; fi
    sleep 1
  done
  echo "compose-run: no leader within 60 s" >&2
  return 1
}

echo "compose-run: build"; dc build n1
echo "compose-run: up"; dc up -d n1 n2 n3
wait_leader > /dev/null
echo "compose-run: the client's load for $seconds s"
dc --profile client run -d --name raft-client --no-deps client client --nodes 1=n1:7100,2=n2:7100,3=n3:7100 --clients 3 --seconds "$seconds" --timeout-ms 1000 --history /out/history.jsonl > /dev/null
sleep 10
read -r victim vstatus < <(wait_leader)
killed="$(date +%s%3N)"
dc kill -s SIGKILL "n$victim"
echo "n$victim $vstatus $killed" > "$RAFT_OUT/kill.txt"
echo "compose-run: killed n$victim ($vstatus)"
read -r next nstatus < <(wait_leader "$victim")
echo "compose-run: n$next leads ($nstatus)"
sleep 5
dc start "n$victim"
echo "compose-run: restarted n$victim; waiting for the load to end"
docker wait raft-client > /dev/null
dc logs --no-log-prefix n1 n2 n3 > "$RAFT_OUT/events.jsonl"
echo "compose-run: check"
scripts/in-sdk.sh dotnet run --project tools/Raft.Check -- "${RAFT_OUT#"$PWD"/}"
