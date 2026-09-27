#!/usr/bin/env bash
# Runs the sabotage entries marked `runner: host` (they need docker, which the SDK container job
# lacks), with the harness's rules: the baseline must pass, the command must exit non-zero with
# its expected message. Host entries carry an empty patch; the sabotage is in the command.
set -euo pipefail
cd "$(dirname "$0")/.."
field() { sed -n "s/^$1: //p" "$2"; }
n=0; bad=0
for f in sabotage/*/sabotage.txt; do
  [ "$(field runner "$f")" = host ] || continue
  id=$(basename "$(dirname "$f")"); n=$((n + 1))
  [ ! -s "sabotage/$id/patch.diff" ] || { echo "$id: host entries must have an empty patch"; bad=1; continue; }
  base=$(field baseline "$f"); cmd=$(field command "$f"); msg=$(field message "$f")
  if ! bash -c "$base" >/tmp/host-sab.out 2>&1; then echo "$id: BASELINE FAILS"; tail -5 /tmp/host-sab.out; bad=1; continue; fi
  if out=$(bash -c "$cmd" 2>&1); then echo "$id: SURVIVED (exit 0)"; bad=1
  elif grep -qF -- "$msg" <<<"$out"; then echo "$id: caught"
  else echo "$id: WRONG REASON (no '$msg')"; echo "$out" | tail -5; bad=1; fi
done
[ "$n" -gt 0 ] || { echo "no runner: host sabotages found"; exit 1; }
echo "$n host sabotages, $bad failure(s)"; exit "$bad"
