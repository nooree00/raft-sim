#!/usr/bin/env bash
# The required `build` check: build-core and one passing job per harness shard of the head, the shard
# list recomputed from the manifest (needs GITHUB_TOKEN, GITHUB_REPOSITORY, GITHUB_RUN_ID, and
# BUILD_CORE_RESULT from the workflow).
set -euo pipefail
cd "$(dirname "$0")/.."
git config --global --add safe.directory "$PWD"
dotnet build tools/Raft.Gates -warnaserror -nologo -v:q
dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll build-collect
