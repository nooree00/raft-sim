#!/usr/bin/env bash
# Build only the gates tool, then check the environment and pins before any other result is believed.
set -euo pipefail
cd "$(dirname "$0")/.."
# In a container job the checkout is owned by the runner's uid, not the container's root, and
# actions/checkout marks it safe only under a temporary HOME; later steps see "dubious ownership".
git config --global --add safe.directory "$PWD"
dotnet build tools/Raft.Gates -warnaserror -nologo -v:q
dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll preflight "$@"
