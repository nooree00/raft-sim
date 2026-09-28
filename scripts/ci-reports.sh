#!/usr/bin/env bash
# Each phase report's certifying commit must have a green ci run (needs GITHUB_TOKEN with
# actions: read, and GITHUB_REPOSITORY; both set by the workflow).
set -euo pipefail
cd "$(dirname "$0")/.."
git config --global --add safe.directory "$PWD" # the checkout may belong to another user (CI containers)
dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll reports
