#!/usr/bin/env bash
# Every sabotage on every push (breakdown P0-09, sharded in P2-01): the harness refuses a dirty tree,
# works in its own worktrees, and fails above its 15-minute ceiling per shard rather than being run
# less often. With SABOTAGE_SHARD=i/n (a CI shard job) it runs that shard; without it (locally) it
# runs every shard of the plan in turn, each under its own ceiling, exactly as CI splits them.
set -euo pipefail
cd "$(dirname "$0")/.."
git config --global --add safe.directory "$PWD" # the checkout may belong to another user (CI containers)
gates=tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll
if [ -n "${SABOTAGE_SHARD:-}" ]; then
  exec dotnet "$gates" sabotage --shard "$SABOTAGE_SHARD"
fi
n="$(dotnet "$gates" sabotage-plan | sed -n 's/.*count=\([0-9][0-9]*\).*/\1/p')"
if [ -z "$n" ]; then echo "sabotage-plan gave no shard count" >&2; exit 1; fi
rc=0
for i in $(seq 1 "$n"); do
  dotnet "$gates" sabotage --shard "$i/$n" || rc=1
done
exit "$rc"
