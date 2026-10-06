#!/usr/bin/env bash
# Runs the sabotage entries marked `runner: host` (they need docker, which the SDK container job
# lacks), with the harness's rules: in a throwaway worktree at HEAD, the baseline must pass
# unpatched, then with the patch applied the command must exit non-zero with its message.
set -euo pipefail
cd "$(dirname "$0")/.."
field() { sed -n "s/^$1: //p" "$2"; }
test -z "$(git status --porcelain)" || { echo "host-sabotages: refusing to run on an uncommitted tree"; exit 1; }
declare -A passed
n=0; bad=0
for f in sabotage/*/sabotage.txt; do
  [ "$(field runner "$f")" = host ] || continue
  id=$(basename "$(dirname "$f")"); n=$((n + 1))
  cmd=$(field command "$f"); msg=$(field message "$f"); base=$(field baseline "$f"); base=${base:-$cmd}
  wt="$(mktemp -d)/wt"; git worktree add -q --detach "$wt" HEAD
  if [ -z "${passed[$base]:-}" ]; then
    if (cd "$wt" && bash -c "$base") >/tmp/host-sab.out 2>&1; then passed[$base]=1
    else echo "$id: BASELINE FAILS"; tail -5 /tmp/host-sab.out; grep -E '::error::' /tmp/host-sab.out || true; echo "::error::host-sabotages: $id baseline fails: $(tail -3 /tmp/host-sab.out | tr '\n' ' ')"; bad=1
      if [ "$(id -u)" != 0 ]; then docker run --rm -v "$wt:/w" "$(cat ci/image.digest)" chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true; fi
      git worktree remove --force "$wt"; continue; fi
  fi
  if [ -s "sabotage/$id/patch.diff" ] && ! (cd "$wt" && git apply "$OLDPWD/sabotage/$id/patch.diff"); then
    echo "$id: APPLY FAILED"; bad=1
  elif out=$(cd "$wt" && bash -c "$cmd" 2>&1); then echo "$id: SURVIVED (exit 0)"; bad=1
  elif grep -qF -- "$msg" <<<"$out"; then echo "$id: caught"
  else echo "$id: WRONG REASON (no '$msg')"; echo "$out" | tail -5; echo "::error::host-sabotages: $id wrong reason: $(echo "$out" | tail -3 | tr '\n' ' ')"; bad=1; fi
  # A container may have left root-owned files in the worktree (the Compose run's build output); on
  # a non-root runner they stopped its removal (exit 255). Hand them back through the pinned image.
  if [ "$(id -u)" != 0 ]; then docker run --rm -v "$wt:/w" "$(cat ci/image.digest)" chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true; fi
  git worktree remove --force "$wt"
done
[ "$n" -gt 0 ] || { echo "no runner: host sabotages found"; exit 1; }
echo "$n host sabotages, $bad failure(s)"; exit "$bad"
