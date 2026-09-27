#!/usr/bin/env bash
# Every sabotage on every push (breakdown P0-09): the harness refuses a dirty tree, works in its
# own worktree, and fails above its 15-minute ceiling rather than being run less often.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll sabotage
