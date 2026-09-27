#!/usr/bin/env bash
# Build and fast-test every non-head commit of the push (the head gets the full run in `build`).
set -euo pipefail
cd "$(dirname "$0")/.."
git config --global --add safe.directory "$PWD"
dotnet build tools/Raft.Gates -warnaserror -nologo -v:q
dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll each-commit --since "${PUSH_BEFORE:-}"
