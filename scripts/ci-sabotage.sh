#!/usr/bin/env bash
# Every sabotage on every push (breakdown P0-09): the harness refuses a dirty tree, works in its
# own worktree, and fails above its 20-minute ceiling (15 until phase 1; docs/register.md) rather than being run less often.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll sabotage
