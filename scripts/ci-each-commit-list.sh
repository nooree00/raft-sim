#!/usr/bin/env bash
# The push's non-head commits, as a JSON array in $GITHUB_OUTPUT, for the each-commit matrix.
set -euo pipefail
cd "$(dirname "$0")/.."
git config --global --add safe.directory "$PWD"
dotnet build tools/Raft.Gates -warnaserror -nologo -v:q
dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll each-commit-list --since "${PUSH_BEFORE:-}"
