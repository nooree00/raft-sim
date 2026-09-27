#!/usr/bin/env bash
# The required each-commit check: one result per commit in the range (needs GITHUB_TOKEN,
# GITHUB_REPOSITORY, GITHUB_RUN_ID; EACH_COMMIT_LIST and EACH_COMMIT_LIST_RESULT from the list job).
set -euo pipefail
cd "$(dirname "$0")/.."
git config --global --add safe.directory "$PWD"
dotnet build tools/Raft.Gates -warnaserror -nologo -v:q
dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll each-commit-collect --since "${PUSH_BEFORE:-}"
